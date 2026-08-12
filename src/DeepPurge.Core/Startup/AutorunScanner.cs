using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Execution;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Security;
using DeepPurge.Core.Services;
using global::Microsoft.Win32;

namespace DeepPurge.Core.Startup;

public class AutorunEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private SignatureStatus _signatureStatus = SignatureStatus.Unknown;
    private string _signatureDisplay = "";

    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Location { get; set; } = "";
    public string RegistryPath { get; set; } = "";
    public AutorunType Type { get; set; }
    public bool IsEnabled { get; set; }
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsRunning { get; set; }
    public bool IsProtected => AutorunSafety.IsProtected(this);
    public bool MutationSupported => !IsProtected && Type != AutorunType.ScheduledTask;

    /// <summary>WinVerifyTrust result for the resolved executable.</summary>
    public SignatureStatus SignatureStatus
    {
        get => _signatureStatus;
        set { _signatureStatus = value; OnPropertyChanged(); }
    }

    public string SignatureDisplay
    {
        get => _signatureDisplay;
        set { _signatureDisplay = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum AutorunType
{
    RegistryRun,
    RegistryRunOnce,
    StartupFolder,
    Service,
    ScheduledTask,
}

internal static class AutorunSafety
{
    public static bool IsProtected(AutorunEntry entry)
        => entry.Type switch
        {
            AutorunType.RegistryRun or AutorunType.RegistryRunOnce =>
                !SafetyGuard.IsAutorunSafeToDelete(entry.Command) ||
                !SafetyGuard.IsRegistryPathSafeToDelete($"{entry.RegistryPath}\\{entry.Name}"),
            AutorunType.StartupFolder => !SafetyGuard.IsPathSafeToDelete(entry.Command),
            AutorunType.Service => !SafetyGuard.IsServiceSafeToModify(entry.Name),
            AutorunType.ScheduledTask => true,
            _ => true,
        };
}

public static class AutorunScanner
{
    // "Enabled" and "Disabled" blobs Windows writes into StartupApproved. The
    // first byte is the flag (2 = enabled, 3 = disabled); the following 11 bytes
    // are a FILETIME that Windows uses for UI display.
    private static readonly byte[] StartupApprovedEnabled = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] StartupApprovedDisabled = { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static List<AutorunEntry> GetAllAutoruns()
        => GetAllAutorunsDetailed().Items.ToList();

    public static ScanResult<AutorunEntry> GetAllAutorunsDetailed(CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var entries = new List<AutorunEntry>();
        var failures = new List<ScanIssue>();
        var warnings = new List<string>();

        // Single pass over the process table — previously every autorun entry
        // did its own Process.GetProcessesByName() call, which (a) leaked
        // Process handles and (b) re-enumerated hundreds of processes per entry.
        using var procSet = ProcessNameSet.Snapshot();
        try
        {
            ct.ThrowIfCancellationRequested();
            ScanRegistryRun(entries, procSet, failures, warnings, ct);
            ct.ThrowIfCancellationRequested();
            ScanStartupFolders(entries, failures, warnings, ct);
            ct.ThrowIfCancellationRequested();
            ScanServices(entries, procSet, failures, warnings, ct);
            ct.ThrowIfCancellationRequested();
            ScanScheduledTasks(entries, procSet, failures, warnings, ct);
            ct.ThrowIfCancellationRequested();
            PopulateSignatures(entries);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            warnings.Add("Autorun scan was cancelled; entries collected so far were retained.");
        }
        catch (Exception ex)
        {
            failures.Add(new ScanIssue("autorun", ex.Message, ex.GetType().Name));
        }

        var result = ScanResult<AutorunEntry>.Create(
            "autorun",
            entries,
            failures,
            warnings,
            stopwatch.Elapsed,
            isCancelled: ct.IsCancellationRequested);
        ScanDiagnosticsLedger.Record("autorun", result);
        return result;
    }

    /// <summary>
    /// Disposable snapshot of running-process names. Enumerates
    /// <see cref="Process.GetProcesses"/> once, extracts the exe basenames,
    /// and disposes every Process object on release. Lookups are O(1).
    /// </summary>
    private sealed class ProcessNameSet : IDisposable
    {
        private readonly HashSet<string> _names;

        private ProcessNameSet(HashSet<string> names) { _names = names; }

        public static ProcessNameSet Snapshot()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return new ProcessNameSet(names); }
            foreach (var p in procs)
            {
                try { names.Add(p.ProcessName); }
                catch { /* exited between enum and access */ }
                finally { try { p.Dispose(); } catch { /* Process.Dispose in finally is best-effort */ } }
            }
            return new ProcessNameSet(names);
        }

        public bool IsRunning(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            var name = Path.GetFileNameWithoutExtension(exePath);
            return !string.IsNullOrEmpty(name) && _names.Contains(name);
        }

        public void Dispose() { /* snapshot is value-only; nothing to release */ }
    }

    /// <summary>
    /// Lifted from Sysinternals Autoruns: every entry carries a
    /// WinVerifyTrust result so the user can instantly spot an unsigned
    /// binary masquerading as a system service.
    /// </summary>
    private static void PopulateSignatures(List<AutorunEntry> entries)
    {
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = 8 }, entry =>
        {
            try
            {
                var exePath = ExtractExePath(entry.Command);
                if (string.IsNullOrEmpty(exePath)) return;

                var expanded = Environment.ExpandEnvironmentVariables(exePath);
                if (!File.Exists(expanded)) return;

                var info = DigitalSignatureInspector.Inspect(expanded);
                entry.SignatureStatus = info.Status;
                entry.SignatureDisplay = info.Display;
                if (info.IsTrusted && string.IsNullOrEmpty(entry.Publisher) && !string.IsNullOrEmpty(info.Subject))
                    entry.Publisher = info.Subject;
            }
            catch { /* never fail the scan because a signature check failed */ }
        });
    }

    // ═══════════════════════════════════════════════════════
    //  Registry Run/RunOnce
    // ═══════════════════════════════════════════════════════

    private record RunLocation(string SubKey, RegistryKey Hive, string Display, AutorunType Type, string HivePrefix);

    private static readonly RunLocation[] RunLocations = new[]
    {
        new RunLocation(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            global::Microsoft.Win32.Registry.LocalMachine, @"HKLM\...\Run", AutorunType.RegistryRun, "HKLM"),
        new RunLocation(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            global::Microsoft.Win32.Registry.LocalMachine, @"HKLM\...\RunOnce", AutorunType.RegistryRunOnce, "HKLM"),
        new RunLocation(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            global::Microsoft.Win32.Registry.CurrentUser, @"HKCU\...\Run", AutorunType.RegistryRun, "HKCU"),
        new RunLocation(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            global::Microsoft.Win32.Registry.CurrentUser, @"HKCU\...\RunOnce", AutorunType.RegistryRunOnce, "HKCU"),
        new RunLocation(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            global::Microsoft.Win32.Registry.LocalMachine, @"HKLM\...\Run (32-bit)", AutorunType.RegistryRun, "HKLM"),
    };

    private static void ScanRegistryRun(List<AutorunEntry> entries, ProcessNameSet procSet,
        List<ScanIssue> failures, List<string> warnings, CancellationToken ct)
    {
        foreach (var loc in RunLocations)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                using var key = loc.Hive.OpenSubKey(loc.SubKey);
                if (key == null) continue;

                foreach (var name in key.GetValueNames())
                {
                    ct.ThrowIfCancellationRequested();
                    var command = key.GetValue(name) as string ?? "";
                    if (string.IsNullOrEmpty(command)) continue;

                    var exePath = ExtractExePath(command);
                    entries.Add(new AutorunEntry
                    {
                        Name = name,
                        Command = command,
                        Location = loc.Display,
                        RegistryPath = $@"{loc.HivePrefix}\{loc.SubKey}",
                        Type = loc.Type,
                        IsEnabled = true,
                        Publisher = GetFilePublisher(exePath),
                        IsRunning = procSet.IsRunning(exePath),
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures.Add(new ScanIssue($"autorun:{loc.Display}", ex.Message, ex.GetType().Name));
            }
        }

        ApplyStartupApprovedFlags(entries, warnings, ct);
    }

    /// <summary>
    /// Windows stores the enabled/disabled flag for Run entries in
    /// HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run
    /// and mirrored paths. Cross-reference so the UI shows the correct toggle.
    /// </summary>
    private static void ApplyStartupApprovedFlags(List<AutorunEntry> entries,
        List<string> warnings, CancellationToken ct)
    {
        var approvedLocations = new (RegistryKey Hive, string Path)[]
        {
            (global::Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
            (global::Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
            (global::Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
            (global::Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
        };

        foreach (var (hive, path) in approvedLocations)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not byte[] data || data.Length == 0) continue;

                    var match = entries.FirstOrDefault(e =>
                        e.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase) &&
                        (e.Type == AutorunType.RegistryRun || e.Type == AutorunType.RegistryRunOnce));

                    if (match != null) match.IsEnabled = data[0] != 0x03;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                warnings.Add($"Startup approval flags at {path} could not be read: {ex.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Startup folders
    // ═══════════════════════════════════════════════════════

    private static void ScanStartupFolders(List<AutorunEntry> entries,
        List<ScanIssue> failures, List<string> warnings, CancellationToken ct)
    {
        var folders = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "User Startup Folder"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "All Users Startup Folder"),
        };

        foreach (var (folder, location) in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".lnk" or ".bat" or ".cmd" or ".exe" or ".vbs" or ".ps1" or ".url")) continue;

                    entries.Add(new AutorunEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Command = file,
                        Location = location,
                        Type = AutorunType.StartupFolder,
                        IsEnabled = true,
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures.Add(new ScanIssue($"autorun:{location}", ex.Message, ex.GetType().Name));
            }
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Services (Win32 only, autostart only)
    // ═══════════════════════════════════════════════════════

    private static void ScanServices(List<AutorunEntry> entries, ProcessNameSet procSet,
        List<ScanIssue> failures, List<string> warnings, CancellationToken ct)
    {
        try
        {
            using var servicesKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var svcKey = servicesKey.OpenSubKey(serviceName);
                    if (svcKey == null) continue;

                    var startType = Convert.ToInt32(svcKey.GetValue("Start") ?? 4);
                    if (startType > 2) continue; // Only Boot(0) / System(1) / Automatic(2).

                    var svcType = Convert.ToInt32(svcKey.GetValue("Type") ?? 0);
                    if (svcType is 1 or 2 or 8) continue; // Skip kernel/FS/adapter drivers.

                    var imagePath = svcKey.GetValue("ImagePath") as string ?? "";
                    if (string.IsNullOrEmpty(imagePath)) continue;

                    var displayName = svcKey.GetValue("DisplayName") as string ?? serviceName;

                    entries.Add(new AutorunEntry
                    {
                        Name = serviceName,
                        Description = displayName,
                        Command = imagePath,
                        Location = "Windows Service",
                        Type = AutorunType.Service,
                        IsEnabled = true,
                        IsRunning = procSet.IsRunning(ExtractExePath(imagePath)),
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    warnings.Add($"Autorun service '{serviceName}' could not be read: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failures.Add(new ScanIssue("autorun:services", ex.Message, ex.GetType().Name));
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Scheduled tasks (non-Microsoft only)
    // ═══════════════════════════════════════════════════════

    private static void ScanScheduledTasks(List<AutorunEntry> entries, ProcessNameSet procSet,
        List<ScanIssue> failures, List<string> warnings, CancellationToken ct)
    {
        // procSet currently unused for scheduled tasks (we don't resolve their
        // image to the process table today), but the parameter keeps the API
        // consistent with the Run/Services scans and leaves the door open for
        // future "task process is running" status.
        _ = procSet;
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = ExternalProcessRunner.Run(new ExternalProcessCommand("schtasks.exe")
            {
                Arguments = new[] { "/query", "/fo", "CSV", "/v", "/nh" },
                Timeout = TimeSpan.FromSeconds(15),
                OutputLimitChars = 512 * 1024,
                ErrorLimitChars = 64 * 1024,
            });
            var output = result.Output;
            if (!result.Success)
            {
                failures.Add(new ScanIssue(
                    "autorun:scheduled-tasks",
                    string.IsNullOrWhiteSpace(result.CombinedOutput)
                        ? "schtasks.exe could not enumerate scheduled tasks."
                        : result.CombinedOutput));
            }

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                var fields = ParseCsvLine(line);
                if (fields.Length < 9) continue;

                var taskName = fields[1].Trim('"');
                var status = fields[3].Trim('"');
                var action = fields[8].Trim('"');

                if (taskName.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(action) || action.Equals("N/A", StringComparison.OrdinalIgnoreCase)) continue;

                entries.Add(new AutorunEntry
                {
                    Name = Path.GetFileName(taskName),
                    Command = action,
                    Location = "Scheduled Task",
                    Description = taskName,
                    Type = AutorunType.ScheduledTask,
                    IsEnabled = !status.Equals("Disabled", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failures.Add(new ScanIssue("autorun:scheduled-tasks", ex.Message, ex.GetType().Name));
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Enable / disable / delete
    // ═══════════════════════════════════════════════════════

    public static bool DisableAutorun(AutorunEntry entry)
        => DisableAutorunDetailed(entry).Succeeded;

    public static AdministrativeMutationResult DisableAutorunDetailed(
        AutorunEntry entry,
        bool dryRun = false)
    {
        const string operation = "autorun-disable";
        var validation = ValidateEntry(entry, operation);
        if (validation != null) return validation;

        try
        {
            return entry.Type switch
            {
                AutorunType.RegistryRun or AutorunType.RegistryRunOnce =>
                    SetRunEntryEnabledDetailed(entry, enabled: false, dryRun),
                AutorunType.StartupFolder =>
                    MoveStartupEntryDetailed(entry, enable: false, dryRun),
                AutorunType.Service => DisableServiceAutorunDetailed(entry, dryRun),
                _ => AdministrativeMutationPolicy.Unsupported(
                    operation,
                    entry.Name,
                    "This autorun source does not expose a reversible disable handler."),
            };
        }
        catch (Exception ex)
        {
            return AdministrativeMutationPolicy.Failed(
                operation,
                entry.Name,
                "Unknown",
                ex.Message);
        }
    }

    public static bool DeleteAutorun(AutorunEntry entry)
        => DeleteAutorunDetailed(entry).Succeeded;

    public static AdministrativeMutationResult DeleteAutorunDetailed(
        AutorunEntry entry,
        bool dryRun = false)
    {
        const string operation = "autorun-delete";
        var validation = ValidateEntry(entry, operation);
        if (validation != null) return validation;

        try
        {
            switch (entry.Type)
            {
                case AutorunType.RegistryRun:
                case AutorunType.RegistryRunOnce:
                    return MapRegistryDeletion(
                        RegistryDeletion.DeleteValue(
                            $@"{entry.RegistryPath}\{entry.Name}",
                            operation,
                            dryRun),
                        operation,
                        entry.Name);

                case AutorunType.StartupFolder:
                    var path = File.Exists(entry.Command)
                        ? entry.Command
                        : entry.Command + ".disabled";
                    if (!File.Exists(path))
                        return AdministrativeMutationPolicy.Skipped(
                            operation,
                            entry.Name,
                            "Absent",
                            "The startup entry file is already absent.");
                    return MapFileDeletion(
                        new DeletionExecutor().Execute(
                            new DeletionRequest(path, Operation: operation),
                            new DeleteOptions(DryRun: dryRun)),
                        operation,
                        path);

                case AutorunType.Service:
                    return ServiceScanner.DeleteServiceDetailed(new ServiceEntry { Name = entry.Name }, dryRun);

                default:
                    return AdministrativeMutationPolicy.Unsupported(
                        operation,
                        entry.Name,
                        "Scheduled-task autoruns must be changed through the scheduled-task safety handler.");
            }
        }
        catch (Exception ex)
        {
            return AdministrativeMutationPolicy.Failed(
                operation,
                entry.Name,
                "Unknown",
                ex.Message);
        }
    }

    public static bool ToggleAutorun(AutorunEntry entry)
        => ToggleAutorunDetailed(entry).Succeeded;

    public static AdministrativeMutationResult ToggleAutorunDetailed(
        AutorunEntry entry,
        bool dryRun = false)
        => entry.IsEnabled
            ? DisableAutorunDetailed(entry, dryRun)
            : EnableAutorunDetailed(entry, dryRun);

    public static AdministrativeMutationResult EnableAutorunDetailed(
        AutorunEntry entry,
        bool dryRun = false)
    {
        const string operation = "autorun-enable";
        var validation = ValidateEntry(entry, operation);
        if (validation != null) return validation;

        try
        {
            return entry.Type switch
            {
                AutorunType.RegistryRun or AutorunType.RegistryRunOnce =>
                    SetRunEntryEnabledDetailed(entry, enabled: true, dryRun),
                AutorunType.StartupFolder =>
                    MoveStartupEntryDetailed(entry, enable: true, dryRun),
                AutorunType.Service => ServiceScanner.EnableServiceDetailed(
                    new ServiceEntry { Name = entry.Name },
                    dryRun),
                _ => AdministrativeMutationPolicy.Unsupported(
                    operation,
                    entry.Name,
                    "This autorun source does not expose a reversible enable handler."),
            };
        }
        catch (Exception ex)
        {
            return AdministrativeMutationPolicy.Failed(
                operation,
                entry.Name,
                "Unknown",
                ex.Message);
        }
    }

    /// <summary>
    /// Reversible enable/disable for HKCU/HKLM Run entries using the
    /// StartupApproved key pattern Windows uses for Task Manager's Startup tab.
    /// Never deletes the underlying Run value, so re-enabling always works.
    /// </summary>
    private static AdministrativeMutationResult SetRunEntryEnabledDetailed(
        AutorunEntry entry,
        bool enabled,
        bool dryRun)
    {
        var operation = enabled ? "autorun-enable" : "autorun-disable";
        var target = $"{entry.RegistryPath}\\{entry.Name}";
        var (hive, approvedPath) = ResolveStartupApprovedPath(entry);
        if (hive == null)
            return AdministrativeMutationPolicy.Unsupported(
                operation,
                target,
                "The autorun registry hive could not be resolved.");

        try
        {
            byte[]? existing;
            using (var current = hive.OpenSubKey(approvedPath))
                existing = current?.GetValue(entry.Name) as byte[];

            var before = JsonSerializer.Serialize(new
            {
                Hive = entry.RegistryPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU",
                Path = approvedPath,
                Value = entry.Name,
                Data = existing is null ? null : Convert.ToBase64String(existing),
            });
            var next = enabled ? StartupApprovedEnabled : StartupApprovedDisabled;
            var rollback = JsonSerializer.Serialize(new
            {
                Hive = entry.RegistryPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU",
                Path = approvedPath,
                Value = entry.Name,
                Data = existing is null ? null : Convert.ToBase64String(existing),
                Restore = existing is null ? "DeleteValue" : "SetBinaryValue",
            });

            if (dryRun)
                return AdministrativeMutationPolicy.Preview(
                    operation,
                    target,
                    before,
                    $"StartupApproved={next[0]:X2}",
                    rollback);

            using var key = hive.CreateSubKey(approvedPath, writable: true);
            if (key == null)
                return AdministrativeMutationPolicy.Failed(
                    operation,
                    target,
                    before,
                    "The StartupApproved registry key could not be opened.",
                    rollback);

            key.SetValue(entry.Name, next, RegistryValueKind.Binary);
            var after = key.GetValue(entry.Name) as byte[];
            if (after is null || after.Length == 0 || after[0] != next[0])
                return AdministrativeMutationPolicy.Failed(
                    operation,
                    target,
                    before,
                    "The StartupApproved value did not match the requested state.",
                    rollback);

            entry.IsEnabled = enabled;
            SystemRefreshNotifier.NotifyShellChanged();
            return AdministrativeMutationPolicy.Changed(
                operation,
                target,
                before,
                $"StartupApproved={after[0]:X2}",
                rollback);
        }
        catch (Exception ex)
        {
            return AdministrativeMutationPolicy.Failed(
                operation,
                target,
                "Unknown",
                ex.Message);
        }
    }

    private static AdministrativeMutationResult MoveStartupEntryDetailed(
        AutorunEntry entry,
        bool enable,
        bool dryRun)
    {
        var operation = enable ? "autorun-enable" : "autorun-disable";
        var source = enable ? entry.Command + ".disabled" : entry.Command;
        var destination = enable ? entry.Command : entry.Command + ".disabled";
        var target = entry.Command;

        if (!SafetyGuard.IsPathSafeToDelete(entry.Command))
            return AdministrativeMutationPolicy.Skipped(
                operation,
                target,
                "Present",
                "Protected startup-folder path.");

        var before = JsonSerializer.Serialize(new
        {
            Source = source,
            Destination = destination,
            SourceExists = File.Exists(source),
            DestinationExists = File.Exists(destination),
        });
        var rollback = JsonSerializer.Serialize(new
        {
            Move = $"MoveFile '{destination}' '{source}'",
            Source = source,
            Destination = destination,
        });

        if (!File.Exists(source))
            return AdministrativeMutationPolicy.Skipped(
                operation,
                target,
                before,
                enable
                    ? "The disabled startup entry file is absent."
                    : "The startup entry file is already disabled or absent.");
        if (File.Exists(destination))
            return AdministrativeMutationPolicy.Failed(
                operation,
                target,
                before,
                "The destination startup entry already exists.",
                rollback);

        if (dryRun)
            return AdministrativeMutationPolicy.Preview(
                operation,
                target,
                before,
                JsonSerializer.Serialize(new
                {
                    Source = source,
                    Destination = destination,
                    SourceExists = false,
                    DestinationExists = true,
                }),
                rollback);

        File.Move(source, destination);
        if (File.Exists(source) || !File.Exists(destination))
            return AdministrativeMutationPolicy.Failed(
                operation,
                target,
                before,
                "The startup entry move was not verified.",
                rollback);

        entry.IsEnabled = enable;
        SystemRefreshNotifier.NotifyShellChanged();
        return AdministrativeMutationPolicy.Changed(
            operation,
            target,
            before,
            JsonSerializer.Serialize(new
            {
                Source = source,
                Destination = destination,
                SourceExists = false,
                DestinationExists = true,
            }),
            rollback);
    }

    private static AdministrativeMutationResult DisableServiceAutorunDetailed(
        AutorunEntry entry,
        bool dryRun)
    {
        var result = ServiceScanner.DisableServiceDetailed(
            new ServiceEntry { Name = entry.Name },
            dryRun);
        if (result.Succeeded) entry.IsEnabled = false;
        return result;
    }

    private static AdministrativeMutationResult? ValidateEntry(
        AutorunEntry entry,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
            return AdministrativeMutationPolicy.Unsupported(
                operation,
                entry.Name,
                "The autorun entry has no stable name.");
        if (entry.Type == AutorunType.ScheduledTask)
            return AdministrativeMutationPolicy.Unsupported(
                operation,
                entry.Name,
                "Scheduled-task autoruns must be changed through the scheduled-task safety handler.");
        if (AutorunSafety.IsProtected(entry))
            return AdministrativeMutationPolicy.Skipped(
                operation,
                entry.Name,
                "Present",
                "Protected Windows autorun entry.");
        return null;
    }

    private static AdministrativeMutationResult MapRegistryDeletion(
        RegistryDeletionResult result,
        string operation,
        string target)
    {
        var rollback = JsonSerializer.Serialize(new
        {
            result.BackupPath,
            result.OperationId,
            result.BackupSha256,
        });
        return result.Status switch
        {
            RegistryDeletionStatus.Deleted => ChangedRegistryResult(result, operation, target, rollback),
            RegistryDeletionStatus.DryRun => AdministrativeMutationPolicy.Preview(
                operation,
                target,
                "Present",
                "Absent (planned)",
                rollback),
            RegistryDeletionStatus.SkippedUnsafePath => AdministrativeMutationPolicy.Skipped(
                operation,
                target,
                "Present",
                "Protected registry path."),
            RegistryDeletionStatus.SkippedMissing => AdministrativeMutationPolicy.Skipped(
                operation,
                target,
                "Absent",
                "The autorun registry value is already absent."),
            _ => AdministrativeMutationPolicy.Failed(
                operation,
                target,
                "Present",
                result.ErrorMessage ?? result.Status.ToString(),
                rollback),
        };
    }

    private static AdministrativeMutationResult ChangedRegistryResult(
        RegistryDeletionResult result,
        string operation,
        string target,
        string rollback)
    {
        SystemRefreshNotifier.NotifyShellChanged();
        return AdministrativeMutationPolicy.Changed(
            operation,
            target,
            "Present",
            "Absent",
            rollback);
    }

    private static AdministrativeMutationResult MapFileDeletion(
        DeletionResult result,
        string operation,
        string target)
    {
        var rollback = JsonSerializer.Serialize(new
        {
            result.Path,
            result.Recoverable,
            result.Outcome,
            Restore = result.Recoverable ? "Restore from Recycle Bin" : "Unavailable",
        });
        return result.Outcome switch
        {
            DeletionOutcomeKind.Recycled or
            DeletionOutcomeKind.PermanentlyDeleted or
            DeletionOutcomeKind.SecurelyDeleted => ChangedFileResult(result, operation, target, rollback),
            DeletionOutcomeKind.Preview => AdministrativeMutationPolicy.Preview(
                operation,
                target,
                "Present",
                "Absent (planned)",
                rollback),
            DeletionOutcomeKind.Skipped => AdministrativeMutationPolicy.Skipped(
                operation,
                target,
                "Present",
                result.Reason ?? "The startup entry was skipped."),
            DeletionOutcomeKind.Queued => AdministrativeMutationPolicy.Unsupported(
                operation,
                target,
                "The startup entry was queued and is not confirmed removed."),
            _ => AdministrativeMutationPolicy.Failed(
                operation,
                target,
                "Present",
                result.Reason ?? result.Outcome.ToString(),
                rollback),
        };
    }

    private static AdministrativeMutationResult ChangedFileResult(
        DeletionResult result,
        string operation,
        string target,
        string rollback)
    {
        SystemRefreshNotifier.NotifyShellChanged();
        return AdministrativeMutationPolicy.Changed(
            operation,
            target,
            "Present",
            "Absent",
            rollback);
    }

    private static (RegistryKey? hive, string path) ResolveStartupApprovedPath(AutorunEntry entry)
    {
        var isWow64 = entry.RegistryPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase);
        var baseSubKey = isWow64
            ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"
            : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        var hive = entry.RegistryPath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
            ? global::Microsoft.Win32.Registry.LocalMachine
            : global::Microsoft.Win32.Registry.CurrentUser;

        return (hive, baseSubKey);
    }

    private static (RegistryKey? hive, string subPath) ResolveHiveAndPath(string registryPath)
    {
        if (registryPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return (global::Microsoft.Win32.Registry.LocalMachine, registryPath[5..]);
        if (registryPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            return (global::Microsoft.Win32.Registry.CurrentUser, registryPath[5..]);
        return (null, string.Empty);
    }

    // ═══════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════

    private static string ExtractExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command;
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static string GetFilePublisher(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return "";
            return FileVersionInfo.GetVersionInfo(filePath).CompanyName ?? "";
        }
        catch { return ""; }
    }

    // Legacy per-entry IsProcessRunning was replaced by ProcessNameSet to
    // eliminate per-call Process handle leaks and O(N*M) re-enumeration.

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var inQuotes = false;
        var current = new StringBuilder();

        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; current.Append(c); }
            else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
