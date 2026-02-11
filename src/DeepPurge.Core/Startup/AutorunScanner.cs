using System.Diagnostics;


namespace DeepPurge.Core.Startup;

public class AutorunEntry
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Location { get; set; } = "";
    public string RegistryPath { get; set; } = "";
    public AutorunType Type { get; set; }
    public bool IsEnabled { get; set; }
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsRunning { get; set; }
}

public enum AutorunType
{
    RegistryRun,
    RegistryRunOnce,
    StartupFolder,
    Service,
    ScheduledTask
}

public static class AutorunScanner
{
    public static List<AutorunEntry> GetAllAutoruns()
    {
        var entries = new List<AutorunEntry>();
        ScanRegistryRun(entries);
        ScanStartupFolders(entries);
        ScanServices(entries);
        ScanScheduledTasks(entries);
        return entries;
    }

    private static void ScanRegistryRun(List<AutorunEntry> entries)
    {
        var locations = new[]
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", global::Microsoft.Win32.Registry.LocalMachine, "HKLM\\...\\Run", AutorunType.RegistryRun),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", global::Microsoft.Win32.Registry.LocalMachine, "HKLM\\...\\RunOnce", AutorunType.RegistryRunOnce),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", global::Microsoft.Win32.Registry.CurrentUser, "HKCU\\...\\Run", AutorunType.RegistryRun),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", global::Microsoft.Win32.Registry.CurrentUser, "HKCU\\...\\RunOnce", AutorunType.RegistryRunOnce),
            (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", global::Microsoft.Win32.Registry.LocalMachine, "HKLM\\...\\Run (32-bit)", AutorunType.RegistryRun),
        };

        foreach (var (path, hive, displayLoc, type) in locations)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;

                var hivePrefix = hive == global::Microsoft.Win32.Registry.LocalMachine ? "HKLM" : "HKCU";
                foreach (var name in key.GetValueNames())
                {
                    var command = key.GetValue(name) as string ?? "";
                    if (string.IsNullOrEmpty(command)) continue;

                    var exePath = ExtractExePath(command);
                    entries.Add(new AutorunEntry
                    {
                        Name = name,
                        Command = command,
                        Location = displayLoc,
                        RegistryPath = $@"{hivePrefix}\{path}",
                        Type = type,
                        IsEnabled = true,
                        Publisher = GetFilePublisher(exePath),
                        IsRunning = IsProcessRunning(exePath),
                    });
                }
            }
            catch { }
        }

        // Also check disabled items (stored separately by Windows)
        try
        {
            using var approvedKey = global::Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            if (approvedKey != null)
            {
                foreach (var name in approvedKey.GetValueNames())
                {
                    var data = approvedKey.GetValue(name) as byte[];
                    if (data != null && data.Length >= 1)
                    {
                        var existing = entries.FirstOrDefault(e =>
                            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                            e.Type == AutorunType.RegistryRun);
                        if (existing != null)
                            existing.IsEnabled = data[0] != 0x03; // 0x03 = disabled
                    }
                }
            }
        }
        catch { }
    }

    private static void ScanStartupFolders(List<AutorunEntry> entries)
    {
        var folders = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "User Startup Folder"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "All Users Startup Folder"),
        };

        foreach (var (folder, location) in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    var ext = Path.GetExtension(file).ToLower();
                    if (ext is ".lnk" or ".bat" or ".cmd" or ".exe" or ".vbs")
                    {
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
            }
            catch { }
        }
    }

    private static void ScanServices(List<AutorunEntry> entries)
    {
        try
        {
            using var servicesKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return;

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var svcKey = servicesKey.OpenSubKey(serviceName);
                    if (svcKey == null) continue;

                    var startType = (int)(svcKey.GetValue("Start") ?? 4);
                    if (startType > 2) continue; // Only auto-start (0=Boot, 1=System, 2=Automatic)

                    var svcType = (int)(svcKey.GetValue("Type") ?? 0);
                    // Skip kernel/filesystem drivers (types 1, 2, 8)
                    if (svcType is 1 or 2 or 8) continue;

                    var imagePath = svcKey.GetValue("ImagePath") as string ?? "";
                    var displayName = svcKey.GetValue("DisplayName") as string ?? serviceName;
                    var description = svcKey.GetValue("Description") as string ?? "";

                    // Skip svchost-hosted system services without a useful display name
                    if (string.IsNullOrEmpty(imagePath)) continue;

                    entries.Add(new AutorunEntry
                    {
                        Name = serviceName,
                        Description = displayName is string dn ? dn : serviceName,
                        Command = imagePath,
                        Location = "Windows Service",
                        Type = AutorunType.Service,
                        IsEnabled = startType <= 2,
                        IsRunning = IsProcessRunning(ExtractExePath(imagePath)),
                    });
                }
                catch { }
            }
        }
        catch { }
    }

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

                if (taskName.StartsWith(@"\Microsoft\")) continue; // Skip built-in Windows tasks
                if (string.IsNullOrEmpty(action) || action == "N/A") continue;

                entries.Add(new AutorunEntry
                {
                    Name = Path.GetFileName(taskName),
                    Command = action,
                    Location = "Scheduled Task",
                    Description = taskName,
                    Type = AutorunType.ScheduledTask,
                    IsEnabled = status != "Disabled",
                });
            }
        }
        catch { }
    }

    public static bool DisableAutorun(AutorunEntry entry)
    {
        try
        {
            switch (entry.Type)
            {
                case AutorunType.RegistryRun:
                case AutorunType.RegistryRunOnce:
                    // Move to a "disabled" backup key
                    var hive = entry.RegistryPath.StartsWith("HKLM") ? global::Microsoft.Win32.Registry.LocalMachine : global::Microsoft.Win32.Registry.CurrentUser;
                    var path = entry.RegistryPath.StartsWith("HKLM") ? entry.RegistryPath[5..] : entry.RegistryPath[5..];
                    using (var key = hive.OpenSubKey(path, true))
                    {
                        if (key != null)
                        {
                            key.DeleteValue(entry.Name, false);
                            entry.IsEnabled = false;
                        }
                    }
                    return true;

                case AutorunType.StartupFolder:
                    if (File.Exists(entry.Command))
                    {
                        var disabledPath = entry.Command + ".disabled";
                        File.Move(entry.Command, disabledPath);
                        entry.IsEnabled = false;
                    }
                    return true;

                case AutorunType.Service:
                    var scPsi = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = $"config \"{entry.Name}\" start= disabled",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (var p = Process.Start(scPsi))
                    {
                        p?.WaitForExit(10000);
                        entry.IsEnabled = false;
                    }
                    return true;
            }
        }
        catch { }
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
                    var hive = entry.RegistryPath.StartsWith("HKLM") ? global::Microsoft.Win32.Registry.LocalMachine : global::Microsoft.Win32.Registry.CurrentUser;
                    var path = entry.RegistryPath.StartsWith("HKLM") ? entry.RegistryPath[5..] : entry.RegistryPath[5..];
                    using (var key = hive.OpenSubKey(path, true))
                    {
                        key?.DeleteValue(entry.Name, false);
                    }
                    return true;

                case AutorunType.StartupFolder:
                    var filePath = entry.Command;
                    if (File.Exists(filePath)) { File.Delete(filePath); return true; }
                    var disabled = filePath + ".disabled";
                    if (File.Exists(disabled)) { File.Delete(disabled); return true; }
                    return false;

                case AutorunType.Service:
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sc.exe", Arguments = $"delete \"{entry.Name}\"",
                        UseShellExecute = false, CreateNoWindow = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        p?.WaitForExit(10000);
                        return p?.ExitCode == 0;
                    }
            }
        }
        catch { }
        return false;
    }

    public static bool ToggleAutorun(AutorunEntry entry)
    {
        // Toggle between enabled/disabled state
        if (entry.IsEnabled)
            return DisableAutorun(entry);

        // Re-enable: for registry entries, re-add the value
        try
        {
            switch (entry.Type)
            {
                case AutorunType.RegistryRun:
                case AutorunType.RegistryRunOnce:
                    var hive = entry.RegistryPath.StartsWith("HKLM") ? global::Microsoft.Win32.Registry.LocalMachine : global::Microsoft.Win32.Registry.CurrentUser;
                    var path = entry.RegistryPath.StartsWith("HKLM") ? entry.RegistryPath[5..] : entry.RegistryPath[5..];
                    using (var key = hive.OpenSubKey(path, true))
                    {
                        key?.SetValue(entry.Name, entry.Command);
                        entry.IsEnabled = true;
                    }
                    return true;
                case AutorunType.StartupFolder:
                    var disabledPath = entry.Command + ".disabled";
                    if (File.Exists(disabledPath))
                    {
                        File.Move(disabledPath, entry.Command);
                        entry.IsEnabled = true;
                    }
                    return true;
                case AutorunType.Service:
                    var psi = new ProcessStartInfo
                    {
                        FileName = "sc.exe", Arguments = $"config \"{entry.Name}\" start= auto",
                        UseShellExecute = false, CreateNoWindow = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        p?.WaitForExit(10000);
                        entry.IsEnabled = true;
                    }
                    return true;
            }
        }
        catch { }
        return false;
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
            if (!File.Exists(filePath)) return "";
            var info = FileVersionInfo.GetVersionInfo(filePath);
            return info.CompanyName ?? "";
        }
        catch { return ""; }
    }

    private static bool IsProcessRunning(string exePath)
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(exePath);
            return Process.GetProcessesByName(exeName).Length > 0;
        }
        catch { return false; }
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; current.Append(c); }
            else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
