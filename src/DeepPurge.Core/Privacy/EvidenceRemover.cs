using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeepPurge.Core.Privacy;

public class TraceCategory
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<TraceItem> Items { get; set; } = new();
    public bool IsSelected { get; set; } = true;
    public long TotalSize => Items.Sum(i => i.SizeBytes);
    public string TotalSizeDisplay => FormatSize(TotalSize);
    public int ItemCount => Items.Count;

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F0} KB";
        double mb = kb / 1024.0;
        return mb < 1024 ? $"{mb:F1} MB" : $"{mb / 1024.0:F2} GB";
    }
}

public class TraceItem
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsRegistryKey { get; set; }
    public bool IsCommand { get; set; }
    public string Command { get; set; } = "";
}

public static class EvidenceRemover
{
    public static List<TraceCategory> ScanAllTraces()
    {
        var cats = new List<TraceCategory>();
        cats.Add(ScanRecentDocuments());
        cats.Add(ScanThumbnailCache());
        cats.Add(ScanJumpLists());
        cats.Add(ScanWindowsExplorerHistory());
        cats.Add(ScanClipboard());
        cats.Add(ScanDnsCache());
        cats.Add(ScanWindowsLogFiles());
        cats.Add(ScanEventLogs());
        cats.Add(ScanCrashReports());
        cats.Add(ScanDeliveryOptimization());
        cats.Add(ScanWindowsErrorReporting());
        cats.Add(ScanFontCache());
        cats.RemoveAll(c => c.Items.Count == 0);
        return cats;
    }

    public static long CleanTraces(IEnumerable<TraceCategory> categories)
    {
        long freed = 0;
        foreach (var cat in categories.Where(c => c.IsSelected))
        {
            foreach (var item in cat.Items)
            {
                try
                {
                    if (item.IsCommand && !string.IsNullOrEmpty(item.Command))
                    {
                        RunCommand(item.Command);
                        freed += item.SizeBytes;
                    }
                    else if (item.IsDirectory && Directory.Exists(item.Path))
                    {
                        var size = item.SizeBytes;
                        Directory.Delete(item.Path, true);
                        freed += size;
                    }
                    else if (File.Exists(item.Path))
                    {
                        var size = item.SizeBytes;
                        File.Delete(item.Path);
                        freed += size;
                    }
                }
                catch { }
            }
        }
        return freed;
    }

