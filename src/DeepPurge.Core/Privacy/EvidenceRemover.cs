using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Execution;
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

    private static string FormatSize(long bytes) => Diagnostics.SizeFormatter.Format(bytes);
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
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
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
            ScanUsbDeviceHistory(),
            ScanBrowserCookies(),
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

        var executor = new DeletionExecutor();
        var results = new List<DeletionResult>(all.Count);

        for (int i = 0; i < all.Count; i++)
        {
            var (_, item) = all[i];
            var label = string.IsNullOrEmpty(item.Path) ? item.Command : item.Path;
            var request = new DeletionRequest(
                item.Path,
                item.IsDirectory,
                item.SizeBytes,
                item.IsCommand ? "trace-command" : "trace-clean");

            void Skip(string reason)
            {
                results.Add(DeletionExecutor.Skipped(
                    request,
                    FormatSkippedReason(reason, label)));
                progress?.Report(new DeleteProgress(
                    i + 1,
                    all.Count,
                    CurrentBytes(results, options.DryRun),
                    label,
                    Skipped: true));
            }

            if (ct.IsCancellationRequested)
            {
                results.Add(new DeletionResult(
                    label,
                    DeletionOutcomeKind.Cancelled,
                    item.SizeBytes,
                    request.Operation,
                    "Cancellation requested."));
                break;
            }

            if (!item.IsCommand && !string.IsNullOrEmpty(item.Path) &&
                IsCookiePath(item.Path) && AppSettings.Current.CookieWhitelist.Count > 0)
            {
                var cookieResult = CleanCookieDatabase(item.Path, AppSettings.Current.CookieWhitelist, options.DryRun);
                if (cookieResult.Skipped)
                {
                    Skip(cookieResult.SkipReason ?? "Cookie whitelist");
                }
                else
                {
                    if (cookieResult.DeletedCookies > 0)
                    {
                        var bytes = item.SizeBytes / Math.Max(cookieResult.TotalCookies, 1) *
                                    cookieResult.DeletedCookies;
                        var cookieOutcome = options.DryRun
                            ? DeletionOutcomeKind.Preview
                            : DeletionOutcomeKind.PermanentlyDeleted;
                        results.Add(new DeletionResult(
                            item.Path,
                            cookieOutcome,
                            bytes,
                            "cookie-clean",
                            options.DryRun ? "Would remove non-whitelisted cookies." : null));
                        if (!options.DryRun)
                        {
                            DeletionManifest.Record(
                                item.Path,
                                "database",
                                bytes,
                                "cookie-clean",
                                outcome: "PermanentlyDeleted");
                        }
                    }
                    else
                    {
                        Skip("No cookies matched");
                    }

                    progress?.Report(new DeleteProgress(
                        i + 1,
                        all.Count,
                        CurrentBytes(results, options.DryRun),
                        $"{item.Path} ({cookieResult.PreservedCookies} kept, {cookieResult.DeletedCookies} removed)",
                        Skipped: cookieResult.DeletedCookies == 0));
                }
                continue;
            }

            if (options.MinAgeDays > 0 && !item.IsCommand && !string.IsNullOrEmpty(item.Path))
            {
                try
                {
                    var cutoff = DateTime.UtcNow.AddDays(-options.MinAgeDays);
                    if (File.GetLastWriteTimeUtc(item.Path) > cutoff)
                    {
                        Skip("Too recent");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Skip($"MinAge check failed: {ex.Message}");
                    continue;
                }
            }

            if (!item.IsCommand && item.IsDirectory && Directory.Exists(item.Path) &&
                Safety.SafetyGuard.IsReparsePoint(item.Path))
            {
                Skip("Reparse point");
                continue;
            }

            if (item.IsCommand && !string.IsNullOrEmpty(item.Command))
            {
                if (options.DryRun)
                {
                    results.Add(new DeletionResult(
                        label,
                        DeletionOutcomeKind.Preview,
                        item.SizeBytes,
                        request.Operation,
                        "Would run command."));
                }
                else
                {
                    var commandFailure = RunCommand(item.Command, item.CommandArgs);
                    if (commandFailure != null)
                    {
                        Skip(commandFailure);
                        continue;
                    }
                    results.Add(DeletionExecutor.ConfirmedExternal(
                        label,
                        item.SizeBytes,
                        request.Operation));
                }
            }
            else
            {
                var result = executor.Execute(request, options, ct);
                results.Add(result);
            }

            progress?.Report(new DeleteProgress(
                i + 1,
                all.Count,
                CurrentBytes(results, options.DryRun),
                label,
                Skipped: !results[^1].IsConfirmed && !results[^1].IsPreview));
        }

        return DeleteSummary.FromResults(results, options.DryRun);
    }

    private static long CurrentBytes(
        IReadOnlyList<DeletionResult> results,
        bool dryRun)
        => dryRun
            ? results.Where(r => r.IsPreview).Sum(r => r.SizeBytes)
            : results.Where(r => r.IsConfirmed).Sum(r => r.SizeBytes);

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
        catch (Exception ex) { Log.Warn($"Recent documents scan failed: {ex.Message}"); }
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
            catch (Exception ex) { Log.Warn($"Thumbnail cache scan failed for pattern '{pattern}': {ex.Message}"); }
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
        var logDirs = new[] { Path.Combine(WinDir, "Logs"), Path.Combine(WinDir, "Panther"), Path.Combine(WinDir, "debug") };
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
                        catch (Exception ex) { Log.Warn($"Log file info read failed for '{f}': {ex.Message}"); }
                    }
                }
                catch (Exception ex) { Log.Warn($"Log file enumeration failed in '{dir}': {ex.Message}"); }
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
        var logDir = Path.Combine(Environment.SystemDirectory, "winevt", "Logs");
        if (!Directory.Exists(logDir)) return cat;

        try
        {
            foreach (var f in Directory.GetFiles(logDir, "Archive-*.evtx")) AddFile(cat, f);
        }
        catch (Exception ex) { Log.Warn($"Event log scan failed: {ex.Message}"); }
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
            Path.Combine(WinDir, "Minidump"),
        };
        foreach (var p in paths)
        {
            if (!Directory.Exists(p)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(p, "*.dmp")) AddFile(cat, f);
            }
            catch (Exception ex) { Log.Warn($"Crash report scan failed in '{p}': {ex.Message}"); }
        }
        AddFile(cat, Path.Combine(WinDir, "MEMORY.DMP"));
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
        var doPath = Path.Combine(WinDir, "SoftwareDistribution", "DeliveryOptimization");
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
            Path.Combine(ProgramData, "Microsoft", "Windows", "WER"),
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
            catch (Exception ex) { Log.Warn($"WER scan failed in '{p}': {ex.Message}"); }
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

        var sysFont = Path.Combine(WinDir, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache");
        if (Directory.Exists(sysFont))
            cat.Items.Add(new TraceItem
            {
                Path = sysFont,
                SizeBytes = GetDirSize(sysFont),
                IsDirectory = true,
            });
        return cat;
    }

    private static TraceCategory ScanUsbDeviceHistory()
    {
        var cat = new TraceCategory
        {
            Name = "USB Device History",
            Description = "Registry records of previously connected USB devices, SetupAPI logs",
        };

        try
        {
            using var usbstorKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USBSTOR");
            if (usbstorKey != null)
            {
                foreach (var deviceClass in usbstorKey.GetSubKeyNames())
                {
                    using var classKey = usbstorKey.OpenSubKey(deviceClass);
                    if (classKey == null) continue;
                    foreach (var serial in classKey.GetSubKeyNames())
                    {
                        cat.Items.Add(new TraceItem
                        {
                            Path = $@"HKLM\SYSTEM\CurrentControlSet\Enum\USBSTOR\{deviceClass}\{serial}",
                            SizeBytes = 0,
                            IsDirectory = false,
                        });
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"USB device history scan failed: {ex.Message}"); }

        var setupApiLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "inf", "setupapi.dev.log");
        AddFile(cat, setupApiLog);

        var setupApiAppLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "inf", "setupapi.app.log");
        AddFile(cat, setupApiAppLog);

        return cat;
    }

    private static readonly HashSet<string> CookieFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookies", "Cookies-journal", "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm",
    };

    public static bool IsCookiePath(string path) =>
        CookieFileNames.Contains(Path.GetFileName(path));

    private static CookieCleanResult CleanCookieDatabase(
        string dbPath, IReadOnlyList<string> whitelist, bool dryRun)
    {
        var fileName = Path.GetFileName(dbPath);
        var profile = Path.GetFileName(Path.GetDirectoryName(dbPath) ?? "");
        var isFirefox = fileName.StartsWith("cookies.sqlite", StringComparison.OrdinalIgnoreCase);
        return isFirefox
            ? CookieDomainCleaner.CleanFirefox(dbPath, whitelist, dryRun, profile)
            : CookieDomainCleaner.CleanChromium(dbPath, whitelist, dryRun, profile);
    }

    private static TraceCategory ScanBrowserCookies()
    {
        var cat = new TraceCategory
        {
            Name = "Browser Cookies",
            Description = "Cookie databases from Chrome, Edge, Brave, Firefox, Vivaldi, Opera",
            IsSelected = AppSettings.Current.CookieWhitelist.Count == 0,
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var chromiumRoots = new[]
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(localAppData, "Vivaldi", "User Data"),
        };

        foreach (var root in chromiumRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var profile in Directory.GetDirectories(root))
                {
                    var cookieFile = Path.Combine(profile, "Cookies");
                    AddFile(cat, cookieFile);
                    AddFile(cat, cookieFile + "-journal");
                }
            }
            catch (Exception ex) { Log.Warn($"Cookie scan failed in '{root}': {ex.Message}"); }
        }

        var operaCookies = Path.Combine(appData, "Opera Software", "Opera Stable", "Cookies");
        AddFile(cat, operaCookies);
        AddFile(cat, operaCookies + "-journal");

        try
        {
            var firefoxProfiles = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
            if (Directory.Exists(firefoxProfiles))
            {
                foreach (var profile in Directory.GetDirectories(firefoxProfiles))
                {
                    AddFile(cat, Path.Combine(profile, "cookies.sqlite"));
                    AddFile(cat, Path.Combine(profile, "cookies.sqlite-wal"));
                    AddFile(cat, Path.Combine(profile, "cookies.sqlite-shm"));
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Firefox cookie scan failed: {ex.Message}"); }

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
        catch (Exception ex) { Log.Warn($"Failed to add file '{path}': {ex.Message}"); }
    }

    private static string? RunCommand(string exe, string args)
    {
        try
        {
            var result = ExternalProcessRunner.Run(new ExternalProcessCommand(exe)
            {
                Arguments = SplitCommandLine(args),
                Timeout = TimeSpan.FromSeconds(15),
                RedactAbsolutePaths = true,
            });
            if (!result.Success)
            {
                Log.Warn($"Command execution failed ({result.RedactedCommandLine}): {result.Status}");
                return $"Command failed ({result.Status}): {result.RedactedCommandLine}";
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Command execution failed ({exe}): {ex.Message}");
            return $"Command failed: {exe}";
        }
    }

    private static IReadOnlyList<string> SplitCommandLine(string args)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var c in args)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static long GetDirSize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Sum(fi => { try { return fi.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private static string FormatSkippedReason(string reason, string label)
        => PrivacyRedactor.RedactPaths($"{reason}: {label}");
}
