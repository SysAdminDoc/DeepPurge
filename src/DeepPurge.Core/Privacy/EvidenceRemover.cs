using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Privacy;

public class TraceCategory : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<TraceItem> Items { get; set; } = new();
    public long TotalSize => Items.Sum(i => i.SizeBytes);
    public string TotalSizeDisplay => FormatSize(TotalSize);
    public int ItemCount => Items.Count;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
    public string CommandArgs { get; set; } = "";
}

public static class EvidenceRemover
{
    public static List<TraceCategory> ScanAllTraces()
    {
        var cats = new List<TraceCategory>
        {
            ScanRecentDocuments(),
            ScanJumpLists(),
            ScanThumbnailCache(),
            ScanWindowsExplorerHistory(),
            ScanClipboard(),
            ScanDnsCache(),
            ScanWindowsLogFiles(),
            ScanEventLogs(),
            ScanCrashReports(),
            ScanDeliveryOptimization(),
            ScanWindowsErrorReporting(),
            ScanFontCache(),
        };
        cats.RemoveAll(c => c.Items.Count == 0);
        return cats;
    }

    /// <summary>Legacy signature kept for callers that don't need progress/dry-run.</summary>
    public static long CleanTraces(IEnumerable<TraceCategory> categories)
        => CleanTracesSafe(categories, DeleteOptions.Default, progress: null, ct: default).BytesFreed;

    /// <summary>
    /// Destructive pass with progress + dry-run. Command items (like
    /// <c>ipconfig /flushdns</c>) are treated as zero-byte and no-op under
    /// dry-run — we don't want to actually execute a privacy command when
    /// the user asked for a preview.
    /// </summary>
    public static DeleteSummary CleanTracesSafe(
        IEnumerable<TraceCategory> categories,
        DeleteOptions options,
        IProgress<DeleteProgress>? progress,
        CancellationToken ct)
    {
        var all = categories
            .Where(c => c.IsSelected)
            .SelectMany(c => c.Items.Select(i => (cat: c, item: i)))
            .ToList();

        long freed = 0;
        int cleaned = 0, skipped = 0;

        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (_, item) = all[i];
            var label = string.IsNullOrEmpty(item.Path) ? item.Command : item.Path;

            if (options.DryRun)
            {
                freed += item.SizeBytes;
                cleaned++;
                progress?.Report(new DeleteProgress(
                    i + 1, all.Count, freed, label, Skipped: false));
                continue;
            }

            try
            {
                if (item.IsCommand && !string.IsNullOrEmpty(item.Command))
                {
                    RunCommand(item.Command, item.CommandArgs);
                    freed += item.SizeBytes;
                    cleaned++;
                }
                else if (item.IsDirectory && Directory.Exists(item.Path))
                {
                    if (options.SecureDelete) SecureDelete.WipeDirectory(item.Path);
                    else Directory.Delete(item.Path, recursive: true);
                    freed += item.SizeBytes;
                    cleaned++;
                }
                else if (File.Exists(item.Path))
                {
                    if (options.SecureDelete) SecureDelete.Wipe(item.Path);
                    else File.Delete(item.Path);
                    freed += item.SizeBytes;
                    cleaned++;
                }
                else
                {
                    skipped++;
                }
            }
            catch { skipped++; }

            progress?.Report(new DeleteProgress(
                i + 1, all.Count, freed, label, Skipped: false));
        }

