namespace DeepPurge.Core.Shell;

public class ContextMenuEntry
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string RegistryPath { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsOrphaned { get; set; }
    public bool IsSelected { get; set; }
    public string Status => IsOrphaned ? "Orphaned" : "Valid";
}

public static class ContextMenuCleaner
{
    public static List<ContextMenuEntry> ScanContextMenuEntries()
    {
        var entries = new List<ContextMenuEntry>();

        // Scan shell extensions from multiple locations
        ScanShellKey(entries, @"*\shell", "All Files");
        ScanShellKey(entries, @"*\shellex\ContextMenuHandlers", "All Files (Handlers)");
        ScanShellKey(entries, @"Directory\shell", "Folders");
        ScanShellKey(entries, @"Directory\shellex\ContextMenuHandlers", "Folders (Handlers)");
        ScanShellKey(entries, @"Directory\Background\shell", "Folder Background");
        ScanShellKey(entries, @"Directory\Background\shellex\ContextMenuHandlers", "Folder Background (Handlers)");
        ScanShellKey(entries, @"Drive\shell", "Drives");
        ScanShellKey(entries, @"DesktopBackground\shell", "Desktop");
        ScanShellKey(entries, @"DesktopBackground\shellex\ContextMenuHandlers", "Desktop (Handlers)");

        // File type associations
        ScanFileTypeShellEntries(entries);

        return entries;
    }

    public static int RemoveOrphanedEntries(IEnumerable<ContextMenuEntry> entries)
    {
        int removed = 0;
        foreach (var entry in entries.Where(e => e.IsSelected && e.IsOrphaned))
        {
            try
            {
                using var key = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
                    GetParentPath(entry.RegistryPath), true);
                if (key != null)
                {
                    var subName = Path.GetFileName(entry.RegistryPath);
                    key.DeleteSubKeyTree(subName, false);
                    removed++;
                }
            }
            catch { }
        }
        return removed;
    }

    private static void ScanShellKey(List<ContextMenuEntry> entries, string subPath, string location)
    {
        try
        {
            using var key = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(subPath);
            if (key == null) return;

            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub == null) continue;

                    var displayName = sub.GetValue(null)?.ToString() ?? name;
                    var command = "";
                    bool isOrphaned = false;

                    // Check for command subkey
                    using var cmdKey = sub.OpenSubKey("command");
                    if (cmdKey != null)
                    {
                        command = cmdKey.GetValue(null)?.ToString() ?? "";
                        isOrphaned = !string.IsNullOrEmpty(command) && !CommandTargetExists(command);
                    }
                    else
                    {
                        // Shell extension handler - check CLSID
                        var clsid = sub.GetValue(null)?.ToString() ?? "";
                        if (clsid.StartsWith("{"))
                        {
                            isOrphaned = !ClsidExists(clsid);
                            command = $"CLSID: {clsid}";
                        }
                    }

                    entries.Add(new ContextMenuEntry
                    {
                        Name = displayName,
                        Command = command,
                        RegistryPath = $"{subPath}\\{name}",
                        Location = location,
                        IsOrphaned = isOrphaned,
                        IsSelected = isOrphaned,
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ScanFileTypeShellEntries(List<ContextMenuEntry> entries)
    {
        try
        {
            using var root = global::Microsoft.Win32.Registry.ClassesRoot;
            foreach (var name in root.GetSubKeyNames())
            {
                if (!name.StartsWith(".")) continue;
                try
                {
                    using var extKey = root.OpenSubKey(name);
                    var progId = extKey?.GetValue(null)?.ToString();
                    if (string.IsNullOrEmpty(progId)) continue;

                    using var progKey = root.OpenSubKey($"{progId}\\shell");
                    if (progKey == null) continue;

                    foreach (var shellName in progKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var shellSub = progKey.OpenSubKey(shellName);
                            using var cmdKey = shellSub?.OpenSubKey("command");
                            var cmd = cmdKey?.GetValue(null)?.ToString() ?? "";
                            if (string.IsNullOrEmpty(cmd)) continue;

                            var isOrphaned = !CommandTargetExists(cmd);
                            if (!isOrphaned) continue; // Only show orphaned for file types

                            entries.Add(new ContextMenuEntry
                            {
                                Name = shellSub?.GetValue(null)?.ToString() ?? shellName,
                                Command = cmd,
                                RegistryPath = $"{progId}\\shell\\{shellName}",
                                Location = $"File Type ({name})",
                                IsOrphaned = true,
                                IsSelected = true,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool CommandTargetExists(string command)
    {
        if (string.IsNullOrEmpty(command)) return false;
        var path = command.Trim();

        // Extract path from quoted or unquoted command
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            if (end > 0) path = path[1..end];
        }
        else
        {
            var space = path.IndexOf(' ');
            if (space > 0) path = path[..space];
        }

        // Expand environment variables
        path = Environment.ExpandEnvironmentVariables(path);

        // Ignore system built-ins
        if (path.Contains("rundll32", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return true;

        return File.Exists(path);
    }

    private static bool ClsidExists(string clsid)
    {
        try
        {
            using var key = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}\\InprocServer32");
            if (key == null)
            {
                using var key64 = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"WOW6432Node\\CLSID\\{clsid}\\InprocServer32");
                if (key64 == null) return false;
                var dll64 = key64.GetValue(null)?.ToString() ?? "";
                return !string.IsNullOrEmpty(dll64) && File.Exists(Environment.ExpandEnvironmentVariables(dll64));
            }
            var dll = key.GetValue(null)?.ToString() ?? "";
            return !string.IsNullOrEmpty(dll) && File.Exists(Environment.ExpandEnvironmentVariables(dll));
        }
        catch { return true; } // Assume valid if we can't check
    }

    private static string GetParentPath(string path)
    {
        var lastSlash = path.LastIndexOf('\\');
        return lastSlash > 0 ? path[..lastSlash] : path;
    }
}
