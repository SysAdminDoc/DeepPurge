using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPurge.Core.App;
using DeepPurge.Core.Cleaning;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Drivers;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.InstallMonitor;
using DeepPurge.Core.Repair;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Schedule;
using DeepPurge.Core.Firewall;
using DeepPurge.Core.Shell;
using DeepPurge.Core.Shortcuts;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Updates;

namespace DeepPurge.App.ViewModels;

/// <summary>
/// v0.9.0 feature surface. The main <see cref="MainViewModel"/> stays focused
/// on the pre-v0.9 feature set; this partial exposes the ten new Core services
/// through observable collections and async RelayCommand methods for future
/// XAML panels (and for anything that wants to dispatch the same logic
/// programmatically, e.g. status-bar shortcuts).
/// </summary>
public partial class MainViewModel
{
    // ═══════════════════════════════════════════════════════
    //  DRIVER STORE
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<DriverPackage> DriverPackages { get; } = new();
    [ObservableProperty] public partial string DriverBadge { get; set; } = "";
    [ObservableProperty] public partial string DriverSummary { get; set; } = "";

    [RelayCommand]
    private async Task ScanDriversAsync()
    {
        StatusText = "Enumerating driver packages...";
        try
        {
            var pkgs = await new DriverStoreScanner().EnumerateAsync();
            _dispatcher.Invoke(() =>
            {
                DriverPackages.Clear();
                foreach (var p in pkgs.OrderByDescending(p => p.SizeBytes)) DriverPackages.Add(p);
                var old = pkgs.Count(p => p.IsOldVersion);
                DriverBadge = old > 0 ? $"{old} old" : "";
                DriverSummary = $"{pkgs.Count} packages, {old} old versions, {FormatSize(pkgs.Sum(p => p.SizeBytes))} total";
                StatusText = DriverSummary;
            });
        }
        catch (Exception ex)
        {
            Log.Error("ScanDriversAsync", ex);
            StatusText = $"Driver scan failed: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════════════════
    //  STARTUP IMPACT
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<StartupImpactEntry> StartupImpacts { get; } = new();

    [RelayCommand]
    private async Task ScanStartupImpactAsync()
    {
        StatusText = "Parsing WDI startup traces...";
        try
        {
            var impacts = await Task.Run(() => new StartupImpactCalculator().CalculateForCurrentUser());
            _dispatcher.Invoke(() =>
            {
                StartupImpacts.Clear();
                foreach (var e in impacts.Values
                                        .OrderByDescending(e => (int)e.Impact)
                                        .ThenByDescending(e => e.DiskBytes))
                    StartupImpacts.Add(e);
                var high = impacts.Values.Count(e => e.Impact == StartupImpact.High);
                StatusText = impacts.Count == 0
                    ? "No WDI startup traces yet (reboot Windows and retry)"
                    : $"{impacts.Count} startup processes, {high} high-impact";
            });
        }
        catch (Exception ex) { Log.Error("ScanStartupImpactAsync", ex); StatusText = $"Startup scan failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  SHORTCUT REPAIR
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<ShortcutEntry> BrokenShortcuts { get; } = new();

    [RelayCommand]
    private async Task ScanShortcutsAsync()
    {
        StatusText = "Scanning Desktop + Start Menu shortcuts...";
        try
        {
            var scanner = new ShortcutRepairScanner();
            var all = await scanner.ScanAllAsync();
            var broken = all.Where(s => s.Status == ShortcutStatus.Broken).ToList();
            _dispatcher.Invoke(() =>
            {
                BrokenShortcuts.Clear();
                foreach (var s in broken) BrokenShortcuts.Add(s);
                StatusText = $"{broken.Count} broken / {all.Count} total shortcuts";
            });
        }
        catch (Exception ex) { Log.Error("ScanShortcutsAsync", ex); StatusText = $"Shortcut scan failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void RecycleBrokenShortcuts()
    {
        try
        {
            var scanner = new ShortcutRepairScanner();
            var n = scanner.RecycleBroken(BrokenShortcuts.ToList());
            _dispatcher.Invoke(() =>
            {
                BrokenShortcuts.Clear();
                StatusText = $"Moved {n} broken shortcut(s) to Recycle Bin.";
            });
        }
        catch (Exception ex) { Log.Error("RecycleBrokenShortcuts", ex); StatusText = $"Shortcut delete failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  DUPLICATE FINDER
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = new();
    [ObservableProperty] public partial string DuplicateSummary { get; set; } = "";

    [RelayCommand]
    private async Task ScanDuplicatesAsync()
    {
        StatusText = "Scanning for duplicates...";
        try
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            var finder = new DuplicateFinder();
            var groups = await finder.FindAsync(roots, progress: new Progress<string>(s => StatusText = s));
            _dispatcher.Invoke(() =>
            {
                DuplicateGroups.Clear();
                foreach (var g in groups) DuplicateGroups.Add(g);
                DuplicateSummary = $"{groups.Count} groups, {FormatSize(groups.Sum(g => g.WastedBytes))} reclaimable";
                StatusText = DuplicateSummary;
            });
        }
        catch (Exception ex) { Log.Error("ScanDuplicatesAsync", ex); StatusText = $"Duplicate scan failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void DeleteDuplicates()
    {
        try
        {
            var opt = new DeleteOptions(
                DryRun: DryRunEnabled,
                SecureDelete: SecureDeleteEnabled,
                UseRecycleBin: !SecureDeleteEnabled);
            var n = new DuplicateFinder().DeleteDuplicates(DuplicateGroups.ToList(), opt);
            ActivityLog.Record("duplicates", $"{(opt.DryRun ? "Would delete" : "Deleted")} {n} duplicate file(s)", itemCount: n, dryRun: opt.DryRun);
            StatusText = $"{(opt.DryRun ? "Would delete" : "Deleted")} {n} duplicate file(s).";
        }
        catch (Exception ex) { Log.Error("DeleteDuplicates", ex); StatusText = $"Duplicate delete failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  WINDOWS REPAIR
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] public partial string RepairOutput { get; set; } = "";
    [ObservableProperty] public partial bool RepairRunning { get; set; }

    // Backing buffer — appending to a string property in a tight loop is
    // O(n²) in characters and also raises PropertyChanged per line.
    // A StringBuilder + periodic snapshot keeps the textbox update cost
    // proportional to lines, not to characters of accumulated output.
    private readonly System.Text.StringBuilder _repairBuffer = new();
    private readonly object _repairBufferLock = new();
    private DateTime _lastRepairFlush = DateTime.MinValue;

    public async Task RunRepairAsync(RepairOperation op)
    {
        RepairRunning = true;
        lock (_repairBufferLock) _repairBuffer.Clear();
        RepairOutput = "";

        try
        {
            var engine = new WindowsRepairEngine();
            var log = new Progress<string>(line => AppendRepairLine(line));
            var r = await engine.RunAsync(op, log);

            // Final flush so the last lines make it onto the screen.
            FlushRepairBuffer(force: true);

            ActivityLog.Record("repair", $"{op}: exit={r.ExitCode} in {r.Elapsed:mm\\:ss}");
            _dispatcher.Invoke(() => StatusText = $"[{op}] exit={r.ExitCode} in {r.Elapsed:mm\\:ss}");
        }
        catch (Exception ex) { Log.Error($"RunRepairAsync({op})", ex); StatusText = $"Repair failed: {ex.Message}"; }
        finally { _dispatcher.Invoke(() => RepairRunning = false); }
    }

    private void AppendRepairLine(string line)
    {
        lock (_repairBufferLock) _repairBuffer.AppendLine(line);
        // Coalesce flushes: repaint at most ~10×/sec to avoid UI thrashing
        // when a tool produces hundreds of lines per second (DISM does).
        FlushRepairBuffer(force: false);
    }

    private void FlushRepairBuffer(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastRepairFlush).TotalMilliseconds < 100) return;
        _lastRepairFlush = now;
        string snapshot;
        lock (_repairBufferLock) snapshot = _repairBuffer.ToString();
        _dispatcher.BeginInvoke(() => RepairOutput = snapshot);
    }

    [RelayCommand] private Task RunSfcAsync()             => RunRepairAsync(RepairOperation.SfcScan);
    [RelayCommand] private Task RunDismScanAsync()        => RunRepairAsync(RepairOperation.DismScanHealth);
    [RelayCommand] private Task RunDismRestoreAsync()     => RunRepairAsync(RepairOperation.DismRestoreHealth);
    [RelayCommand] private Task RunDismCleanupAsync()     => RunRepairAsync(RepairOperation.DismComponentCleanup);
    [RelayCommand] private Task RunChkDskAsync()          => RunRepairAsync(RepairOperation.ChkDsk);
    [RelayCommand] private Task RebuildFontCacheAsync()   => RunRepairAsync(RepairOperation.RebuildFontCache);
    [RelayCommand] private Task RebuildIconCacheAsync()   => RunRepairAsync(RepairOperation.RebuildIconCache);

    // ═══════════════════════════════════════════════════════
    //  WINAPP2 COMMUNITY CLEANERS
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<Winapp2Entry> Winapp2Entries { get; } = new();
    public ObservableCollection<CleanerValidationReport> CleanerValidationReports { get; } = new();
    [ObservableProperty] public partial string Winapp2Source { get; set; } = "";
    [ObservableProperty] public partial string CleanerValidationSummary { get; set; } = "";

    [RelayCommand]
    private async Task LoadWinapp2Async()
    {
        var localIni = Winapp2Updater.LocalPath;
        try
        {
            StatusText = File.Exists(localIni)
                ? "Loading winapp2.ini..."
                : "Downloading winapp2.ini...";

            var provenance = await Winapp2Updater.GetProvenanceAsync();
            if (!File.Exists(localIni))
            {
                var update = await Winapp2Updater.UpdateDetailedAsync();
                if (!update.Success)
                {
                    Winapp2Source = FormatWinapp2Provenance(provenance);
                    StatusText = $"winapp2.ini download failed: {update.ErrorMessage}";
                    return;
                }
                provenance = await Winapp2Updater.GetProvenanceAsync();
            }

            Winapp2Source = FormatWinapp2Provenance(provenance);

            var entries = Winapp2Parser.ParseFile(localIni);
            _dispatcher.Invoke(() =>
            {
                Winapp2Entries.Clear();
                foreach (var e in entries.Where(e => e.IsApplicable())) Winapp2Entries.Add(e);

                var reports = CleanerDefinitionRunner.ValidateAll();
                CleanerValidationReports.Clear();
                foreach (var report in reports.OrderByDescending(r => r.RiskLevel).ThenBy(r => r.FileName))
                    CleanerValidationReports.Add(report);

                var blocked = reports.Count(r => !r.IsValid);
                var ready = reports.Count - blocked;
                CleanerValidationSummary = reports.Count == 0
                    ? "Custom JSON cleaners: none found"
                    : $"Custom JSON cleaners: {ready} ready, {blocked} blocked";

                StatusText = $"{Winapp2Entries.Count} applicable / {entries.Count} winapp2 cleaners; {CleanerValidationSummary}";
            });
        }
        catch (TaskCanceledException)
        {
            StatusText = "winapp2.ini download timed out. Check connection and retry.";
        }
        catch (Exception ex)
        {
            Log.Error("LoadWinapp2Async", ex);
            StatusText = $"winapp2 load failed: {ex.Message}";
        }
    }

    private static string FormatWinapp2Provenance(Winapp2Provenance provenance)
    {
        var local = provenance.LocalExists
            ? provenance.LocalMetadata is { } metadata
                ? $"local {metadata.ShortCommit} ({metadata.CommitDateUtc:yyyy-MM-dd}), {FormatBytes(metadata.ByteCount)}, sha256 {metadata.ShortSha256}"
                : $"local file {(provenance.LocalWriteTimeUtc.HasValue ? provenance.LocalWriteTimeUtc.Value.ToString("yyyy-MM-dd") : "date unknown")}, {FormatBytes(provenance.LocalByteCount ?? 0)}, sha256 {ShortHash(provenance.LocalSha256)}"
            : "not downloaded";

        var remote = provenance.Remote is { } remoteInfo
            ? $"remote {remoteInfo.ShortCommit} ({remoteInfo.CommitDateUtc:yyyy-MM-dd})"
            : $"remote unavailable{(string.IsNullOrWhiteSpace(provenance.RemoteError) ? "" : $": {provenance.RemoteError}")}";

        var backup = provenance.LocalMetadata?.BackupPath is { Length: > 0 } path
            ? $"; previous backup {Path.GetFileName(path)}"
            : "";

        return $"{local}; {remote}{backup}";
    }

    private static string ShortHash(string? hash)
        => string.IsNullOrWhiteSpace(hash) ? "unknown" : hash.Length <= 12 ? hash : hash[..12];

    [RelayCommand]
    private async Task RunWinapp2Async()
    {
        try
        {
            var runner = new Winapp2Runner();
            var opt = new DeleteOptions(
                DryRun: DryRunEnabled,
                SecureDelete: SecureDeleteEnabled,
                UseRecycleBin: !SecureDeleteEnabled);
            var progress = new Progress<DeleteProgress>(p => _dispatcher.BeginInvoke(() =>
            {
                OperationProgress = p.Percent;
                OperationProgressText = $"{p.CurrentItem} ({p.ItemsProcessed}/{p.ItemsTotal})";
                OperationProgressVisible = true;
            }));
            var s = await runner.RunAsync(Winapp2Entries.ToList(), opt, progress);
            ActivityLog.Record("winapp2", $"{(opt.DryRun ? "Would free" : "Freed")} {FormatSize(s.BytesFreed)} across {s.ItemsDeleted} entries", s.BytesFreed, s.ItemsDeleted, opt.DryRun);
            _dispatcher.Invoke(() =>
            {
                OperationProgressVisible = false;
                StatusText = $"winapp2: {(opt.DryRun ? "would free" : "freed")} " +
                             $"{FormatSize(s.BytesFreed)} across {s.ItemsDeleted} entries " +
                             $"({s.ItemsSkipped} skipped)";
            });
        }
        catch (Exception ex) { Log.Error("RunWinapp2Async", ex); StatusText = $"winapp2 run failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  INSTALL MONITOR
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] public partial string SnapshotStatus { get; set; } = "";

    public async Task<InstallDelta?> TraceInstallerAsync(string programName, string installerPath, string? args = null)
    {
        SnapshotStatus = $"Capturing baseline for {programName}...";
        try
        {
            var engine = new InstallSnapshotEngine();
            var useV2 = UsnJournalReader.IsSupported(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ?? @"C:\");
            SnapshotStatus = useV2
                ? $"Tracing {programName} via USN journal..."
                : $"Capturing baseline for {programName}...";
            var delta = useV2
                ? await engine.TraceInstallV2Async(programName, installerPath, args)
                : await engine.TraceInstallAsync(programName, installerPath, args);
            _dispatcher.Invoke(() =>
            {
                var parts = new List<string>
                {
                    $"+{delta.AddedFiles.Count} files (+{FormatSize(delta.TotalAddedBytes)})",
                    $"+{delta.AddedRegistryKeys.Count} keys"
                };
                if (delta.RemovedFiles.Count > 0)
                    parts.Add($"-{delta.RemovedFiles.Count} removed files");
                if (delta.RemovedRegistryKeys.Count > 0)
                    parts.Add($"-{delta.RemovedRegistryKeys.Count} removed keys");
                SnapshotStatus = $"{programName}: {string.Join(", ", parts)}";
            });
            return delta;
        }
        catch (Exception ex)
        {
            Log.Error("TraceInstallerAsync", ex);
            SnapshotStatus = $"Trace failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Forced uninstall by manifest: looks up a previously-captured install
    /// trace for <paramref name="programName"/> and replays its delete list
    /// through SafetyGuard. This is the flagship "open-source Revo" feature
    /// — it lets the uninstall pipeline fall back from heuristic leftover
    /// matching to an exact per-app manifest when one is available.
    /// </summary>
    public async Task<(bool Found, int Removed, int Skipped, long Freed)> ForcedUninstallByManifestAsync(
        string programName, CancellationToken ct = default)
    {
        var engine = new InstallSnapshotEngine();
        var delta = engine.LoadManifest(programName);
        if (delta == null)
        {
            SnapshotStatus = $"No install manifest recorded for '{programName}'. Run 'snapshot trace' first.";
            return (false, 0, 0, 0);
        }

        var opt = new DeleteOptions(
            DryRun: DryRunEnabled,
            SecureDelete: SecureDeleteEnabled,
            UseRecycleBin: !SecureDeleteEnabled);

        var progress = new Progress<DeleteProgress>(p => _dispatcher.BeginInvoke(() =>
        {
            OperationProgress = p.Percent;
            OperationProgressText = $"{p.CurrentItem} ({p.ItemsProcessed}/{p.ItemsTotal})";
            OperationProgressVisible = true;
        }));

        try
        {
            var (removed, skipped, freed) = await engine.ReplayRemoveAsync(delta, opt, progress, ct);
            _dispatcher.Invoke(() =>
            {
                OperationProgressVisible = false;
                SnapshotStatus = $"{programName}: {(opt.DryRun ? "would remove" : "removed")} " +
                                 $"{removed} file(s), skipped {skipped}, freed {FormatSize(freed)}";
            });
            return (true, removed, skipped, freed);
        }
        catch (Exception ex)
        {
            Log.Error("ForcedUninstallByManifestAsync", ex);
            _dispatcher.Invoke(() => OperationProgressVisible = false);
            SnapshotStatus = $"Manifest replay failed: {ex.Message}";
            return (true, 0, 0, 0);
        }
    }

    // ═══════════════════════════════════════════════════════
    //  SCHEDULED JOBS
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<string> ScheduledJobs { get; } = new();

    [RelayCommand]
    private void RefreshScheduledJobs()
    {
        try
        {
            var jobs = new ScheduleManager().ListJobs();
            _dispatcher.Invoke(() =>
            {
                ScheduledJobs.Clear();
                foreach (var j in jobs) ScheduledJobs.Add(j);
            });
        }
        catch (Exception ex) { Log.Error("RefreshScheduledJobs", ex); StatusText = $"Schedule list failed: {ex.Message}"; }
    }

    public bool CreateScheduledJob(string name, ScheduleFrequency freq, DayOfWeek day, int hh, int mm, string cliArgs)
    {
        var cliPath = ResolveCliPath();
        if (cliPath == null)
        {
            StatusText = "DeepPurgeCli.exe not found. Run BUILD.bat or publish the CLI first.";
            return false;
        }
        try
        {
            var ok = new ScheduleManager().CreateJob(
                new ScheduleJob(name, freq, day, hh, mm, cliArgs), cliPath);
            RefreshScheduledJobs();
            return ok;
        }
        catch (Exception ex) { Log.Error("CreateScheduledJob", ex); StatusText = $"Schedule create failed: {ex.Message}"; return false; }
    }

    private static string? ResolveCliPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate1 = Path.Combine(baseDir, "DeepPurgeCli.exe");
        if (File.Exists(candidate1)) return candidate1;

        // Dev-box fallback: sibling bin tree for F5 runs.
        var parent = Path.GetDirectoryName(baseDir);
        if (parent != null)
        {
            var candidate2 = Path.Combine(parent, "DeepPurge.Cli", "DeepPurgeCli.exe");
            if (File.Exists(candidate2)) return candidate2;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════
    //  ACTIVITY HISTORY
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<ActivityEntry> HistoryEntries { get; } = new();
    public ObservableCollection<ActivityLog.DailyCleanSummary> CleanHistory { get; } = new();
    [ObservableProperty] public partial string CleanHistorySummary { get; set; } = "";

    [RelayCommand]
    private void LoadHistory()
    {
        try
        {
            var entries = ActivityLog.LoadRecent(200);
            var daily = ActivityLog.GetCleanHistory(90);
            _dispatcher.Invoke(() =>
            {
                HistoryEntries.Clear();
                foreach (var e in entries) HistoryEntries.Add(e);

                CleanHistory.Clear();
                foreach (var d in daily) CleanHistory.Add(d);

                var totalFreed = daily.Sum(d => d.TotalBytesFreed);
                var totalRuns = daily.Sum(d => d.RunCount);
                CleanHistorySummary = totalRuns > 0
                    ? $"{totalRuns} cleanup runs over {daily.Count} days — {FormatBytes(totalFreed)} total freed"
                    : "No cleanup history yet";

                StatusText = $"{entries.Count} history entries loaded";
            });
        }
        catch (Exception ex) { Log.Error("LoadHistory", ex); }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F0} KB";
        double mb = kb / 1024.0;
        return mb < 1024 ? $"{mb:F1} MB" : $"{mb / 1024.0:F2} GB";
    }

    // ═══════════════════════════════════════════════════════
    //  HEALTH DASHBOARD
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<HealthScore> HealthCategories { get; } = new();
    [ObservableProperty] public partial int HealthOverallScore { get; set; }
    [ObservableProperty] public partial string HealthGrade { get; set; } = "";
    [ObservableProperty] public partial string HealthSummary { get; set; } = "Run health check to assess system hygiene";

    [RelayCommand]
    private async Task RunHealthCheckAsync()
    {
        IsBusy = true;
        StatusText = "Running health assessment...";
        try
        {
            var report = await HealthScorer.AssessAsync();
            _dispatcher.Invoke(() =>
            {
                HealthCategories.Clear();
                foreach (var c in report.Categories) HealthCategories.Add(c);
                HealthOverallScore = report.OverallScore;
                HealthGrade = report.Grade;
                HealthSummary = $"Overall: {report.Grade} ({report.OverallScore}/100)";
                StatusText = HealthSummary;
            });
        }
        catch (Exception ex) { StatusText = $"Health check failed: {ex.Message}"; Log.Error("HealthCheck", ex); }
        finally { IsBusy = false; }
    }

    // ═══════════════════════════════════════════════════════
    //  SYSTEM SLIMMING
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<SlimmableComponent> SlimmableComponents { get; } = new();
    [ObservableProperty] public partial string SlimSummary { get; set; } = "";

    [RelayCommand]
    private async Task ScanSlimmableAsync()
    {
        IsBusy = true;
        StatusText = "Scanning removable Windows components...";
        try
        {
            var items = await Task.Run(() => SystemSlimmer.Scan());
            _dispatcher.Invoke(() =>
            {
                SlimmableComponents.Clear();
                foreach (var c in items) SlimmableComponents.Add(c);
                var totalSize = items.Sum(c => c.SizeBytes);
                SlimSummary = $"{items.Count} components found — {FormatBytes(totalSize)} total";
                StatusText = SlimSummary;
            });
        }
        catch (Exception ex) { StatusText = $"Slim scan failed: {ex.Message}"; Log.Error("ScanSlimmable", ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RunSlimAsync()
    {
        IsBusy = true;
        StatusText = "Removing selected Windows components...";
        try
        {
            var selected = SlimmableComponents.Where(c => c.IsSelected).ToList();
            var opts = new DeleteOptions(DryRun: DryRunEnabled, SecureDelete: false, UseRecycleBin: false);
            var progress = new Progress<DeleteProgress>(p =>
                _dispatcher.BeginInvoke(() => StatusText = $"Slimming: {p.CurrentItem} ({p.ItemsProcessed}/{p.ItemsTotal})"));
            var result = await Task.Run(() => SystemSlimmer.Delete(selected, opts, progress));
            StatusText = opts.DryRun
                ? $"Dry-run: would free {FormatBytes(result.BytesFreed)} from {result.ItemsDeleted} components"
                : $"Freed {FormatBytes(result.BytesFreed)} from {result.ItemsDeleted} components";
            await ScanSlimmableAsync();
        }
        catch (Exception ex) { StatusText = $"Slim failed: {ex.Message}"; Log.Error("RunSlim", ex); }
        finally { IsBusy = false; }
    }

    // ═══════════════════════════════════════════════════════
    //  ABOUT PANEL
    // ═══════════════════════════════════════════════════════
    public string AppVersionDisplay =>
        (typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 9, 0)).ToString(3);

    public string DataRootDisplay => DataPaths.Root;

    public string PortableStatusDisplay => DataPaths.IsPortable
        ? "ON — settings live next to the exe in the Data\\ folder."
        : "OFF — settings live in %LocalAppData%\\DeepPurge\\. Drop a file named 'DeepPurge.portable' next to the exe and restart to switch.";

    // ═══════════════════════════════════════════════════════
    //  ORPHANED ARTIFACTS (unified panel)
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<FirewallRuleEntry> OrphanedFirewallRules { get; } = new();
    public ObservableCollection<PathEntry> OrphanedPathEntries { get; } = new();
    [ObservableProperty] public partial string OrphanBadge { get; set; } = "";
    [ObservableProperty] public partial string OrphanSummary { get; set; } = "";

    [RelayCommand]
    private async Task ScanOrphansAsync()
    {
        StatusText = "Scanning for orphaned artifacts (services, tasks, firewall, PATH)...";
        try
        {
            // Run all four orphan scans in parallel.
            var fwTask = Task.Run(() => FirewallRuleScanner.GetAllRules(orphanedOnly: true));
            var pathTask = Task.Run(() => PathCleaner.ScanPathEntries(orphanedOnly: true));
            var svcTask = Task.Run(() => Core.Services.ServiceScanner.GetAllServices(orphanedOnly: true));
            var taskTask = Task.Run(() => Core.Tasks.ScheduledTaskScanner.GetAllTasks()
                .Where(t => t.IsOrphaned).ToList());

            await Task.WhenAll(fwTask, pathTask, svcTask, taskTask);

            var fwRules = fwTask.Result;
            var pathEntries = pathTask.Result;
            var orphanedSvcs = svcTask.Result;
            var orphanedTasks = taskTask.Result;

            _dispatcher.Invoke(() =>
            {
                OrphanedFirewallRules.Clear();
                foreach (var r in fwRules) OrphanedFirewallRules.Add(r);

                OrphanedPathEntries.Clear();
                foreach (var p in pathEntries) OrphanedPathEntries.Add(p);

                // Services and tasks already have panels — update their collections too.
                // (The existing Apply* methods handle full replacement, but orphan-only
                // results should merge: we flag the orphaned count on the Orphans panel.)
                var total = orphanedSvcs.Count + orphanedTasks.Count + fwRules.Count + pathEntries.Count;
                OrphanBadge = total > 0 ? total.ToString() : "";
                OrphanSummary = $"{orphanedSvcs.Count} services, {orphanedTasks.Count} tasks, " +
                                $"{fwRules.Count} firewall rules, {pathEntries.Count} PATH entries";
                StatusText = $"Orphan scan: {total} orphaned artifacts — {OrphanSummary}";
            });
        }
        catch (Exception ex)
        {
            Log.Error("ScanOrphansAsync", ex);
            StatusText = $"Orphan scan failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteOrphanedFirewallRules()
    {
        try
        {
            var selected = OrphanedFirewallRules.Where(r => r.IsSelected).ToList();
            var n = FirewallRuleScanner.DeleteRules(selected);
            foreach (var r in selected.Where(r => r.IsSelected))
                OrphanedFirewallRules.Remove(r);
            ActivityLog.Record("orphans", $"Deleted {n} orphaned firewall rule(s)", itemCount: n);
            StatusText = $"Deleted {n} orphaned firewall rule(s).";
        }
        catch (Exception ex) { Log.Error("DeleteOrphanedFirewallRules", ex); StatusText = $"Delete failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void RemoveOrphanedPathEntries()
    {
        try
        {
            var selected = OrphanedPathEntries.Where(p => p.IsSelected).ToList();
            var n = PathCleaner.RemoveOrphanedEntries(selected);
            foreach (var p in selected.Where(p => p.IsSelected))
                OrphanedPathEntries.Remove(p);
            ActivityLog.Record("orphans", $"Removed {n} orphaned PATH entr(ies)", itemCount: n);
            StatusText = $"Removed {n} orphaned PATH entr(ies).";
        }
        catch (Exception ex) { Log.Error("RemoveOrphanedPathEntries", ex); StatusText = $"Remove failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  UPDATE CHECK
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] public partial string UpdateText { get; set; } = "";

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var cur = (typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 9, 0)).ToString(3);
            var info = await new UpdateChecker().CheckAsync(cur);
            _dispatcher.Invoke(() =>
            {
                if (info == null) UpdateText = "Update check failed (network error or rate-limited).";
                else if (info.HasUpdate) UpdateText = $"Update available: v{info.LatestVersion} → {info.ReleaseUrl}";
                else UpdateText = $"Up to date (v{info.CurrentVersion}).";
                StatusText = UpdateText;
            });
        }
        catch (Exception ex) { Log.Error("CheckForUpdatesAsync", ex); StatusText = $"Update check failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  CLIPBOARD (extension panels)
    // ═══════════════════════════════════════════════════════

    [RelayCommand]
    private void CopyHistoryToClipboard()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Timestamp\tOperation\tSummary\tBytes Freed\tItems\tDry Run");
        foreach (var e in HistoryEntries)
            sb.AppendLine($"{e.TimestampUtc:yyyy-MM-dd HH:mm}\t{e.Operation}\t{e.Summary}\t{FormatBytes(e.BytesFreed)}\t{e.ItemCount}\t{e.DryRun}");
        SetClipboard(sb.ToString());
    }

    [RelayCommand]
    private void CopyDriversToClipboard()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Published\tOriginal\tProvider\tVersion\tSize\tOld");
        foreach (var d in DriverPackages)
            sb.AppendLine($"{d.PublishedName}\t{d.OriginalName}\t{d.ProviderName}\t{d.DriverVersion}\t{FormatBytes(d.SizeBytes)}\t{d.IsOldVersion}");
        SetClipboard(sb.ToString());
    }

    [RelayCommand]
    private void CopyStartupImpactToClipboard()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Process\tImpact\tDisk\tCPU ms");
        foreach (var e in StartupImpacts)
            sb.AppendLine($"{e.ProcessName}\t{e.Impact}\t{FormatBytes(e.DiskBytes)}\t{e.CpuMs}");
        SetClipboard(sb.ToString());
    }

    [RelayCommand]
    private void CopyDuplicatesToClipboard()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Size\tWasted\tCopies\tPaths");
        foreach (var g in DuplicateGroups)
            sb.AppendLine($"{FormatBytes(g.FileSize)}\t{FormatBytes(g.WastedBytes)}\t{g.Paths.Count}\t{string.Join(" | ", g.Paths)}");
        SetClipboard(sb.ToString());
    }

    [RelayCommand]
    private void CopyOrphansToClipboard()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Type\tName\tDetail");
        foreach (var s in OrphanedFirewallRules)
            sb.AppendLine($"Firewall\t{s.DisplayName}\t{s.Program}");
        foreach (var p in OrphanedPathEntries)
            sb.AppendLine($"PATH\t{p.Directory}\t{p.Source}");
        SetClipboard(sb.ToString());
    }
}
