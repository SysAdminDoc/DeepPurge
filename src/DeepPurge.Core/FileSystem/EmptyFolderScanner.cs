using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeepPurge.Core.FileSystem;

public class EmptyFolderInfo : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Path { get; set; } = "";
    public string ParentFolder { get; set; } = "";
    public DateTime LastModified { get; set; }
    public string LastModifiedDisplay => LastModified == default ? "" : LastModified.ToString("yyyy-MM-dd HH:mm");

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class EmptyFolderScanner
{
    private const int DefaultMaxDepth = 6;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", ".git", ".svn", ".hg",
        "node_modules", "__pycache__", ".vs", "AppData", "Windows",
        "WindowsApps", "Program Files", "Program Files (x86)", "OneDrive",
    };

    public static List<EmptyFolderInfo> ScanForEmptyFolders(string rootPath)
    {
        var results = new List<EmptyFolderInfo>();
        if (!Directory.Exists(rootPath)) return results;
        try { ScanDirectory(rootPath, results, depth: 0, maxDepth: DefaultMaxDepth); }
        catch { /* skip */ }
        return results.OrderBy(f => f.Path).ToList();
    }

    public static List<EmptyFolderInfo> ScanCommonLocations()
    {
        var results = new List<EmptyFolderInfo>();
        var paths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        foreach (var path in paths.Distinct().Where(Directory.Exists))
        {
            try { ScanDirectory(path, results, depth: 0, maxDepth: 4); }
            catch { /* skip */ }
        }

        return results
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Path)
            .ToList();
    }

    public static int DeleteEmptyFolders(IEnumerable<EmptyFolderInfo> folders)
    {
        int deleted = 0;
        // Delete the deepest paths first so parent folders also become eligible.
        foreach (var folder in folders
                     .Where(f => f.IsSelected)
                     .OrderByDescending(f => f.Path.Length))
        {
            try
            {
                if (Directory.Exists(folder.Path) && IsDirectoryEmpty(folder.Path))
                {
                    Directory.Delete(folder.Path, recursive: false);
                    deleted++;
                }
            }
            catch { /* skip */ }
        }
        return deleted;
    }

    // ═══════════════════════════════════════════════════════

    private static bool ScanDirectory(string path, List<EmptyFolderInfo> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return false;

        var dirName = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(dirName) && SkipDirectories.Contains(dirName)) return false;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(path); }
        catch { return false; }

        if (entries.Length == 0)
        {
            AddEmpty(results, path);
            return true;
        }

        bool hasFileOrNonEmptyChild = false;
        foreach (var entry in entries)
        {
            if (File.Exists(entry)) { hasFileOrNonEmptyChild = true; continue; }
            if (!Directory.Exists(entry)) continue;

            var childIsEmpty = ScanDirectory(entry, results, depth + 1, maxDepth);
            if (!childIsEmpty) hasFileOrNonEmptyChild = true;
        }

        if (!hasFileOrNonEmptyChild && depth > 0)
        {
            AddEmpty(results, path);
            return true;
        }

        return false;
    }

    private static void AddEmpty(List<EmptyFolderInfo> results, string path)
    {
        DateTime modified = default;
        try { modified = Directory.GetLastWriteTime(path); } catch { /* keep default */ }

        results.Add(new EmptyFolderInfo
        {
            Path = path,
            ParentFolder = Path.GetDirectoryName(path) ?? "",
            LastModified = modified,
        });
    }

    private static bool IsDirectoryEmpty(string path)
    {
        try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }
}