    private static TraceCategory ScanRecentDocuments()
    {
        var cat = new TraceCategory { Name = "Recent Documents", Description = "Windows recent file access history and shortcuts" };
        var recentPath = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentPath)) return cat;
        try
        {
            foreach (var f in Directory.GetFiles(recentPath, "*.lnk"))
                try { cat.Items.Add(new TraceItem { Path = f, SizeBytes = new FileInfo(f).Length }); } catch { }
            var autoDir = Path.Combine(recentPath, "AutomaticDestinations");
            if (Directory.Exists(autoDir))
                foreach (var f in Directory.GetFiles(autoDir))
                    try { cat.Items.Add(new TraceItem { Path = f, SizeBytes = new FileInfo(f).Length }); } catch { }
            var customDir = Path.Combine(recentPath, "CustomDestinations");
            if (Directory.Exists(customDir))
                foreach (var f in Directory.GetFiles(customDir))
                    try { cat.Items.Add(new TraceItem { Path = f, SizeBytes = new FileInfo(f).Length }); } catch { }
        }
        catch { }
        return cat;
    }

    private static TraceCategory ScanThumbnailCache()
    {
        var cat = new TraceCategory { Name = "Thumbnail Cache", Description = "Windows Explorer thumbnail database files" };
        var explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(explorerPath)) return cat;
        try
        {
            foreach (var f in Directory.GetFiles(explorerPath, "thumbcache_*.db"))
                try { cat.Items.Add(new TraceItem { Path = f, SizeBytes = new FileInfo(f).Length }); } catch { }
            foreach (var f in Directory.GetFiles(explorerPath, "iconcache_*.db"))
                try { cat.Items.Add(new TraceItem { Path = f, SizeBytes = new FileInfo(f).Length }); } catch { }
        }
        catch { }
        return cat;
    }

    private static TraceCategory ScanJumpLists()
    {
        var cat = new TraceCategory { Name = "Jump Lists", Description = "Taskbar and Start Menu jump list history" };
        var recentPath = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        var autoPath = Path.Combine(recentPath, "AutomaticDestinations");
        if (Directory.Exists(autoPath))
            cat.Items.Add(new TraceItem { Path = autoPath, SizeBytes = GetDirSize(autoPath), IsDirectory = true });
        return cat;
    }

    private static TraceCategory ScanWindowsExplorerHistory()
    {
        var cat = new TraceCategory { Name = "Explorer History", Description = "File Explorer address bar and search history" };
        // TypedPaths, RunMRU, WordWheelQuery are in registry - use command to clear
        cat.Items.Add(new TraceItem
        {
            IsCommand = true,
            Command = "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\TypedPaths\" /f",
            SizeBytes = 1024,
            Path = "HKCU\\...\\Explorer\\TypedPaths"
        });
        cat.Items.Add(new TraceItem
        {
            IsCommand = true,
            Command = "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\RunMRU\" /f",
            SizeBytes = 1024,
            Path = "HKCU\\...\\Explorer\\RunMRU"
        });
        cat.Items.Add(new TraceItem
        {
            IsCommand = true,
            Command = "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\WordWheelQuery\" /f",
            SizeBytes = 1024,
            Path = "HKCU\\...\\Explorer\\WordWheelQuery"
        });
        return cat;
    }

    private static TraceCategory ScanClipboard()
    {
        var cat = new TraceCategory { Name = "Clipboard Data", Description = "Current and cached clipboard contents" };
        var clipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Clipboard");
        if (Directory.Exists(clipPath))
            cat.Items.Add(new TraceItem { Path = clipPath, SizeBytes = GetDirSize(clipPath), IsDirectory = true });
        return cat;
    }

    private static TraceCategory ScanDnsCache()
    {
        var cat = new TraceCategory { Name = "DNS Cache", Description = "Cached DNS lookups revealing browsing history" };
        cat.Items.Add(new TraceItem { IsCommand = true, Command = "ipconfig /flushdns", SizeBytes = 0, Path = "DNS Resolver Cache" });
        return cat;
    }

    private static TraceCategory ScanWindowsLogFiles()
    {
        var cat = new TraceCategory { Name = "Windows Logs", Description = "System and application log files" };
        var logDirs = new[] { @"C:\Windows\Logs", @"C:\Windows\Panther", @"C:\Windows\debug" };
        foreach (var dir in logDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.log", SearchOption.AllDirectories))
                    try { var fi = new FileInfo(f); if (fi.LastWriteTime < DateTime.Now.AddDays(-7))
                        cat.Items.Add(new TraceItem { Path = f, SizeBytes = fi.Length }); } catch { }
                foreach (var f in Directory.EnumerateFiles(dir, "*.etl", SearchOption.AllDirectories))
                    try { var fi = new FileInfo(f); if (fi.LastWriteTime < DateTime.Now.AddDays(-7))
                        cat.Items.Add(new TraceItem { Path = f, SizeBytes = fi.Length }); } catch { }
            }
            catch { }
        }
        return cat;
    }

    private static TraceCategory ScanEventLogs()
    {
        var cat = new TraceCategory { Name = "Event Logs", Description = "Windows Event Log archives", IsSelected = false };
        var logDir = @"C:\Windows\System32\winevt\Logs";
        if (!Directory.Exists(logDir)) return cat;
        try
        {
            foreach (var f in Directory.GetFiles(logDir, "Archive-*.evtx"))
                try { cat.Items.Add(new TraceItem { Path = f, SizeBytes = new FileInfo(f).Length }); } catch { }
        }
        catch { }
        return cat;
    }

    private static TraceCategory ScanCrashReports()
    {
        var cat = new TraceCategory { Name = "Crash Reports", Description = "Windows Error Reporting crash dumps and logs" };
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
            @"C:\Windows\Minidump",
        };
        foreach (var p in paths)
        {
            if (!Directory.Exists(p)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(p, "*.dmp"))
                    try { cat.Items.Add(new TraceItem { Path = f, SizeBytes = new FileInfo(f).Length }); } catch { }
            }
            catch { }
        }
        if (File.Exists(@"C:\Windows\MEMORY.DMP"))
            try { cat.Items.Add(new TraceItem { Path = @"C:\Windows\MEMORY.DMP", SizeBytes = new FileInfo(@"C:\Windows\MEMORY.DMP").Length }); } catch { }
        return cat;
    }

    private static TraceCategory ScanDeliveryOptimization()
    {
        var cat = new TraceCategory { Name = "Delivery Optimization", Description = "Windows Update peer-to-peer download cache", IsSelected = false };
        var doPath = @"C:\Windows\SoftwareDistribution\DeliveryOptimization";
        if (Directory.Exists(doPath))
            cat.Items.Add(new TraceItem { Path = doPath, SizeBytes = GetDirSize(doPath), IsDirectory = true });
        return cat;
    }

    private static TraceCategory ScanWindowsErrorReporting()
    {
        var cat = new TraceCategory { Name = "Error Reports", Description = "Windows Error Reporting queued and archived reports" };
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER"),
            @"C:\ProgramData\Microsoft\Windows\WER",
        };
        foreach (var p in paths)
        {
            if (!Directory.Exists(p)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(p, "*", SearchOption.TopDirectoryOnly))
                    cat.Items.Add(new TraceItem { Path = dir, SizeBytes = GetDirSize(dir), IsDirectory = true });
            }
            catch { }
        }
        return cat;
    }

    private static TraceCategory ScanFontCache()
    {
        var cat = new TraceCategory { Name = "Font Cache", Description = "Windows font rendering cache files" };
        var fontCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "FontCache");
        if (Directory.Exists(fontCachePath))
            cat.Items.Add(new TraceItem { Path = fontCachePath, SizeBytes = GetDirSize(fontCachePath), IsDirectory = true });
        var sysFont = @"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache";
        if (Directory.Exists(sysFont))
            cat.Items.Add(new TraceItem { Path = sysFont, SizeBytes = GetDirSize(sysFont), IsDirectory = true });
        return cat;
    }

    private static void RunCommand(string cmd)
    {
        try
        {
            var parts = cmd.Split(' ', 2);
            var psi = new ProcessStartInfo
            {
                FileName = parts[0],
                Arguments = parts.Length > 1 ? parts[1] : "",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch { }
    }

    private static long GetDirSize(string path)
    {
        try { return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(fi => { try { return fi.Length; } catch { return 0L; } }); }
        catch { return 0; }
    }
}
