using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Schedule;

public enum ScheduleFrequency { Daily, Weekly, Monthly }

public record ScheduleJob(
    string Name,
    ScheduleFrequency Frequency,
    DayOfWeek DayOfWeek,
    int HourOfDay,
    int MinuteOfHour,
    string CliArguments);

public sealed record ScheduledJobInfo(
    string Name,
    string TaskPath,
    string Principal,
    string LogonType,
    string RunLevel,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    bool Enabled,
    bool ActionTargetTrusted,
    bool TaskAclTrusted,
    bool IsLegacyWrapper,
    string Diagnostic)
{
    public bool IsTrusted => ActionTargetTrusted && TaskAclTrusted && !IsLegacyWrapper;
    public bool DeletionSupported => !string.IsNullOrWhiteSpace(Name) &&
        TaskPath.StartsWith(@"\DeepPurge\", StringComparison.OrdinalIgnoreCase);
    public string SecurityStatus => IsLegacyWrapper
        ? "Migration required"
        : !Enabled ? "Protected / disabled"
        : IsTrusted ? "Protected" : "Unsafe / degraded";
}

public sealed record ScheduleMigrationResult(
    string Name,
    bool Migrated,
    string Message);

/// <summary>
/// Creates hardened Task Scheduler 2.0 definitions under <c>\DeepPurge\</c>.
/// Highest-privilege actions point only to a content-addressed CLI copy in an
/// administrator-owned ProgramData directory. The task and folder DACLs grant
/// the desktop user read/execute access but no ability to replace the action.
/// </summary>
public sealed class ScheduleManager
{
    private const string TaskFolder = @"\DeepPurge\";
    private const string SafeMigrationArguments = "clean junk evidence --dry-run";
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly SecurityIdentifier AdministratorsSid = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        domainSid: null);
    private static readonly SecurityIdentifier SystemSid = new(
        WellKnownSidType.LocalSystemSid,
        domainSid: null);

    private readonly ITaskSchedulerBackend _backend;
    private readonly ScheduledExecutableStore _executables;
    private readonly string _principalSid;

    public string LastError { get; private set; } = string.Empty;

    public ScheduleManager()
        : this(
            new WindowsTaskSchedulerBackend(),
            ScheduledExecutableStore.Production,
            UserIdentity.RealUserSid)
    {
    }

    internal ScheduleManager(
        ITaskSchedulerBackend backend,
        ScheduledExecutableStore executables,
        string principalSid)
    {
        _backend = backend;
        _executables = executables;
        _principalSid = principalSid;
    }

    public bool CreateJob(ScheduleJob job, string cliPath)
        => CreateJobDetailed(job, cliPath).Succeeded;

    public AdministrativeMutationResult CreateJobDetailed(
        ScheduleJob job,
        string cliPath,
        bool dryRun = false)
    {
        var safeName = SanitizeName(job.Name);
        var target = TaskFolder + safeName;
        try
        {
            ValidateJob(job);
            if (!SafetyGuard.IsTaskSafeToDelete(target))
                return AdministrativeMutationPolicy.Skipped(
                    "scheduled-job-create",
                    target,
                    "Absent",
                    "Protected scheduled-task path.");

            var existing = _backend.Get(safeName);
            var before = existing == null
                ? "Absent"
                : JsonSerializer.Serialize(new
                {
                    existing.Name,
                    existing.Xml,
                    existing.SecurityDescriptor,
                });
            var rollback = existing == null
                ? $"Delete the newly registered task '{target}'."
                : JsonSerializer.Serialize(new
                {
                    existing.Name,
                    existing.Xml,
                    existing.SecurityDescriptor,
                    Restore = "Register the captured protected task definition.",
                });

            if (dryRun)
                return AdministrativeMutationPolicy.Preview(
                    "scheduled-job-create",
                    target,
                    before,
                    "Registered (planned)",
                    rollback);

            if (!CreateJobCore(job, cliPath))
                return AdministrativeMutationPolicy.Failed(
                    "scheduled-job-create",
                    target,
                    before,
                    LastError,
                    rollback);

            var registered = _backend.Get(safeName)
                ?? throw new InvalidOperationException(
                    "The scheduled job was not returned after registration.");
            var after = JsonSerializer.Serialize(new
            {
                registered.Name,
                registered.Xml,
                registered.SecurityDescriptor,
            });
            SystemRefreshNotifier.NotifyShellChanged();
            return AdministrativeMutationPolicy.Changed(
                "scheduled-job-create",
                target,
                before,
                after,
                rollback);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return AdministrativeMutationPolicy.Failed(
                "scheduled-job-create",
                target,
                "Unknown",
                ex.Message);
        }
    }

    private bool CreateJobCore(ScheduleJob job, string cliPath)
    {
        LastError = string.Empty;
        ValidateJob(job);
        var safeName = SanitizeName(job.Name);
        var normalizedArguments = NormalizeCliArguments(job.CliArguments);
        var existing = _backend.Get(safeName);
        var legacyWrapper = existing != null && TryGetLegacyWrapperPath(existing, out var wrapper)
            ? wrapper
            : null;

        try
        {
            var principalSid = ValidatePrincipalSid(_principalSid);
            var artifact = _executables.Install(cliPath);
            var xml = BuildTaskXml(job with { Name = safeName }, artifact.Path, principalSid);
            var security = BuildProtectedSecurityDescriptor(principalSid);
            _backend.Register(safeName, xml, principalSid, security, security);

            var registered = _backend.Get(safeName)
                ?? throw new InvalidOperationException(
                    "Task Scheduler did not return the newly registered task.");
            var info = Describe(registered);
            if (!info.IsTrusted ||
                !Path.GetFullPath(info.ExecutablePath).Equals(
                    Path.GetFullPath(artifact.Path),
                    StringComparison.OrdinalIgnoreCase) ||
                !info.Arguments.Equals(normalizedArguments, StringComparison.Ordinal) ||
                !info.Principal.Equals(principalSid, StringComparison.Ordinal) ||
                !info.LogonType.Equals("InteractiveToken", StringComparison.Ordinal) ||
                !info.RunLevel.Equals("HighestAvailable", StringComparison.Ordinal) ||
                !info.Enabled)
            {
                try { _backend.Delete(safeName); } catch { }
                throw new InvalidOperationException(
                    $"Task registration did not pass post-write validation: {info.Diagnostic}");
            }

            DeleteLegacyWrapper(legacyWrapper);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn($"Scheduled job creation failed: {ex.Message}");
            return false;
        }
    }

    public bool DeleteJob(string name)
        => DeleteJobDetailed(name).Succeeded;

    public AdministrativeMutationResult DeleteJobDetailed(
        string name,
        bool dryRun = false)
    {
        LastError = string.Empty;
        var safeName = SanitizeName(name);
        var target = TaskFolder + safeName;
        if (!SafetyGuard.IsTaskSafeToDelete(target))
        {
            var protectedResult = AdministrativeMutationPolicy.Skipped(
                "scheduled-job-delete",
                target,
                "Present",
                "Protected scheduled-task path.");
            LastError = protectedResult.Reason ?? "Protected scheduled-task path.";
            return protectedResult;
        }

        var existing = _backend.Get(safeName);
        if (existing == null)
        {
            var missing = AdministrativeMutationPolicy.Skipped(
                "scheduled-job-delete",
                target,
                "Absent",
                "The DeepPurge scheduled job is already absent.");
            LastError = missing.Reason ?? "The scheduled job is absent.";
            return missing;
        }

        var legacyWrapper = existing != null && TryGetLegacyWrapperPath(existing, out var wrapper)
            ? wrapper
            : null;
        var before = JsonSerializer.Serialize(new
        {
            existing!.Name,
            existing.Xml,
            existing.SecurityDescriptor,
        });
        var rollback = JsonSerializer.Serialize(new
        {
            existing.Name,
            existing.Xml,
            existing.SecurityDescriptor,
            Restore = "Register the captured protected task definition.",
        });

        if (dryRun)
            return AdministrativeMutationPolicy.Preview(
                "scheduled-job-delete",
                target,
                before,
                "Absent (planned)",
                rollback);

        try
        {
            _backend.Delete(safeName);
            if (_backend.Get(safeName) != null)
                throw new InvalidOperationException(
                    "The scheduled job still exists after deletion.");
            DeleteLegacyWrapper(legacyWrapper);
            SystemRefreshNotifier.NotifyShellChanged();
            return AdministrativeMutationPolicy.Changed(
                "scheduled-job-delete",
                target,
                before,
                "Absent",
                rollback);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn($"Scheduled job deletion failed: {ex.Message}");
            return AdministrativeMutationPolicy.Failed(
                "scheduled-job-delete",
                target,
                before,
                ex.Message,
                rollback);
        }
    }

    public List<ScheduledJobInfo> ListJobs()
    {
        try
        {
            return _backend.List()
                .Select(Describe)
                .OrderByDescending(job => job.IsLegacyWrapper)
                .ThenBy(job => job.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Scheduled job listing failed: {ex.Message}");
            return new List<ScheduledJobInfo>();
        }
    }

    /// <summary>
    /// Replaces legacy cmd/batch actions without reading or executing their
    /// user-writable contents. Existing triggers are preserved; the replacement
    /// starts as a constrained dry-run so an administrator can review it.
    /// </summary>
    public IReadOnlyList<ScheduleMigrationResult> MigrateLegacyJobs(string cliPath)
    {
        var records = _backend.List()
            .Where(record => IsLegacyRecord(record))
            .ToList();
        if (records.Count == 0)
            return Array.Empty<ScheduleMigrationResult>();

        ScheduledExecutableArtifact artifact;
        string principalSid;
        try
        {
            principalSid = ValidatePrincipalSid(_principalSid);
            artifact = _executables.Install(cliPath);
        }
        catch (Exception ex)
        {
            return records.Select(record =>
                new ScheduleMigrationResult(record.Name, false, ex.Message)).ToList();
        }

        var security = BuildProtectedSecurityDescriptor(principalSid);
        var results = new List<ScheduleMigrationResult>();
        foreach (var record in records)
        {
            var legacyWrapper = TryGetLegacyWrapperPath(record, out var wrapper)
                ? wrapper
                : null;
            try
            {
                var xml = HardenLegacyTaskXml(
                    record.Xml,
                    artifact.Path,
                    principalSid,
                    SafeMigrationArguments);
                _backend.Register(record.Name, xml, principalSid, security, security);
                var replacement = _backend.Get(record.Name)
                    ?? throw new InvalidOperationException(
                        "Task Scheduler did not return the migrated task.");
                var info = Describe(replacement);
                if (!info.IsTrusted ||
                    !info.Arguments.Equals(SafeMigrationArguments, StringComparison.Ordinal) ||
                    !info.Principal.Equals(principalSid, StringComparison.Ordinal) ||
                    !info.LogonType.Equals("InteractiveToken", StringComparison.Ordinal) ||
                    !info.RunLevel.Equals("HighestAvailable", StringComparison.Ordinal) ||
                    info.Enabled)
                {
                    try { _backend.Delete(record.Name); } catch { }
                    throw new InvalidOperationException(
                        $"Migrated task did not pass post-write validation: {info.Diagnostic}");
                }

                DeleteLegacyWrapper(legacyWrapper);
                results.Add(new ScheduleMigrationResult(
                    record.Name,
                    true,
                    "Replaced with a protected disabled dry-run action; existing triggers were preserved."));
            }
            catch (Exception ex)
            {
                results.Add(new ScheduleMigrationResult(record.Name, false, ex.Message));
            }
        }
        return results;
    }

    internal static string BuildTaskXml(
        ScheduleJob job,
        string executablePath,
        string principalSid)
    {
        ValidateJob(job);
        var command = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(command) ||
            !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Scheduled action must use an absolute executable path.",
                nameof(executablePath));
        var arguments = NormalizeCliArguments(job.CliArguments);
        var sid = ValidatePrincipalSid(principalSid);
        XNamespace ns = TaskNamespace;
        var start = DateTime.Today
            .AddHours(job.HourOfDay)
            .AddMinutes(job.MinuteOfHour);

        var task = new XElement(ns + "Task",
            new XAttribute("version", "1.4"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Description",
                    "DeepPurge constrained scheduled cleaning job."),
                new XElement(ns + "URI", TaskFolder + SanitizeName(job.Name))),
            BuildTrigger(ns, job, start),
            BuildPrincipal(ns, sid),
            BuildSettings(ns, enabled: true),
            BuildActions(ns, command, arguments));
        return new XDocument(new XDeclaration("1.0", "UTF-16", null), task)
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static string HardenLegacyTaskXml(
        string existingXml,
        string executablePath,
        string principalSid,
        string cliArguments)
    {
        var document = XDocument.Parse(existingXml, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("Legacy task XML has no root element.");
        var ns = root.Name.Namespace;
        if (ns == XNamespace.None) ns = TaskNamespace;
        var command = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(command) ||
            !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Scheduled action must use an absolute executable path.",
                nameof(executablePath));
        var sid = ValidatePrincipalSid(principalSid);
        var arguments = NormalizeCliArguments(cliArguments);

        ReplaceChild(root, "Principals", BuildPrincipal(ns, sid));
        ReplaceChild(root, "Settings", BuildSettings(ns, enabled: false));
        ReplaceChild(root, "Actions", BuildActions(ns, command, arguments));

        var registration = FindChild(root, "RegistrationInfo");
        if (registration == null)
        {
            registration = new XElement(ns + "RegistrationInfo");
            root.AddFirst(registration);
        }
        var description = FindChild(registration, "Description");
        if (description == null)
            registration.Add(new XElement(ns + "Description",
                "DeepPurge migrated constrained scheduled cleaning job."));
        else
            description.Value = "DeepPurge migrated constrained scheduled cleaning job.";

        return document.ToString(SaveOptions.DisableFormatting);
    }

    internal static string NormalizeCliArguments(string? cliArguments)
    {
        if (string.IsNullOrWhiteSpace(cliArguments))
            throw new ArgumentException(
                "A constrained scheduled-cleaning preset is required.",
                nameof(cliArguments));

        foreach (var c in cliArguments)
        {
            if (IsForbiddenCliArgumentChar(c) || (char.IsControl(c) && c != '\t'))
            {
                throw new ArgumentException(
                    "Scheduled CLI arguments must be whitespace-separated DeepPurge tokens. Quotes, line breaks, environment expansion, and shell metacharacters are not allowed.",
                    nameof(cliArguments));
            }
        }

        var tokens = cliArguments.Split(
            new[] { ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 ||
            !tokens[0].Equals("clean", StringComparison.OrdinalIgnoreCase) ||
            tokens.Skip(1).Any(token =>
                !token.Equals("junk", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("evidence", StringComparison.OrdinalIgnoreCase) &&
                !token.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) ||
            !tokens.Skip(1).Any(token =>
                token.Equals("junk", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("evidence", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Scheduled jobs are limited to clean junk/evidence presets with an optional --dry-run flag.",
                nameof(cliArguments));
        }
        if (tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tokens.Length)
            throw new ArgumentException(
                "Scheduled CLI arguments cannot contain duplicate tokens.",
                nameof(cliArguments));
        return string.Join(' ', tokens.Select(token => token.ToLowerInvariant()));
    }

    internal static string BuildProtectedSecurityDescriptor(string principalSid)
    {
        var sid = ValidatePrincipalSid(principalSid);
        return $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})";
    }

    internal static bool IsTaskSecurityDescriptorTrusted(
        string securityDescriptor,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(securityDescriptor))
        {
            reason = "Task security descriptor is unavailable.";
            return false;
        }

        try
        {
            var descriptor = new RawSecurityDescriptor(securityDescriptor);
            var owner = descriptor.Owner?.Value ?? string.Empty;
            if (!owner.Equals(AdministratorsSid.Value, StringComparison.Ordinal) &&
                !owner.Equals(SystemSid.Value, StringComparison.Ordinal))
            {
                reason = $"Task owner is untrusted: {owner}";
                return false;
            }
            if (descriptor.DiscretionaryAcl == null)
            {
                reason = "Task has no DACL.";
                return false;
            }

            const int genericWrite = unchecked((int)0x40000000);
            const int genericAll = 0x10000000;
            const int delete = 0x00010000;
            const int writeDac = 0x00040000;
            const int writeOwner = 0x00080000;
            // Task Scheduler maps generic task permissions to file access
            // masks. Read/execute is 0x001200A9; the mutating file rights are
            // write/append data, write EA/attributes, and delete-child.
            const int objectWriteRights = 0x00000156;
            const int mutating = genericWrite | genericAll | delete |
                                 writeDac | writeOwner | objectWriteRights;
            var administratorsFullControl = false;
            var systemFullControl = false;
            foreach (GenericAce genericAce in descriptor.DiscretionaryAcl)
            {
                if (genericAce is not CommonAce ace) continue;
                var sid = ace.SecurityIdentifier.Value;
                if (ace.AceQualifier == AceQualifier.AccessDenied)
                {
                    reason = $"Task contains an unexpected deny ACE for SID {sid}.";
                    return false;
                }
                if (ace.AceQualifier != AceQualifier.AccessAllowed) continue;
                const int fileAllAccess = 0x001F01FF;
                var fullControl =
                    (ace.AccessMask & genericAll) != 0 ||
                    (ace.AccessMask & fileAllAccess) == fileAllAccess;
                if (sid.Equals(AdministratorsSid.Value, StringComparison.Ordinal))
                {
                    administratorsFullControl |= fullControl;
                    continue;
                }
                if (sid.Equals(SystemSid.Value, StringComparison.Ordinal))
                {
                    systemFullControl |= fullControl;
                    continue;
                }
                if ((ace.AccessMask & mutating) != 0)
                {
                    reason =
                        $"Task grants mutating rights 0x{ace.AccessMask:X8} to untrusted SID {sid}.";
                    return false;
                }
            }
            if (!administratorsFullControl || !systemFullControl)
            {
                reason = "Task does not grant full control to both Administrators and SYSTEM.";
                return false;
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Task security descriptor is invalid: {ex.Message}";
            return false;
        }
    }

    private ScheduledJobInfo Describe(TaskSchedulerTaskRecord record)
    {
        string principal = string.Empty;
        string logonType = string.Empty;
        string runLevel = string.Empty;
        string command = string.Empty;
        string arguments = string.Empty;
        string workingDirectory = string.Empty;
        var enabled = false;
        string xmlReason = string.Empty;
        try
        {
            var document = XDocument.Parse(record.Xml);
            var root = document.Root
                ?? throw new InvalidOperationException("Task XML has no root element.");
            principal = DescendantValue(root, "UserId");
            if (string.IsNullOrWhiteSpace(principal))
                principal = DescendantValue(root, "GroupId");
            logonType = DescendantValue(root, "LogonType");
            runLevel = DescendantValue(root, "RunLevel");
            command = DescendantValue(root, "Command");
            arguments = DescendantValue(root, "Arguments");
            workingDirectory = DescendantValue(root, "WorkingDirectory");
            enabled = !DescendantValue(
                    FindChild(root, "Settings") ?? root,
                    "Enabled").Equals(
                "false",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            xmlReason = $"Task XML could not be parsed: {ex.Message}";
        }

        var legacy = IsLegacyCommand(command);
        var actionReason = legacy ? "Legacy shell wrapper is user-replaceable." : string.Empty;
        var actionTrusted = !legacy &&
            _executables.TryValidatePath(command, out _, out actionReason);
        var taskTrusted = IsTaskSecurityDescriptorTrusted(
            record.SecurityDescriptor,
            out var taskReason);
        var diagnostic = string.Join(
            " ",
            new[] { xmlReason, actionReason, taskReason }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(diagnostic))
            diagnostic = "Protected executable and task DACL verified.";

        return new ScheduledJobInfo(
            record.Name,
            TaskFolder + record.Name,
            principal,
            logonType,
            runLevel,
            command,
            arguments,
            workingDirectory,
            enabled,
            actionTrusted,
            taskTrusted,
            legacy,
            diagnostic);
    }

    private static XElement BuildTrigger(
        XNamespace ns,
        ScheduleJob job,
        DateTime start)
    {
        var calendar = new XElement(ns + "CalendarTrigger",
            new XElement(ns + "StartBoundary", start.ToString("s")),
            new XElement(ns + "Enabled", "true"));
        calendar.Add(job.Frequency switch
        {
            ScheduleFrequency.Daily =>
                new XElement(ns + "ScheduleByDay",
                    new XElement(ns + "DaysInterval", 1)),
            ScheduleFrequency.Weekly =>
                new XElement(ns + "ScheduleByWeek",
                    new XElement(ns + "WeeksInterval", 1),
                    new XElement(ns + "DaysOfWeek",
                        new XElement(ns + job.DayOfWeek.ToString()))),
            ScheduleFrequency.Monthly =>
                new XElement(ns + "ScheduleByMonth",
                    new XElement(ns + "DaysOfMonth",
                        new XElement(ns + "Day", 1)),
                    new XElement(ns + "Months",
                        new[]
                        {
                            "January", "February", "March", "April", "May", "June",
                            "July", "August", "September", "October", "November", "December",
                        }.Select(month => new XElement(ns + month)))),
            _ => throw new ArgumentOutOfRangeException(nameof(job.Frequency)),
        });
        return new XElement(ns + "Triggers", calendar);
    }

    private static XElement BuildPrincipal(XNamespace ns, string sid)
        => new(ns + "Principals",
            new XElement(ns + "Principal",
                new XAttribute("id", "Author"),
                new XElement(ns + "UserId", sid),
                new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable")));

    private static XElement BuildSettings(XNamespace ns, bool enabled)
        => new(ns + "Settings",
            new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(ns + "DisallowStartIfOnBatteries", "false"),
            new XElement(ns + "StopIfGoingOnBatteries", "false"),
            new XElement(ns + "AllowHardTerminate", "true"),
            new XElement(ns + "StartWhenAvailable", "true"),
            new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
            new XElement(ns + "AllowStartOnDemand", "true"),
            new XElement(ns + "Enabled", enabled ? "true" : "false"),
            new XElement(ns + "Hidden", "false"),
            new XElement(ns + "RunOnlyIfIdle", "false"),
            new XElement(ns + "WakeToRun", "false"),
            new XElement(ns + "ExecutionTimeLimit", "PT2H"),
            new XElement(ns + "Priority", 7));

    private static XElement BuildActions(
        XNamespace ns,
        string executablePath,
        string arguments)
        => new(ns + "Actions",
            new XAttribute("Context", "Author"),
            new XElement(ns + "Exec",
                new XElement(ns + "Command", executablePath),
                new XElement(ns + "Arguments", arguments),
                new XElement(ns + "WorkingDirectory",
                    Path.GetDirectoryName(executablePath) ?? string.Empty)));

    private static void ReplaceChild(
        XElement root,
        string localName,
        XElement replacement)
    {
        var existing = FindChild(root, localName);
        if (existing == null) root.Add(replacement);
        else existing.ReplaceWith(replacement);
    }

    private static XElement? FindChild(XElement element, string localName)
        => element.Elements().FirstOrDefault(child =>
            child.Name.LocalName.Equals(localName, StringComparison.Ordinal));

    private static string DescendantValue(XElement element, string localName)
        => element.Descendants().FirstOrDefault(descendant =>
            descendant.Name.LocalName.Equals(localName, StringComparison.Ordinal))?.Value
            ?? string.Empty;

    private static bool IsLegacyRecord(TaskSchedulerTaskRecord record)
    {
        try
        {
            var document = XDocument.Parse(record.Xml);
            return IsLegacyCommand(DescendantValue(document.Root!, "Command"));
        }
        catch { return false; }
    }

    private static bool TryGetLegacyWrapperPath(
        TaskSchedulerTaskRecord record,
        out string? wrapperPath)
    {
        wrapperPath = null;
        try
        {
            var document = XDocument.Parse(record.Xml);
            var command = DescendantValue(document.Root!, "Command");
            if (!IsLegacyCommand(command)) return false;
            var full = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(command.Trim().Trim('"')));
            var expected = Path.GetFullPath(Path.Combine(
                DataPaths.Config,
                $"job_{SanitizeName(record.Name)}.cmd"));
            if (!full.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return false;
            wrapperPath = full;
            return true;
        }
        catch { return false; }
    }

    private static bool IsLegacyCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        var leaf = Path.GetFileName(expanded);
        return leaf.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               leaf.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               leaf.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
               expanded.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
               expanded.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
               expanded.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteLegacyWrapper(string? wrapperPath)
    {
        if (string.IsNullOrWhiteSpace(wrapperPath) || !File.Exists(wrapperPath))
            return;
        if (!HandleBoundFileOperations.DeleteFileWithinScope(
                wrapperPath,
                DataPaths.Config,
                out var reason))
            Log.Warn($"Failed to delete legacy scheduled wrapper: {reason}");
    }

    private static void ValidateJob(ScheduleJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
            throw new ArgumentException("Job name is required.", nameof(job));
        if (job.HourOfDay is < 0 or > 23 || job.MinuteOfHour is < 0 or > 59)
            throw new ArgumentException("Time is out of range.", nameof(job));
        _ = NormalizeCliArguments(job.CliArguments);
    }

    private static string ValidatePrincipalSid(string principalSid)
    {
        if (string.IsNullOrWhiteSpace(principalSid))
            throw new InvalidOperationException(
                "The interactive desktop user's SID could not be resolved.");
        return new SecurityIdentifier(principalSid).Value;
    }

    private static bool IsForbiddenCliArgumentChar(char c)
        => c is '&' or '|' or '<' or '>' or '^' or '%' or '!' or '"' or
            '(' or ')' or '\r' or '\n';

    /// <summary>Strict alpha-numeric plus space, dash, and underscore whitelist.</summary>
    private static string SanitizeName(string name)
    {
        var clean = new string((name ?? "").Where(c =>
            char.IsLetterOrDigit(c) || c is '_' or '-' or ' ').ToArray()).Trim();
        if (clean.Length == 0) return "DeepPurgeJob";
        return clean.Length > 64 ? clean[..64] : clean;
    }
}
