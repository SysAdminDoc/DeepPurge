namespace DeepPurge.Core.FileSystem;

public class DiskFolderInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public double Percentage { get; set; }
    public string SizeDisplay => FormatSize(SizeBytes);
    public string PercentageDisplay => $"{Percentage:F1}%";

    private static string FormatSize(long b)
    {
        if (b < 1024) return $"{b} B";
        double k = b / 1024.0;
        if (k < 1024) return $"{k:F0} KB";
        double m = k / 1024.0;
        return m < 1024 ? $"{m:F1} MB" : $"{m / 1024.0:F2} GB";
    }
}

public class LargeFileInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsSelected { get; set; }
    public string SizeDisplay => FormatSize(SizeBytes);
    public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd");

    private static string FormatSize(long b)
    {
        if (b < 1024) return $"{b} B";
        double k = b / 1024.0;
        if (k < 1024) return $"{k:F0} KB";
        double m = k / 1024.0;
        return m < 1024 ? $"{m:F1} MB" : $"{m / 1024.0:F2} GB";
    }
}

public static class DiskSpaceAnalyzer
{
    public static List<DiskFolderInfo> AnalyzeFolder(string rootPath, int maxDepth = 1)
    {
        var results = new List<DiskFolderInfo>();
        if (!Directory.Exists(rootPath)) return results;

        try
        {
            long totalSize = 0;
            foreach (var dir in Directory.GetDirectories(rootPath))
            {
                try
                {
                    var info = GetFolderInfo(dir, maxDepth - 1);
                    results.Add(info);
                    totalSize += info.SizeBytes;
                }
                catch { }
            }

            // Also count files in root
            long rootFiles = 0;
            int rootFileCount = 0;
            foreach (var f in Directory.GetFiles(rootPath))
            {
                try { rootFiles += new FileInfo(f).Length; rootFileCount++; } catch { }
            }
            if (rootFileCount > 0)
            {
                results.Add(new DiskFolderInfo
                {
                    Path = rootPath, Name = "(files in root)",
                    SizeBytes = rootFiles, FileCount = rootFileCount,
                });
                totalSize += rootFiles;
            }

            // Calculate percentages
            if (totalSize > 0)
                foreach (var r in results)
                    r.Percentage = r.SizeBytes * 100.0 / totalSize;
        }
        catch { }

        return results.OrderByDescending(r => r.SizeBytes).ToList();
    }

    public static List<LargeFileInfo> FindLargeFiles(string rootPath, long minSizeBytes = 50 * 1024 * 1024, int maxResults = 200)
    {
        var results = new List<LargeFileInfo>();
        if (!Directory.Exists(rootPath)) return results;

        try
        {
            ScanForLargeFiles(rootPath, minSizeBytes, results, maxResults, 0);
        }
        catch { }

        return results.OrderByDescending(f => f.SizeBytes).Take(maxResults).ToList();
    }

    public static List<DiskFolderInfo> GetDriveOverview()
    {
        var results = new List<DiskFolderInfo>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            results.Add(new DiskFolderInfo
            {
                Path = drive.RootDirectory.FullName,
                Name = $"{drive.Name} ({drive.VolumeLabel})",
                SizeBytes = drive.TotalSize - drive.TotalFreeSpace,
                FileCount = 0,
                Percentage = (drive.TotalSize - drive.TotalFreeSpace) * 100.0 / drive.TotalSize,
            });
        }
        return results;
    }

    private static DiskFolderInfo GetFolderInfo(string path, int depth)
    {
        var info = new DiskFolderInfo
        {
            Path = path,
            Name = Path.GetFileName(path),
        };

        try
        {
            var dirInfo = new DirectoryInfo(path);
            foreach (var f in dirInfo.EnumerateFiles("*", new EnumerationOptions
                { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 10 }))
            {
                try
                {
                    info.SizeBytes += f.Length;
                    info.FileCount++;
                }
                catch { }
            }

            info.FolderCount = dirInfo.EnumerateDirectories("*", new EnumerationOptions
                { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 10 }).Count();
        }
        catch { }

        return info;
    }

    private static void ScanForLargeFiles(string path, long minSize, List<LargeFileInfo> results, int max, int depth)
    {
        if (depth > 10 || results.Count >= max) return;

        try
        {
            foreach (var f in Directory.GetFiles(path))
            {
                if (results.Count >= max) return;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length >= minSize)
                    {
                        results.Add(new LargeFileInfo
                        {
                            Path = f,
                            Name = fi.Name,
                            Extension = fi.Extension.ToLowerInvariant(),
                            SizeBytes = fi.Length,
                            LastModified = fi.LastWriteTime,
                        });
                    }
                }
                catch { }
            }

            foreach (var d in Directory.GetDirectories(path))
            {
                if (results.Count >= max) return;
                var dirName = Path.GetFileName(d);
                if (dirName.StartsWith('.') || dirName == "$Recycle.Bin" || dirName == "System Volume Information")
                    continue;
                ScanForLargeFiles(d, minSize, results, max, depth + 1);
            }
        }
        catch { }
    }
}
