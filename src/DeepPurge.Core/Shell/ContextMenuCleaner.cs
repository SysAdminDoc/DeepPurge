using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeepPurge.Core.Shell;

public class ContextMenuEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string RegistryPath { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsOrphaned { get; set; }
    public string Status => IsOrphaned ? "Orphaned" : "Valid";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ContextMenuCleaner
{
    private static readonly string[] ShellLocations =
    {
        @"*\shell",                                             // All files (verbs)
        @"*\shellex\ContextMenuHandlers",                       // All files (handlers)
        @"Directory\shell",                                     // Folders
        @"Directory\shellex\ContextMenuHandlers",               // Folders (handlers)
        @"Directory\Background\shell",                          // Folder background
        @"Directory\Background\shellex\ContextMenuHandlers",    // Folder background (handlers)
        @"Drive\shell",                                         // Drives
        @"DesktopBackground\shell",                             // Desktop
        @"DesktopBackground\shellex\ContextMenuHandlers",       // Desktop (handlers)
    };

    /// <summary>
    /// Commands that always resolve through a system executable — we don't
    /// treat these as orphaned even if File.Exists can't resolve them.
    /// </summary>
    private static readonly string[] SystemCommandMarkers =
    {
        "rundll32", "cmd.exe", "powershell", "pwsh",
        "explorer.exe", "msiexec", "mmc.exe", "%systemroot%",
    };

    public static List<ContextMenuEntry> ScanContextMenuEntries()
    {
        var entries = new List<ContextMenuEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ShellLocations)
            ScanShellKey(entries, seen, path, FriendlyLocation(path));

        ScanFileTypeShellEntries(entries, seen);
        return entries;
    }

    public static int RemoveOrphanedEntries(IEnumerable<ContextMenuEntry> entries)
    {
        int removed = 0;
        foreach (var entry in entries.Where(e => e.IsSelected && e.IsOrphaned))
        {
            try
            {
                var parent = GetParentPath(entry.RegistryPath);
                if (string.IsNullOrEmpty(parent)) continue;

                using var key = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(parent, writable: true);
                if (key == null) continue;

                var subName = Path.GetFileName(entry.RegistryPath);
                key.DeleteSubKeyTree(subName, throwOnMissingSubKey: false);
                removed++;
            }
            catch { /* skip unreachable entries */ }
        }
        return removed;
    }

    // ═══════════════════════════════════════════════════════

    private static void ScanShellKey(List<ContextMenuEntry> entries, HashSet<string> seen,
        string subPath, string location)
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
                    string command;
                    bool isOrphaned;

                    using var cmdKey = sub.OpenSubKey("command");
                    if (cmdKey != null)
                    {
                        command = cmdKey.GetValue(null)?.ToString() ?? "";
                        isOrphaned = !string.IsNullOrEmpty(command) && !CommandTargetExists(command);
                    }
                    else
                    {
                        // Shell-extension handler - the default value should be a CLSID.
                        var clsid = sub.GetValue(null)?.ToString() ?? "";
                        if (!clsid.StartsWith("{")) continue;
                        isOrphaned = !ClsidExists(clsid);
                        command = $"CLSID: {clsid}";
                    }

                    var regPath = $"{subPath}\\{name}";
                    if (!seen.Add(regPath)) continue;

                    entries.Add(new ContextMenuEntry
                    {
                        Name = displayName,
                        Command = command,
                        RegistryPath = regPath,
                        Location = location,
                        IsOrphaned = isOrphaned,
                        IsSelected = false,
                    });
                }
                catch { /* skip unreadable child */ }
            }
        }
        catch { /* skip unreachable root */ }
    }

    private static void ScanFileTypeShellEntries(List<ContextMenuEntry> entries, HashSet<string> seen)
    {
        try
        {
            using var root = global::Microsoft.Win32.Registry.ClassesRoot;
            foreach (var name in root.GetSubKeyNames())
            {
                if (!name.StartsWith('.')) continue;
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

                            if (!IsCommandOrphaned(cmd)) continue; // Only show orphaned file-type verbs.

                            var regPath = $"{progId}\\shell\\{shellName}";
                            if (!seen.Add(regPath)) continue;

                            entries.Add(new ContextMenuEntry
                            {
                                Name = shellSub?.GetValue(null)?.ToString() ?? shellName,
                                Command = cmd,
                                RegistryPath = regPath,
                                Location = $"File Type ({name})",
                                IsOrphaned = true,
                                IsSelected = false,
                            });
                        }
                        catch { /* skip */ }
                    }
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
    }

    // ═══════════════════════════════════════════════════════
    //  Orphan detection
    // ═══════════════════════════════════════════════════════

    private static bool IsCommandOrphaned(string command) => !CommandTargetExists(command);

    private static bool CommandTargetExists(string command)
    {
        if (string.IsNullOrEmpty(command)) return false;

        var path = command.Trim();
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

        if (string.IsNullOrEmpty(path)) return false;

        path = Environment.ExpandEnvironmentVariables(path);
        var lower = path.ToLowerInvariant();

        // System launchers - always consider valid.
        foreach (var marker in SystemCommandMarkers)
            if (lower.Contains(marker)) return true;

        return File.Exists(path);
    }

    private static bool ClsidExists(string clsid)
    {
        try
        {
            var relative = $"CLSID\\{clsid}\\InprocServer32";
            using var key = global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(relative)
                         ?? global::Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($"WOW6432Node\\{relative}");
            if (key == null) return false;

            var dll = key.GetValue(null)?.ToString() ?? "";
            if (string.IsNullOrEmpty(dll)) return true; // Registered CLSID without path info — assume valid.
            return File.Exists(Environment.ExpandEnvironmentVariables(dll));
        }
        catch { return true; } // Err on safe side: treat ambiguous as valid.
    }

    // ═══════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════

    private static string FriendlyLocation(string path) => path switch
    {
        @"*\shell" => "All Files",
        @"*\shellex\ContextMenuHandlers" => "All Files (Handlers)",
        @"Directory\shell" => "Folders",
        @"Directory\shellex\ContextMenuHandlers" => "Folders (Handlers)",
        @"Directory\Background\shell" => "Folder Background",
        @"Directory\Background\shellex\ContextMenuHandlers" => "Folder Background (Handlers)",
        @"Drive\shell" => "Drives",
        @"DesktopBackground\shell" => "Desktop",
        @"DesktopBackground\shellex\ContextMenuHandlers" => "Desktop (Handlers)",
        _ => path,
    };

    private static string GetParentPath(string path)
    {
        var lastSlash = path.LastIndexOf('\\');
        return lastSlash > 0 ? path[..lastSlash] : path;
    }
}