        return new DeleteSummary(cleaned, skipped, freed, options.DryRun);
    }

    // ═══════════════════════════════════════════════════════
    //  Scanners
    // ═══════════════════════════════════════════════════════

    private static TraceCategory ScanRecentDocuments()
    {
        var cat = new TraceCategory
        {
            Name = "Recent Documents",
            Description = "Windows recent file access history (*.lnk in Recent folder)",
        };
        var recentPath = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentPath)) return cat;

        try
        {
            foreach (var f in Directory.GetFiles(recentPath, "*.lnk"))
                AddFile(cat, f);

            var customDir = Path.Combine(recentPath, "CustomDestinations");
            if (Directory.Exists(customDir))
                foreach (var f in Directory.GetFiles(customDir))
                    AddFile(cat, f);
        }
        catch { /* skip */ }
        return cat;
    }

    /// <summary>
    /// Jump Lists are the AutomaticDestinations subfolder. Kept as a *single*
    /// directory-scope trace so it isn't double-counted against Recent Documents.
    /// </summary>
    private static TraceCategory ScanJumpLists()
    {
        var cat = new TraceCategory
        {
            Name = "Jump Lists",
            Description = "Taskbar and Start Menu jump list history",
        };
        var recentPath = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrEmpty(recentPath)) return cat;

        var autoPath = Path.Combine(recentPath, "AutomaticDestinations");
        if (Directory.Exists(autoPath))
            cat.Items.Add(new TraceItem
            {
                Path = autoPath,
                SizeBytes = GetDirSize(autoPath),
                IsDirectory = true,
            });
        return cat;
    }

    private static TraceCategory ScanThumbnailCache()
    {
        var cat = new TraceCategory
        {
            Name = "Thumbnail Cache",
            Description = "Windows Explorer thumbnail / icon cache databases",
        };
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(explorerPath)) return cat;

        foreach (var pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
        {
            try
            {
                foreach (var f in Directory.GetFiles(explorerPath, pattern)) AddFile(cat, f);
            }
            catch { /* skip */ }
        }
        return cat;
    }

    private static TraceCategory ScanWindowsExplorerHistory()
    {
        var cat = new TraceCategory
        {
            Name = "Explorer History",
            Description = "Address bar, Run dialog, and File Explorer search history",
        };

        var paths = new[]
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
        };

        foreach (var p in paths)
        {
            cat.Items.Add(new TraceItem
            {
                IsCommand = true,
                Command = "reg.exe",
                CommandArgs = $"delete \"{p}\" /f",
                Path = p,
                SizeBytes = 1024,
            });
        }
        return cat;
    }

    private static TraceCategory ScanClipboard()
    {
        var cat = new TraceCategory
        {
            Name = "Clipboard Data",
            Description = "Current and cached clipboard contents",
        };
        var clipPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Clipboard");
        if (Directory.Exists(clipPath))
            cat.Items.Add(new TraceItem
            {
                Path = clipPath,
                SizeBytes = GetDirSize(clipPath),
                IsDirectory = true,
            });
        return cat;
    }

    private static TraceCategory ScanDnsCache()
    {
        var cat = new TraceCategory
        {
            Name = "DNS Cache",
            Description = "Cached DNS lookups revealing browsing history",
        };
        cat.Items.Add(new TraceItem
        {
            IsCommand = true,
            Command = "ipconfig.exe",
            CommandArgs = "/flushdns",
            Path = "DNS Resolver Cache",
        });
        return cat;
    }

    private static TraceCategory ScanWindowsLogFiles()
    {
        var cat = new TraceCategory
        {
            Name = "Windows Logs",
            Description = "System and application log files older than 7 days",
        };
        var cutoff = DateTime.Now.AddDays(-7);
        var logDirs = new[] { @"C:\Windows\Logs", @"C:\Windows\Panther", @"C:\Windows\debug" };
        var patterns = new[] { "*.log", "*.etl" };

        foreach (var dir in logDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pattern in patterns)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (fi.LastWriteTime < cutoff)
                                cat.Items.Add(new TraceItem { Path = f, SizeBytes = fi.Length });
                        }
                        catch { /* skip */ }
                    }
                }
                catch { /* skip */ }
            }
        }
        return cat;
    }

    private static TraceCategory ScanEventLogs()
    {
        var cat = new TraceCategory
        {
            Name = "Event Logs",
            Description = "Archived Windows Event Log files",
            IsSelected = false,
        };
        var logDir = @"C:\Windows\System32\winevt\Logs";
        if (!Directory.Exists(logDir)) return cat;

        try
        {
            foreach (var f in Directory.GetFiles(logDir, "Archive-*.evtx")) AddFile(cat, f);
        }
        catch { /* skip */ }
        return cat;
    }

    private static TraceCategory ScanCrashReports()
    {
        var cat = new TraceCategory
        {
            Name = "Crash Reports",
            Description = "Windows Error Reporting crash dumps and logs",
        };
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
                foreach (var f in Directory.GetFiles(p, "*.dmp")) AddFile(cat, f);
            }
            catch { /* skip */ }
        }
        AddFile(cat, @"C:\Windows\MEMORY.DMP");
        return cat;
    }

    private static TraceCategory ScanDeliveryOptimization()
    {
        var cat = new TraceCategory
        {
            Name = "Delivery Optimization",
            Description = "Windows Update peer-to-peer download cache",
            IsSelected = false,
        };
        var doPath = @"C:\Windows\SoftwareDistribution\DeliveryOptimization";
        if (Directory.Exists(doPath))
            cat.Items.Add(new TraceItem
            {
                Path = doPath,
                SizeBytes = GetDirSize(doPath),
                IsDirectory = true,
            });
        return cat;
    }

    private static TraceCategory ScanWindowsErrorReporting()
    {
        var cat = new TraceCategory
        {
            Name = "Error Reports",
            Description = "Queued and archived Windows Error Reporting reports",
        };
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "WER"),
            @"C:\ProgramData\Microsoft\Windows\WER",
        };
        foreach (var p in paths)
        {
            if (!Directory.Exists(p)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(p, "*", SearchOption.TopDirectoryOnly))
                    cat.Items.Add(new TraceItem
                    {
                        Path = dir,
                        SizeBytes = GetDirSize(dir),
                        IsDirectory = true,
                    });
            }
            catch { /* skip */ }
        }
        return cat;
    }

    private static TraceCategory ScanFontCache()
    {
        var cat = new TraceCategory
        {
            Name = "Font Cache",
            Description = "Windows font rendering cache files",
        };
        var userFontCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "FontCache");
        if (Directory.Exists(userFontCache))
            cat.Items.Add(new TraceItem
            {
                Path = userFontCache,
                SizeBytes = GetDirSize(userFontCache),
                IsDirectory = true,
            });

        var sysFont = @"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache";
        if (Directory.Exists(sysFont))
            cat.Items.Add(new TraceItem
            {
                Path = sysFont,
                SizeBytes = GetDirSize(sysFont),
                IsDirectory = true,
            });
        return cat;
    }

    // ═══════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════

    private static void AddFile(TraceCategory cat, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            cat.Items.Add(new TraceItem { Path = path, SizeBytes = new FileInfo(path).Length });
        }
        catch { /* skip */ }
    }

    private static void RunCommand(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
        }
        catch { /* best-effort */ }
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
