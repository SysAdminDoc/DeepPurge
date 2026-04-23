using System.Collections.ObjectModel;
using System.Net.Http;
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
    // Single shared HttpClient for any VM-side network work. Reusing one
    // instance prevents socket-exhaustion under repeated polling and picks
    // up DNS changes correctly under .NET 8. Timeout is set to 15s — any
    // winapp2 mirror slower than that should fail fast rather than freeze
    // the UI behind a spinner.
    private static readonly HttpClient _vmHttp = CreateVmHttp();
    private static HttpClient CreateVmHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepPurge/0.9 (+https://github.com/SysAdminDoc/DeepPurge)");
        return http;
    }

    // ═══════════════════════════════════════════════════════
    //  DRIVER STORE
    // ═══════════════════════════════════════════════════════
    public ObservableCollection<DriverPackage> DriverPackages { get; } = new();
    [ObservableProperty] private string _driverBadge = "";
    [ObservableProperty] private string _driverSummary = "";

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
    [ObservableProperty] private string _duplicateSummary = "";

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
            StatusText = $"{(opt.DryRun ? "Would delete" : "Deleted")} {n} duplicate file(s).";
        }
        catch (Exception ex) { Log.Error("DeleteDuplicates", ex); StatusText = $"Duplicate delete failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  WINDOWS REPAIR
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] private string _repairOutput = "";
    [ObservableProperty] private bool   _repairRunning;

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
    [ObservableProperty] private string _winapp2Source = "";

    [RelayCommand]
    private async Task LoadWinapp2Async()
    {
        var localIni = Path.Combine(DataPaths.Cleaners, "winapp2.ini");
        try
        {
            if (!File.Exists(localIni))
            {
                StatusText = "Downloading winapp2.ini...";
                // The shared HttpClient.Timeout already caps this — no extra CTS needed.
                var url = "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";
                var ini = await _vmHttp.GetStringAsync(url);
                await File.WriteAllTextAsync(localIni, ini);
                Winapp2Source = $"downloaded from {url}";
            }
            else Winapp2Source = localIni;

            var entries = Winapp2Parser.ParseFile(localIni);
            _dispatcher.Invoke(() =>
            {
                Winapp2Entries.Clear();
                foreach (var e in entries.Where(e => e.IsApplicable())) Winapp2Entries.Add(e);
                StatusText = $"{Winapp2Entries.Count} applicable / {entries.Count} total cleaners";
            });
        }
        catch (TaskCanceledException)
        {
            StatusText = "winapp2.ini download timed out. Check connection and retry.";
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"winapp2 download: {ex.Message}");
            StatusText = $"winapp2.ini unreachable: {ex.Message}";
        }
        catch (Exception ex)
        {
            Log.Error("LoadWinapp2Async", ex);
            StatusText = $"winapp2 load failed: {ex.Message}";
        }
    }

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
    [ObservableProperty] private string _snapshotStatus = "";

    public async Task<InstallDelta?> TraceInstallerAsync(string programName, string installerPath, string? args = null)
    {
        SnapshotStatus = $"Capturing baseline for {programName}...";
        try
        {
            var delta = await new InstallSnapshotEngine().TraceInstallAsync(programName, installerPath, args);
            _dispatcher.Invoke(() =>
            {
                SnapshotStatus = $"{programName}: +{delta.AddedFiles.Count} files, " +
                                 $"+{delta.AddedRegistryKeys.Count} keys, " +
                                 $"+{FormatSize(delta.TotalAddedBytes)}";
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
    //  ABOUT PANEL
    // ═══════════════════════════════════════════════════════
    public string AppVersionDisplay =>
        (typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 9, 0)).ToString(3);

    public string DataRootDisplay => DataPaths.Root;

    public string PortableStatusDisplay => DataPaths.IsPortable
        ? "ON — settings live next to the exe in the Data\\ folder."
        : "OFF — settings live in %LocalAppData%\\DeepPurge\\. Drop a file named 'DeepPurge.portable' next to the exe and restart to switch.";

    // ═══════════════════════════════════════════════════════
    //  UPDATE CHECK
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] private string _updateText = "";

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
}
