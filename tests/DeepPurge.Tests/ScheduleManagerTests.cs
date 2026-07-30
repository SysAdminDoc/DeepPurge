using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml.Linq;
using DeepPurge.Core.App;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Schedule;
using Xunit;

namespace DeepPurge.Tests;

public sealed class ScheduleManagerTests : IDisposable
{
    private const string UserSid = "S-1-5-21-1000-1001-1002-1003";
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeepPurgeScheduleTests",
        Guid.NewGuid().ToString("N"));

    public ScheduleManagerTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static string Sanitize(string input)
    {
        var method = typeof(ScheduleManager).GetMethod(
            "SanitizeName",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { input })!;
    }

    private static string Normalize(string input)
    {
        try
        {
            return ScheduleManager.NormalizeCliArguments(input);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    [Theory]
    [InlineData("Normal Job Name", "Normal Job Name")]
    [InlineData("with_underscore-dash", "with_underscore-dash")]
    [InlineData("Nightly 3am", "Nightly 3am")]
    public void Allows_normal_names(string input, string expected)
        => Assert.Equal(expected, Sanitize(input));

    [Theory]
    [InlineData("foo & del /q *", "foo  del q")]
    [InlineData("job | rm -rf /", "job  rm -rf")]
    [InlineData("a\"b\"c", "abc")]
    [InlineData("..\\..\\escape", "escape")]
    [InlineData("'; DROP TABLE jobs; --", "DROP TABLE jobs --")]
    [InlineData("", "DeepPurgeJob")]
    [InlineData("   ", "DeepPurgeJob")]
    public void Strips_name_metacharacters_and_falls_back(
        string input,
        string expected)
        => Assert.Equal(expected, Sanitize(input));

    [Fact]
    public void Name_sanitizer_caps_at_64_chars()
        => Assert.Equal(64, Sanitize(new string('A', 200)).Length);

    [Theory]
    [InlineData("  clean\tjunk   evidence --dry-run  ", "clean junk evidence --dry-run")]
    [InlineData("CLEAN EVIDENCE", "clean evidence")]
    [InlineData("clean junk", "clean junk")]
    public void Normalizes_constrained_cleaning_presets(
        string input,
        string expected)
        => Assert.Equal(expected, Normalize(input));

    [Theory]
    [InlineData("clean junk & calc")]
    [InlineData("clean junk | calc")]
    [InlineData("clean junk %PATH%")]
    [InlineData("clean \"junk\" evidence")]
    [InlineData("clean junk\r\ncalc")]
    [InlineData("uninstall Example")]
    [InlineData("repair dism")]
    [InlineData("clean junk junk")]
    [InlineData("")]
    public void Rejects_shell_syntax_and_non_cleaning_commands(string input)
        => Assert.Throws<ArgumentException>(() => Normalize(input));

    [Theory]
    [InlineData(ScheduleFrequency.Daily, "ScheduleByDay", "DaysInterval")]
    [InlineData(ScheduleFrequency.Weekly, "ScheduleByWeek", "Monday")]
    [InlineData(ScheduleFrequency.Monthly, "ScheduleByMonth", "January")]
    public void Task_xml_separates_absolute_command_and_arguments(
        ScheduleFrequency frequency,
        string scheduleElement,
        string cadenceElement)
    {
        var executable = Path.Combine(_tempRoot, "Protected", "DeepPurgeCli.exe");
        var xml = ScheduleManager.BuildTaskXml(
            new ScheduleJob(
                "Nightly",
                frequency,
                DayOfWeek.Monday,
                3,
                15,
                "clean junk evidence --dry-run"),
            executable,
            UserSid);
        var document = XDocument.Parse(xml);

        Assert.Equal(
            Path.GetFullPath(executable),
            Value(document, "Command"));
        Assert.Equal(
            "clean junk evidence --dry-run",
            Value(document, "Arguments"));
        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath(executable)),
            Value(document, "WorkingDirectory"));
        Assert.Equal(UserSid, Value(document, "UserId"));
        Assert.Equal("InteractiveToken", Value(document, "LogonType"));
        Assert.Equal("HighestAvailable", Value(document, "RunLevel"));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == scheduleElement);
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == cadenceElement);
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "ComHandler");
    }

    [Fact]
    public void Protected_task_sddl_is_trusted_and_user_writable_sddl_is_rejected()
    {
        var protectedSddl = ScheduleManager.BuildProtectedSecurityDescriptor(UserSid);
        Assert.True(
            ScheduleManager.IsTaskSecurityDescriptorTrusted(
                protectedSddl,
                out var protectedReason),
            protectedReason);

        var writable = $"O:{UserSid}G:{UserSid}D:P(A;;GA;;;{UserSid})(A;;FA;;;SY)";
        Assert.False(
            ScheduleManager.IsTaskSecurityDescriptorTrusted(
                writable,
                out var writableReason));
        Assert.Contains("owner is untrusted", writableReason);

        var userWrite = $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GW;;;{UserSid})";
        Assert.False(
            ScheduleManager.IsTaskSecurityDescriptorTrusted(
                userWrite,
                out var userWriteReason));
        Assert.Contains("mutating rights", userWriteReason);

        var missingSystem = $"O:BAG:BAD:P(A;;FA;;;BA)(A;;GRGX;;;{UserSid})";
        Assert.False(
            ScheduleManager.IsTaskSecurityDescriptorTrusted(
                missingSystem,
                out var missingSystemReason));
        Assert.Contains("Administrators and SYSTEM", missingSystemReason);
    }

    [Fact]
    public void Create_copies_cli_to_content_addressed_store_and_verifies_definition()
    {
        var source = CreateFakeExecutable("DeepPurgeCli.exe", "source payload");
        var backend = new FakeTaskSchedulerBackend();
        var manager = CreateManager(backend);

        Assert.True(manager.CreateJob(
            new ScheduleJob(
                "Nightly",
                ScheduleFrequency.Daily,
                DayOfWeek.Monday,
                2,
                30,
                "clean junk"),
            source));

        var record = Assert.Single(backend.List());
        var command = Value(XDocument.Parse(record.Xml), "Command");
        Assert.NotEqual(Path.GetFullPath(source), command);
        Assert.StartsWith(
            Path.Combine(_tempRoot, "protected") + Path.DirectorySeparatorChar,
            command,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(command));
        Assert.Equal("clean junk", Value(XDocument.Parse(record.Xml), "Arguments"));

        var diagnostic = Assert.Single(manager.ListJobs());
        Assert.True(diagnostic.ActionTargetTrusted, diagnostic.Diagnostic);
        Assert.True(diagnostic.TaskAclTrusted, diagnostic.Diagnostic);
        Assert.Equal(UserSid, diagnostic.Principal);
    }

    [Fact]
    public void Create_removes_task_when_backend_changes_the_registered_action()
    {
        var source = CreateFakeExecutable("DeepPurgeCli.exe", "source payload");
        var backend = new FakeTaskSchedulerBackend
        {
            RegistrationTransform = record =>
            {
                var document = XDocument.Parse(record.Xml);
                document.Descendants().Single(element =>
                    element.Name.LocalName == "Command").Value =
                    Path.Combine(_tempRoot, "writable.exe");
                return record with { Xml = document.ToString(SaveOptions.DisableFormatting) };
            },
        };
        var manager = CreateManager(backend);

        Assert.False(manager.CreateJob(
            new ScheduleJob(
                "Nightly",
                ScheduleFrequency.Daily,
                DayOfWeek.Monday,
                2,
                30,
                "clean junk"),
            source));
        Assert.Empty(backend.List());
        Assert.Contains("post-write validation", manager.LastError);
    }

    [Fact]
    public void Diagnostics_reject_content_drift_in_protected_action()
    {
        var source = CreateFakeExecutable("DeepPurgeCli.exe", "source payload");
        var backend = new FakeTaskSchedulerBackend();
        var manager = CreateManager(backend);
        Assert.True(manager.CreateJob(
            new ScheduleJob(
                "Nightly",
                ScheduleFrequency.Daily,
                DayOfWeek.Monday,
                2,
                30,
                "clean junk"),
            source));
        var command = Value(
            XDocument.Parse(Assert.Single(backend.List()).Xml),
            "Command");

        File.AppendAllText(command, "drift");

        var diagnostic = Assert.Single(manager.ListJobs());
        Assert.False(diagnostic.ActionTargetTrusted);
        Assert.Contains("SHA-256 content", diagnostic.Diagnostic);
    }

    [Fact]
    public void Migration_preserves_triggers_but_never_carries_wrapper_arguments()
    {
        var source = CreateFakeExecutable("DeepPurgeCli.exe", "source payload");
        var backend = new FakeTaskSchedulerBackend();
        var maliciousWrapper = Path.Combine(_tempRoot, "job_Legacy.cmd");
        var legacyXml =
            """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>2026-07-29T04:00:00</StartBoundary>
                  <ScheduleByWeek><WeeksInterval>2</WeeksInterval><DaysOfWeek><Friday /></DaysOfWeek></ScheduleByWeek>
                </CalendarTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>S-1-5-21-9</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><Enabled>true</Enabled></Settings>
              <Actions Context="Author"><Exec><Command>PLACEHOLDER</Command><Arguments>/c calc.exe</Arguments></Exec></Actions>
            </Task>
            """.Replace("PLACEHOLDER", maliciousWrapper);
        backend.Seed(new TaskSchedulerTaskRecord(
            "Legacy",
            legacyXml,
            $"O:{UserSid}G:{UserSid}D:(A;;GA;;;{UserSid})"));
        var manager = CreateManager(backend);

        var result = Assert.Single(manager.MigrateLegacyJobs(source));
        Assert.True(result.Migrated, result.Message);
        var migrated = XDocument.Parse(backend.Get("Legacy")!.Xml);
        Assert.Equal(
            "2026-07-29T04:00:00",
            Value(migrated, "StartBoundary"));
        Assert.Equal("2", Value(migrated, "WeeksInterval"));
        Assert.Contains(migrated.Descendants(), element =>
            element.Name.LocalName == "Friday");
        Assert.Equal(
            "clean junk evidence --dry-run",
            Value(migrated, "Arguments"));
        Assert.DoesNotContain("calc.exe", migrated.ToString());
        Assert.Equal(UserSid, Value(migrated, "UserId"));
        Assert.Equal(
            "false",
            migrated.Descendants().Single(element =>
                element.Name.LocalName == "Settings")
                .Elements().Single(element =>
                    element.Name.LocalName == "Enabled").Value);
        var migratedInfo = Assert.Single(manager.ListJobs());
        Assert.True(migratedInfo.IsTrusted);
        Assert.False(migratedInfo.Enabled);
    }

    [Fact]
    public void Rejects_multi_file_development_apphost()
    {
        var source = CreateFakeExecutable("DeepPurgeCli.exe", "apphost");
        File.WriteAllText(Path.ChangeExtension(source, ".dll"), "companion");
        var manager = CreateManager(new FakeTaskSchedulerBackend());

        Assert.False(manager.CreateJob(
            new ScheduleJob(
                "Nightly",
                ScheduleFrequency.Daily,
                DayOfWeek.Monday,
                2,
                30,
                "clean junk"),
            source));
        Assert.Contains("multi-file development build", manager.LastError);
    }

    [Fact]
    public void Elevated_production_scheduler_round_trips_a_hardened_task()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DEEPPURGE_RUN_ELEVATED_TESTS"),
                "1",
                StringComparison.Ordinal))
            return;

        using (var identity = WindowsIdentity.GetCurrent())
        {
            Assert.True(
                new WindowsPrincipal(identity).IsInRole(
                    WindowsBuiltInRole.Administrator),
                "The production scheduler test must run elevated.");
        }

        var cliPath = Environment.GetEnvironmentVariable(
            "DEEPPURGE_SCHEDULE_CLI_PATH");
        Assert.True(File.Exists(cliPath), "Published CLI path is required.");
        var hash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(cliPath!))).ToLowerInvariant();
        var artifactPath = Path.Combine(
            DataPaths.ScheduledTaskExecutables,
            $"DeepPurgeCli-{hash[..16]}.exe");
        var artifactAlreadyExisted = File.Exists(artifactPath);
        var taskName = $"DeepPurge Schedule Test {Guid.NewGuid():N}";
        var manager = new ScheduleManager();

        try
        {
            Assert.True(
                manager.CreateJob(
                    new ScheduleJob(
                        taskName,
                        ScheduleFrequency.Daily,
                        DayOfWeek.Monday,
                        23,
                        59,
                        "clean junk evidence --dry-run"),
                    cliPath!),
                manager.LastError);

            var job = Assert.Single(
                manager.ListJobs(),
                candidate => candidate.Name == taskName);
            Assert.True(job.ActionTargetTrusted, job.Diagnostic);
            Assert.True(job.TaskAclTrusted, job.Diagnostic);
            Assert.False(job.IsLegacyWrapper);
            Assert.Equal(UserIdentity.RealUserSid, job.Principal);
            Assert.Equal("InteractiveToken", job.LogonType);
            Assert.Equal("HighestAvailable", job.RunLevel);
            Assert.Equal(artifactPath, job.ExecutablePath);
            Assert.Equal("clean junk evidence --dry-run", job.Arguments);
        }
        finally
        {
            manager.DeleteJob(taskName);
            if (!artifactAlreadyExisted)
            {
                HandleBoundFileOperations.DeleteFileWithinScope(
                    artifactPath,
                    DataPaths.ScheduledTaskExecutables,
                    out _);
            }
        }
    }

    private ScheduleManager CreateManager(FakeTaskSchedulerBackend backend)
        => new(
            backend,
            new ScheduledExecutableStore(
                Path.Combine(_tempRoot, "protected"),
                requireTrustedAcl: false),
            UserSid);

    private string CreateFakeExecutable(string name, string payload)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllBytes(
            path,
            new[] { (byte)'M', (byte)'Z' }
                .Concat(System.Text.Encoding.UTF8.GetBytes(payload))
                .ToArray());
        return path;
    }

    private static string Value(XDocument document, string localName)
        => document.Descendants().Single(element =>
            element.Name.LocalName == localName).Value;

    private sealed class FakeTaskSchedulerBackend : ITaskSchedulerBackend
    {
        private readonly Dictionary<string, TaskSchedulerTaskRecord> _records =
            new(StringComparer.OrdinalIgnoreCase);

        internal Func<TaskSchedulerTaskRecord, TaskSchedulerTaskRecord>? RegistrationTransform
        { get; init; }

        public IReadOnlyList<TaskSchedulerTaskRecord> List()
            => _records.Values.ToList();

        public TaskSchedulerTaskRecord? Get(string name)
            => _records.GetValueOrDefault(name);

        public void Register(
            string name,
            string xml,
            string principalSid,
            string taskSecurityDescriptor,
            string folderSecurityDescriptor)
        {
            var record = new TaskSchedulerTaskRecord(
                name,
                xml,
                taskSecurityDescriptor);
            _records[name] = RegistrationTransform?.Invoke(record) ?? record;
        }

        public void Delete(string name) => _records.Remove(name);

        internal void Seed(TaskSchedulerTaskRecord record)
            => _records[record.Name] = record;
    }
}
