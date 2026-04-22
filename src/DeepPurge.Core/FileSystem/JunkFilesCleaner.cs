using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.FileSystem;

public class JunkCategory : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<JunkFile> Files { get; set; } = new();
    public long TotalSize => Files.Sum(f => f.Size);

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string TotalSizeDisplay
    {
        get
        {
            var b = TotalSize;
            if (b <= 0) return "0 B";
            double kb = b / 1024.0;
            if (kb < 1) return $"{b} B";
            double mb = kb / 1024.0;
            if (mb < 1) return $"{kb:F0} KB";
            if (mb < 1024) return $"{mb:F1} MB";
            return $"{mb / 1024.0:F2} GB";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class JunkFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
}

public static class JunkFilesCleaner
{
    private static readonly TimeSpan RecentThreshold = TimeSpan.FromHours(1);
    private static readonly TimeSpan LogAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan PrefetchAge = TimeSpan.FromDays(30);

    public static List<JunkCategory> ScanForJunk()
    {
        var categories = new List<JunkCategory>
        {
            // System + User Temp
            ScanSystemTemp(), ScanUserTemp(),
            // Browser caches
            ScanChromiumCaches(), ScanFirefoxCaches(),
            // Windows caches
            ScanWindowsUpdate(), ScanDeliveryOptimization(), ScanThumbnailCache(),
            ScanIconCache(), ScanFontCache(), ScanPrefetch(),
            // Logs & dumps
            ScanLogFiles(), ScanCrashDumps(), ScanWerReports(),
            // Installer cache
            ScanInstallerCache(),
            // App-specific caches
            ScanAppCaches(),
            // Runtime caches
            ScanDotNetCaches(), ScanNuGetCache(),
            // Privacy
            ScanRecentItems(), ScanThumbsDb(),
        };

        categories.RemoveAll(c => c.Files.Count == 0);
        return categories;
    }

    /// <summary>Legacy signature kept for callers that don't need progress/dry-run.</summary>
    public static long DeleteJunk(IEnumerable<JunkCategory> categories)
        => DeleteJunkSafe(categories, DeleteOptions.Default, progress: null, ct: default).BytesFreed;

    /// <summary>
    /// Destructive pass with SafetyGuard enforcement, progress reporting,
    /// and dry-run support. Previously this lived in the view — see the v0.8
    /// follow-up. Always validates each item through <see cref="SafetyGuard"/>
    /// even though junk scanning only produces safe-parent paths; the guard
    /// is defense-in-depth against a future scanner that forgets to validate.
    /// </summary>
    public static DeleteSummary DeleteJunkSafe(
        IEnumerable<JunkCategory> categories,
        DeleteOptions options,
        IProgress<DeleteProgress>? progress,
        CancellationToken ct)
    {
        var all = categories
            .Where(c => c.IsSelected)
            .SelectMany(c => c.Files)
            .ToList();

        long freed = 0;
        int cleaned = 0, skipped = 0;

        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = all[i];

            if (!SafetyGuard.IsJunkPathSafeToDelete(file.Path))
            {
                skipped++;
                progress?.Report(new DeleteProgress(
                    i + 1, all.Count, freed, file.Path, Skipped: true));
                continue;
            }

            if (options.DryRun)
            {
                freed += file.Size;
                cleaned++;
                progress?.Report(new DeleteProgress(
                    i + 1, all.Count, freed, file.Path, Skipped: false));
                continue;
            }

            try
            {
                if (file.IsDirectory && Directory.Exists(file.Path))
                {
                    if (options.SecureDelete) SecureDelete.WipeDirectory(file.Path);
                    else Directory.Delete(file.Path, recursive: true);
                    freed += file.Size;
                    cleaned++;
                }
                else if (File.Exists(file.Path))
                {
                    if (options.SecureDelete) SecureDelete.Wipe(file.Path);
                    else File.Delete(file.Path);
                    freed += file.Size;
                    cleaned++;
                }
            }
            catch { skipped++; }

            progress?.Report(new DeleteProgress(
                i + 1, all.Count, freed, file.Path, Skipped: false));
        }

        return new DeleteSummary(cleaned, skipped, freed, options.DryRun);
    }

    // ─── SYSTEM TEMP ───
    private static JunkCategory ScanSystemTemp()
    {
        var cat = new JunkCategory { Name = "System Temp Files", Description = @"C:\Windows\Temp" };
        ScanTempDir(cat, @"C:\Windows\Temp");
        return cat;
    }

    private static JunkCategory ScanUserTemp()
    {
        var cat = new JunkCategory { Name = "User Temp Files", Description = "%TEMP% - installer residue, app scratch data" };
        ScanTempDir(cat, Path.GetTempPath());
        return cat;
    }

    private static void ScanTempDir(JunkCategory cat, string basePath)
    {
        if (!Directory.Exists(basePath)) return;
        var cutoff = DateTime.Now - RecentThreshold;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(basePath, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (di.LastWriteTime < cutoff)
                        cat.Files.Add(new JunkFile { Path = dir, Size = GetDirSize(dir), IsDirectory = true });
                }
                catch { /* skip */ }
            }
            foreach (var file in Directory.EnumerateFiles(basePath, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTime < cutoff)
                        cat.Files.Add(new JunkFile { Path = file, Size = fi.Length });
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
    }

    // ─── CHROMIUM CACHES ───
    private static JunkCategory ScanChromiumCaches()
    {
        var cat = new JunkCategory
        {
            Name = "Browser Caches (Chromium)",
            Description = "Chrome, Edge, Brave, Opera, Vivaldi",
        };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var browsers = new (string Label, string BasePath)[]
        {
            ("Chrome",    Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Edge",      Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave",     Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Opera",     Path.Combine(roaming, "Opera Software", "Opera Stable")),
            ("Opera GX",  Path.Combine(roaming, "Opera Software", "Opera GX Stable")),
            ("Vivaldi",   Path.Combine(local, "Vivaldi", "User Data")),
            ("Chromium",  Path.Combine(local, "Chromium", "User Data")),
        };

        var cacheSubs = new[]
        {
            "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "Service Worker", "GrShaderCache", "ShaderCache", "blob_storage",
        };

        foreach (var (label, basePath) in browsers)
        {
            if (!Directory.Exists(basePath)) continue;
            try
            {
                var profiles = new List<string>();
                if (Directory.Exists(Path.Combine(basePath, "Default"))) profiles.Add(Path.Combine(basePath, "Default"));
                try { profiles.AddRange(Directory.GetDirectories(basePath, "Profile *")); } catch { }
                if (label.StartsWith("Opera", StringComparison.Ordinal) && profiles.Count == 0) profiles.Add(basePath);

                foreach (var profile in profiles)
                    foreach (var sub in cacheSubs)
                        AddDirIfExists(cat, Path.Combine(profile, sub));

                foreach (var sub in new[] { "ShaderCache", "GrShaderCache", "Safe Browsing" })
                    AddDirIfExists(cat, Path.Combine(basePath, sub));
            }
            catch { /* skip */ }
        }
        return cat;
    }

    // ─── FIREFOX CACHES ───
    private static JunkCategory ScanFirefoxCaches()
    {
        var cat = new JunkCategory
        {
            Name = "Browser Caches (Firefox)",
            Description = "Firefox cache2, startupCache, OfflineCache",
        };
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla", "Firefox", "Profiles");
        var localFf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mozilla", "Firefox", "Profiles");

        try
        {
            if (Directory.Exists(roaming))
                foreach (var p in Directory.GetDirectories(roaming))
                    AddDirIfExists(cat, Path.Combine(p, "startupCache"));
        }
        catch { /* skip */ }

        try
        {
            if (Directory.Exists(localFf))
                foreach (var p in Directory.GetDirectories(localFf))
                    foreach (var sub in new[] { "cache2", "OfflineCache", "shader-cache" })
                        AddDirIfExists(cat, Path.Combine(p, sub));
        }
        catch { /* skip */ }

        return cat;
    }

    // ─── WINDOWS CACHES ───
    private static JunkCategory ScanWindowsUpdate()
    {
        var cat = new JunkCategory
        {
            Name = "Windows Update Cache",
            Description = "Downloaded update packages",
            IsSelected = false,
        };
        AddDirIfExists(cat, @"C:\Windows\SoftwareDistribution\Download");
        return cat;
    }

    private static JunkCategory ScanDeliveryOptimization()
    {
        var cat = new JunkCategory
        {
            Name = "Delivery Optimization Cache",
            Description = "P2P update delivery cache",
        };
        AddDirIfExists(cat, @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
        return cat;
    }

    private static JunkCategory ScanThumbnailCache()
    {
        var cat = new JunkCategory { Name = "Thumbnail Cache", Description = "Explorer thumbnail and icon cache" };
        var exp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(exp)) return cat;
        try
        {
            foreach (var pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
                foreach (var file in Directory.GetFiles(exp, pattern))
                    AddFileIfExists(cat, file);
        }
        catch { /* skip */ }
        return cat;
    }

    private static JunkCategory ScanIconCache()
    {
        var cat = new JunkCategory { Name = "Icon Cache", Description = "Windows icon cache" };
        AddFileIfExists(cat, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconCache.db"));
        return cat;
    }

    private static JunkCategory ScanFontCache()
    {
        var cat = new JunkCategory { Name = "Font Cache", Description = "Windows font rendering cache" };
        AddDirIfExists(cat, @"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache");
        return cat;
    }

    private static JunkCategory ScanPrefetch()
    {
        var cat = new JunkCategory
        {
            Name = "Prefetch Files",
            Description = "App launch prefetch data (30+ days old)",
        };
        if (!Directory.Exists(@"C:\Windows\Prefetch")) return cat;
        var cutoff = DateTime.Now - PrefetchAge;
        try
        {
            foreach (var f in Directory.GetFiles(@"C:\Windows\Prefetch", "*.pf"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.LastAccessTime < cutoff)
                        cat.Files.Add(new JunkFile { Path = f, Size = fi.Length });
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
        return cat;
    }

    // ─── LOGS & DUMPS ───
    private static JunkCategory ScanLogFiles()
    {
        var cat = new JunkCategory
        {
            Name = "Log Files",
            Description = "System, setup, and application logs (7+ days old)",
        };
        var cutoff = DateTime.Now - LogAge;
        var paths = new[] { @"C:\Windows\Logs", @"C:\Windows\Panther", @"C:\Windows\inf", @"C:\Windows\Debug" };
        var exts = new[] { "*.log", "*.etl", "*.old" };

        foreach (var p in paths)
        {
            if (!Directory.Exists(p)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(p, ext, SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (fi.LastWriteTime < cutoff)
                                cat.Files.Add(new JunkFile { Path = f, Size = fi.Length });
                        }
                        catch { /* skip */ }
                    }
                }
                catch { /* skip */ }
            }
        }

        // Windows Setup installer logs
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "dd_*.log"))
            {
                try { cat.Files.Add(new JunkFile { Path = f, Size = new FileInfo(f).Length }); }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }

        return cat;
    }

    private static JunkCategory ScanCrashDumps()
    {
        var cat = new JunkCategory
        {
            Name = "Crash Dumps",
            Description = "Application & kernel crash dump files",
        };
        AddDirFilesIfExists(cat,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
            "*.dmp");
        AddDirFilesIfExists(cat, @"C:\Windows\Minidump", "*.dmp");
        AddFileIfExists(cat, @"C:\Windows\MEMORY.DMP");
        AddDirIfExists(cat, @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive");
        return cat;
    }

    private static JunkCategory ScanWerReports()
    {
        var cat = new JunkCategory
        {
            Name = "Windows Error Reports",
            Description = "WER error reporting queue and history",
        };
        AddDirIfExists(cat, @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue");
        AddDirIfExists(cat, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "WER", "ReportQueue"));
        AddDirIfExists(cat, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "WER", "ReportArchive"));
        return cat;
    }

    // ─── INSTALLER CACHE ───
    private static JunkCategory ScanInstallerCache()
    {
        var cat = new JunkCategory
        {
            Name = "Installer & Patch Cache",
            Description = "Windows Installer patch cache, Package Cache",
            IsSelected = false,
        };
        AddDirIfExists(cat, @"C:\Windows\Installer\$PatchCache$");
        AddDirIfExists(cat, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Package Cache"));
        try
        {
            foreach (var dir in Directory.GetDirectories(@"C:\", "$WINDOWS.~*"))
                cat.Files.Add(new JunkFile { Path = dir, Size = GetDirSize(dir), IsDirectory = true });
        }
        catch { /* skip */ }
        AddDirIfExists(cat, @"C:\Windows.old");
        return cat;
    }

    // ─── APP CACHES ───
    private static JunkCategory ScanAppCaches()
    {
        var cat = new JunkCategory
        {
            Name = "Application Caches",
            Description = "Discord, Steam, Spotify, Teams, VS Code, npm, pip caches",
        };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cacheSubs = new[] { "Cache", "Code Cache", "GPUCache" };

        foreach (var sub in cacheSubs)
        {
            AddDirIfExists(cat, Path.Combine(roaming, "discord", sub));
            AddDirIfExists(cat, Path.Combine(roaming, "Slack", sub));
        }

        AddDirIfExists(cat, Path.Combine(local, "Spotify", "Data"));
        AddDirIfExists(cat, Path.Combine(local, "Spotify", "Storage"));
        AddDirIfExists(cat, Path.Combine(local, "Steam", "htmlcache"));

        foreach (var sub in new[] { "CachedData", "CachedExtensions", "CachedExtensionVSIXs", "GPUCache", "Code Cache" })
            AddDirIfExists(cat, Path.Combine(local, "Programs", "Microsoft VS Code", sub));
        AddDirIfExists(cat, Path.Combine(roaming, "Code", "Cache"));
        AddDirIfExists(cat, Path.Combine(roaming, "Code", "CachedData"));

        foreach (var sub in cacheSubs)
            AddDirIfExists(cat, Path.Combine(local, "Microsoft", "Teams", sub));

        AddDirIfExists(cat, Path.Combine(roaming, "npm-cache"));
        AddDirIfExists(cat, Path.Combine(local, "pip", "cache"));
        AddDirIfExists(cat, Path.Combine(local, "Adobe", "Acrobat", "DC", "Cache"));
        AddDirIfExists(cat, Path.Combine(roaming, "Zoom", "data"));

        return cat;
    }

    // ─── RUNTIME ───
    private static JunkCategory ScanDotNetCaches()
    {
        var cat = new JunkCategory
        {
            Name = ".NET / NGen Cache",
            Description = "CLR temporary ASP.NET files, HTTP cache",
        };
        AddDirIfExists(cat, @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\Temporary ASP.NET Files");
        AddDirIfExists(cat, @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Temporary ASP.NET Files");
        AddDirIfExists(cat, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "dotnet", "cache"));
        return cat;
    }

    private static JunkCategory ScanNuGetCache()
    {
        var cat = new JunkCategory { Name = "NuGet / Package Cache", Description = "NuGet HTTP cache" };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddDirIfExists(cat, Path.Combine(local, "NuGet", "v3-cache"));
        AddDirIfExists(cat, Path.Combine(local, "NuGet", "plugins-cache"));
        return cat;
    }

    // ─── PRIVACY ───
    private static JunkCategory ScanRecentItems()
    {
        var cat = new JunkCategory
        {
            Name = "Recent Files List",
            Description = "Windows recent file history + jump lists",
            IsSelected = false,
        };
        var recentPath = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentPath)) return cat;

        try { foreach (var f in Directory.GetFiles(recentPath, "*.lnk")) AddFileIfExists(cat, f); }
        catch { /* skip */ }

        foreach (var sub in new[] { "AutomaticDestinations", "CustomDestinations" })
        {
            var p = Path.Combine(recentPath, sub);
            if (!Directory.Exists(p)) continue;
            try { foreach (var f in Directory.GetFiles(p)) AddFileIfExists(cat, f); }
            catch { /* skip */ }
        }
        return cat;
    }

    private static JunkCategory ScanThumbsDb()
    {
        var cat = new JunkCategory
        {
            Name = "Thumbs.db Files",
            Description = "Legacy thumbnail files scattered across user folders",
        };
        try
        {
            foreach (var f in Directory.EnumerateFiles(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Thumbs.db", SearchOption.AllDirectories))
                AddFileIfExists(cat, f);
        }
        catch { /* skip */ }
        return cat;
    }

    // ─── HELPERS ───
    private static void AddDirIfExists(JunkCategory cat, string path)
    {
        if (!Directory.Exists(path)) return;
        try { cat.Files.Add(new JunkFile { Path = path, Size = GetDirSize(path), IsDirectory = true }); }
        catch { /* skip */ }
    }

    private static void AddFileIfExists(JunkCategory cat, string path)
    {
        if (!File.Exists(path)) return;
        try { cat.Files.Add(new JunkFile { Path = path, Size = new FileInfo(path).Length }); }
        catch { /* skip */ }
    }

    private static void AddDirFilesIfExists(JunkCategory cat, string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return;
        try { foreach (var f in Directory.GetFiles(dir, pattern)) AddFileIfExists(cat, f); }
        catch { /* skip */ }
    }

    private static long GetDirSize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(fi => { try { return fi.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }
}
