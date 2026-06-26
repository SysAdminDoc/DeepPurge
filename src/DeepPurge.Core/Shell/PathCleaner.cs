using System.ComponentModel;
using System.Runtime.CompilerServices;
// Using fully-qualified Microsoft.Win32.Registry to avoid collision with DeepPurge.Core.Registry namespace.

namespace DeepPurge.Core.Shell;

public class PathEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Directory { get; set; } = "";
    public string Source { get; set; } = ""; // "System" or "User"
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

/// <summary>
/// Scans the system and user PATH environment variables for entries that
/// point to non-existent directories. These accumulate after programs are
/// uninstalled and can slow process startup (the loader probes every PATH
/// entry when resolving DLLs).
/// </summary>
public static class PathCleaner
{
    private static readonly string SystemRoot =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Enumerate all PATH entries from both system and user scopes,
    /// flagging any that point to non-existent directories.
    /// </summary>
    public static List<PathEntry> ScanPathEntries(bool orphanedOnly = false)
    {
        var entries = new List<PathEntry>();

        // System PATH
        ScanScope(entries, orphanedOnly, "System",
            global::Microsoft.Win32.Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
            "Path");

        // User PATH
        ScanScope(entries, orphanedOnly, "User",
            global::Microsoft.Win32.Registry.CurrentUser,
            @"Environment",
            "Path");

        return entries;
    }

    /// <summary>
    /// Remove orphaned entries from the PATH environment variable.
    /// Only removes entries that are both selected and orphaned.
    /// Returns the number of entries removed.
    /// </summary>
    public static int RemoveOrphanedEntries(IEnumerable<PathEntry> entries)
    {
        var toRemove = entries.Where(e => e.IsSelected && e.IsOrphaned).ToList();
        if (toRemove.Count == 0) return 0;

        int removed = 0;

        // Group by source scope
        var systemRemoves = toRemove.Where(e => e.Source == "System")
            .Select(e => e.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userRemoves = toRemove.Where(e => e.Source == "User")
            .Select(e => e.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (systemRemoves.Count > 0)
            removed += RemoveFromScope(
                global::Microsoft.Win32.Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                "Path",
                systemRemoves);

        if (userRemoves.Count > 0)
            removed += RemoveFromScope(
                global::Microsoft.Win32.Registry.CurrentUser,
                @"Environment",
                "Path",
                userRemoves);

        return removed;
    }

    // ===============================================================
    //  Internals
    // ===============================================================

    private static void ScanScope(List<PathEntry> entries, bool orphanedOnly,
        string source, global::Microsoft.Win32.RegistryKey root, string subKeyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            if (key == null) return;

            var raw = key.GetValue(valueName, "", global::Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                         ?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return;

            var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var dir = part.Trim();
                if (string.IsNullOrEmpty(dir)) continue;

                var expanded = Environment.ExpandEnvironmentVariables(dir);
                var isOrphaned = IsOrphanedPathEntry(expanded);

                if (orphanedOnly && !isOrphaned) continue;

                entries.Add(new PathEntry
                {
                    Directory = dir,
                    Source = source,
                    IsOrphaned = isOrphaned,
                });
            }
        }
        catch { /* registry unreadable */ }
    }

    private static bool IsOrphanedPathEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Never flag system directories as orphaned.
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith(SystemRoot.ToLowerInvariant()))
            return false;

        // Only flag fully-qualified paths.
        if (!Path.IsPathRooted(path)) return false;

        return !System.IO.Directory.Exists(path);
    }

    private static int RemoveFromScope(global::Microsoft.Win32.RegistryKey root, string subKeyPath,
        string valueName, HashSet<string> toRemove)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath, writable: true);
            if (key == null) return 0;

            // Preserve the original value kind (REG_EXPAND_SZ vs REG_SZ).
            var kind = key.GetValueKind(valueName);
            var raw = key.GetValue(valueName, "", global::Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                         ?.ToString() ?? "";

            var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int removed = 0;
            var kept = new List<string>();

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (toRemove.Contains(trimmed))
                    removed++;
                else
                    kept.Add(trimmed);
            }

            if (removed > 0)
            {
                var newPath = string.Join(';', kept);
                key.SetValue(valueName, newPath, kind);
            }

            return removed;
        }
        catch { return 0; }
    }
}
