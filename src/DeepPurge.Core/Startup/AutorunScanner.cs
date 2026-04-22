using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DeepPurge.Core.Security;
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

public static class AutorunScanner
{
    // "Enabled" and "Disabled" blobs Windows writes into StartupApproved. The
    // first byte is the flag (2 = enabled, 3 = disabled); the following 11 bytes
    // are a FILETIME that Windows uses for UI display.
    private static readonly byte[] StartupApprovedEnabled = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] StartupApprovedDisabled = { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static List<AutorunEntry> GetAllAutoruns()
    {
        var entries = new List<AutorunEntry>();
        ScanRegistryRun(entries);
        ScanStartupFolders(entries);
        ScanServices(entries);
        ScanScheduledTasks(entries);
        PopulateSignatures(entries);
        return entries;
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

    private static void ScanRegistryRun(List<AutorunEntry> entries)
    {
        foreach (var loc in RunLocations)
        {
            try
            {
                using var key = loc.Hive.OpenSubKey(loc.SubKey);
                if (key == null) continue;

                foreach (var name in key.GetValueNames())
                {
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
                        IsRunning = IsProcessRunning(exePath),
                    });
                }
            }
            catch { /* permission/missing - skip */ }
        }

        ApplyStartupApprovedFlags(entries);
    }

    /// <summary>
    /// Windows stores the enabled/disabled flag for Run entries in
    /// HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run
    /// and mirrored paths. Cross-reference so the UI shows the correct toggle.
    /// </summary>
    private static void ApplyStartupApprovedFlags(List<AutorunEntry> entries)
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
            catch { /* skip */ }
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Startup folders
    // ═══════════════════════════════════════════════════════

    private static void ScanStartupFolders(List<AutorunEntry> entries)
    {
        var folders = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "User Startup Folder"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "All Users Startup Folder"),
        };

        foreach (var (folder, location) in folders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(folder))
                {
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
            catch { /* skip */ }
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Services (Win32 only, autostart only)
    // ═══════════════════════════════════════════════════════

    private static void ScanServices(List<AutorunEntry> entries)
    {
        try
        {
            using var servicesKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
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
                        IsRunning = IsProcessRunning(ExtractExePath(imagePath)),
                    });
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
    }

    // ═══════════════════════════════════════════════════════
    //  Scheduled tasks (non-Microsoft only)
    // ═══════════════════════════════════════════════════════

    private static void ScanScheduledTasks(List<AutorunEntry> entries)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/query /fo CSV /v /nh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
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
        catch { /* schtasks unavailable - skip */ }
    }

    // ═══════════════════════════════════════════════════════
    //  Enable / disable / delete
    // ═══════════════════════════════════════════════════════

    public static bool DisableAutorun(AutorunEntry entry)
    {
        try
        {
            switch (entry.Type)
            {
                case AutorunType.RegistryRun:
                case AutorunType.RegistryRunOnce:
                    return SetRunEntryEnabled(entry, enabled: false);

                case AutorunType.StartupFolder:
                    if (File.Exists(entry.Command))
                    {
                        var disabledPath = entry.Command + ".disabled";
                        if (File.Exists(disabledPath)) File.Delete(disabledPath);
                        File.Move(entry.Command, disabledPath);
                        entry.IsEnabled = false;
                    }
                    return true;

                case AutorunType.Service:
                    if (RunScConfig(entry.Name, "disabled"))
                    {
                        entry.IsEnabled = false;
                        return true;
                    }
                    return false;
            }
        }
        catch { /* fall through */ }
        return false;
    }

    public static bool DeleteAutorun(AutorunEntry entry)
    {
        try
        {
            switch (entry.Type)
            {
                case AutorunType.RegistryRun:
                case AutorunType.RegistryRunOnce:
                    var (hive, path) = ResolveHiveAndPath(entry.RegistryPath);
                    if (hive == null) return false;
                    using (var key = hive.OpenSubKey(path, writable: true))
                        key?.DeleteValue(entry.Name, throwOnMissingValue: false);
                    return true;

                case AutorunType.StartupFolder:
                    if (File.Exists(entry.Command)) { File.Delete(entry.Command); return true; }
                    var disabled = entry.Command + ".disabled";
                    if (File.Exists(disabled)) { File.Delete(disabled); return true; }
                    return false;

                case AutorunType.Service:
                    return RunSc($"delete \"{entry.Name}\"");
            }
        }
        catch { /* fall through */ }
        return false;
    }

    public static bool ToggleAutorun(AutorunEntry entry)
    {
        if (entry.IsEnabled) return DisableAutorun(entry);

        try
        {
            switch (entry.Type)
            {
                case AutorunType.RegistryRun:
                case AutorunType.RegistryRunOnce:
                    return SetRunEntryEnabled(entry, enabled: true);

                case AutorunType.StartupFolder:
                    var disabledPath = entry.Command + ".disabled";
                    if (File.Exists(disabledPath))
                    {
                        File.Move(disabledPath, entry.Command);
                        entry.IsEnabled = true;
                        return true;
                    }
                    return false;

                case AutorunType.Service:
                    if (RunScConfig(entry.Name, "auto"))
                    {
                        entry.IsEnabled = true;
                        return true;
                    }
                    return false;
            }
        }
        catch { /* fall through */ }
        return false;
    }

    /// <summary>
    /// Reversible enable/disable for HKCU/HKLM Run entries using the
    /// StartupApproved key pattern Windows uses for Task Manager's Startup tab.
    /// Never deletes the underlying Run value, so re-enabling always works.
    /// </summary>
    private static bool SetRunEntryEnabled(AutorunEntry entry, bool enabled)
    {
        var (hive, approvedPath) = ResolveStartupApprovedPath(entry);
        if (hive == null) return false;

        using var key = hive.CreateSubKey(approvedPath, writable: true);
        if (key == null) return false;

        key.SetValue(entry.Name, enabled ? StartupApprovedEnabled : StartupApprovedDisabled, RegistryValueKind.Binary);
        entry.IsEnabled = enabled;
        return true;
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

    private static bool RunScConfig(string serviceName, string startType)
        => RunSc($"config \"{serviceName}\" start= {startType}");

    private static bool RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

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

    private static bool IsProcessRunning(string exePath)
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(exePath);
            if (string.IsNullOrEmpty(exeName)) return false;
            return Process.GetProcessesByName(exeName).Length > 0;
        }
        catch { return false; }
    }

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
