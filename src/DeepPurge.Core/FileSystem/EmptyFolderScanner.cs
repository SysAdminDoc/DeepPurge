namespace DeepPurge.Core.FileSystem;

public class EmptyFolderInfo
{
    public string Path { get; set; } = "";
    public string ParentFolder { get; set; } = "";
    public DateTime LastModified { get; set; }
    public bool IsSelected { get; set; } = true;
    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");
}

public static class EmptyFolderScanner
{
    private static readonly HashSet<string> _skipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", ".git", ".svn", ".hg",
        "node_modules", "__pycache__", ".vs", "AppData", "Windows",
    };

    public static List<EmptyFolderInfo> ScanForEmptyFolders(string rootPath)
    {
        var results = new List<EmptyFolderInfo>();
        if (!Directory.Exists(rootPath)) return results;

        try
        {
            ScanDirectory(rootPath, results, 0);
        }
        catch { }

        return results.OrderBy(f => f.Path).ToList();
    }

    public static List<EmptyFolderInfo> ScanCommonLocations()
    {
        var results = new List<EmptyFolderInfo>();
        var paths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        foreach (var path in paths.Where(Directory.Exists))
        {
            try { ScanDirectory(path, results, 0, maxDepth: 4); } catch { }
        }

        return results.OrderBy(f => f.Path).ToList();
    }

    public static int DeleteEmptyFolders(IEnumerable<EmptyFolderInfo> folders)
    {
        int deleted = 0;
        foreach (var folder in folders.Where(f => f.IsSelected).OrderByDescending(f => f.Path.Length))
        {
            try
            {
                if (Directory.Exists(folder.Path) && IsDirectoryEmpty(folder.Path))
                {
                    Directory.Delete(folder.Path, false);
                    deleted++;
                }
            }
            catch { }
        }
        return deleted;
    }

    private static bool ScanDirectory(string path, List<EmptyFolderInfo> results, int depth, int maxDepth = 8)
    {
        if (depth > maxDepth) return false;

        var dirName = Path.GetFileName(path);
        if (_skipDirs.Contains(dirName)) return false;

        try
        {
            var entries = Directory.GetFileSystemEntries(path);
            if (entries.Length == 0)
            {
                results.Add(new EmptyFolderInfo
                {
                    Path = path,
                    ParentFolder = Path.GetDirectoryName(path) ?? "",
                    LastModified = Directory.GetLastWriteTime(path),
                });
                return true;
            }

            // Check subdirectories
            bool allSubdirsEmpty = true;
            bool hasFiles = false;
            foreach (var entry in entries)
            {
                if (File.Exists(entry))
                {
                    hasFiles = true;
                    allSubdirsEmpty = false;
                }
                else if (Directory.Exists(entry))
                {
                    if (!ScanDirectory(entry, results, depth + 1, maxDepth))
                        allSubdirsEmpty = false;
                }
            }

            // If all subdirs were empty and no files, this dir is also empty
            if (allSubdirsEmpty && !hasFiles && depth > 0)
            {
                results.Add(new EmptyFolderInfo
                {
                    Path = path,
                    ParentFolder = Path.GetDirectoryName(path) ?? "",
                    LastModified = Directory.GetLastWriteTime(path),
                });
                return true;
            }

            return false;
        }
        catch { return false; }
    }

    private static bool IsDirectoryEmpty(string path)
    {
        try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }
}
