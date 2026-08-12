using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPurge.Core.App;
using DeepPurge.Core.Cleaning;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Drivers;
using DeepPurge.Core.Execution;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.InstallMonitor;
using DeepPurge.Core.Repair;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Schedule;
using DeepPurge.Core.Firewall;
using DeepPurge.Core.Security;
using DeepPurge.Core.Shell;
using DeepPurge.Core.Shortcuts;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Updates;

namespace DeepPurge.App.ViewModels;

/// <summary>
/// v0.9.0 feature surface. The main <see cref="MainViewModel"/> stays focused
/// on the pre-v0.9 feature set; this partial exposes the ten new Core services
/// through observable collections and async RelayCommand methods for the
/// corresponding XAML panels (and for anything that wants to dispatch the same logic
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
            if (DuplicateGroups.Any(g => !g.HasExplicitKeeper))
            {
                StatusText = "Select one retained keeper in every duplicate group before deleting.";
                return;
            }
            var groups = DuplicateGroups.ToList();
            var summary = new DuplicateFinder().DeleteDuplicatesDetailed(
                groups,
                opt,
                new DuplicateKeeperPolicy());
            if (!opt.DryRun)
            {
                foreach (var result in summary.Results.Where(r => r.IsConfirmed))
                {
                    var group = groups.FirstOrDefault(g =>
                        g.Paths.Contains(result.Path, StringComparer.OrdinalIgnoreCase));
                    group?.RemovePath(result.Path);
                }
                foreach (var group in groups.Where(g => g.Paths.Count < 2).ToList())
                    DuplicateGroups.Remove(group);
            }
            ActivityLog.Record(
                "duplicates",
                $"{(opt.DryRun ? "Would delete" : "Deleted")} {summary.ItemsDeleted} duplicate file(s)",
                bytesFreed: summary.BytesFreed,
                itemCount: summary.ItemsDeleted,
                dryRun: opt.DryRun);
            StatusText = (opt.DryRun ? "Would delete" : "Deleted") + " " +
                         summary.ItemsDeleted + " duplicate file(s)" +
                         (summary.ItemsSkipped > 0 ? " (" + summary.ItemsSkipped + " skipped" : "") +
                         (summary.ItemsFailed > 0 ? ", " + summary.ItemsFailed + " failed)." : summary.ItemsSkipped > 0 ? ")." : ".");
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
    [ObservableProperty] public partial string CleanerUpdateDiff { get; set; } = "";
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
                CleanerUpdateDiff = update.Diff?.Summary ?? "No target diff available";
                provenance = await Winapp2Updater.GetProvenanceAsync();
            }
            else
            {
                CleanerUpdateDiff = provenance.LocalMetadata?.TargetDiff?.Summary ?? "No recorded update diff";
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
                var schemaWarnings = reports.Sum(r => r.Issues.Count(i =>
                    i.Field.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase) ||
                    i.Field.Equals("Schema", StringComparison.OrdinalIgnoreCase)));
                CleanerValidationSummary = reports.Count == 0
                    ? "Custom JSON cleaners: none found"
                    : $"Custom JSON cleaners: {ready} ready, {blocked} blocked" +
                      (schemaWarnings > 0 ? $", {schemaWarnings} schema warning(s)" : "");

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
                ? $"local {metadata.ShortCommit} ({metadata.CommitDateUtc:yyyy-MM-dd}), {FormatBytes(metadata.ByteCount)}, sha256 {metadata.ShortSha256}, schema v{metadata.SchemaVersion}, {metadata.TrustState}"
                : $"local file {(provenance.LocalWriteTimeUtc.HasValue ? provenance.LocalWriteTimeUtc.Value.ToString("yyyy-MM-dd") : "date unknown")}, {FormatBytes(provenance.LocalByteCount ?? 0)}, sha256 {ShortHash(provenance.LocalSha256)}"
            : "not downloaded";

        var remote = provenance.Remote is { } remoteInfo
            ? $"remote {remoteInfo.ShortCommit} ({remoteInfo.CommitDateUtc:yyyy-MM-dd})"
            : $"remote unavailable{(string.IsNullOrWhiteSpace(provenance.RemoteError) ? "" : $": {provenance.RemoteError}")}";

        var backup = provenance.LocalMetadata?.BackupPath is { Length: > 0 } path
            ? $"; previous backup {Path.GetFileName(path)}"
            : "";
        var diff = provenance.LocalMetadata?.TargetDiff is { } targetDiff
            ? $"; last update {targetDiff.Summary}"
            : "";

        return $"{local}; {remote}{backup}{diff}";
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
                ? $"Tracing {programName} with pre/post snapshots + diagnostic journal evidence..."
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
        var manifest = engine.LoadInstallManifest(programName);
        if (manifest == null)
        {
            SnapshotStatus = $"No install manifest recorded for '{programName}'. Run 'snapshot trace' first.";
            return (false, 0, 0, 0);
        }

        if (!manifest.ReplayEligible)
        {
            SnapshotStatus = $"Manifest replay blocked: {manifest.ReplayEligibilityReason}";
            return (true, 0, manifest.Delta.AddedFiles.Count, 0);
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
            var (removed, skipped, freed) = await engine.ReplayRemoveAsync(manifest, opt, progress, ct);
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
    public ObservableCollection<ScheduledJobInfo> ScheduledJobs { get; } = new();

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
            var manager = new ScheduleManager();
            var ok = manager.CreateJob(
                new ScheduleJob(name, freq, day, hh, mm, cliArgs), cliPath);
            RefreshScheduledJobs();
            StatusText = ok
                ? $"Scheduled protected job '{name}'."
                : $"Schedule create failed: {manager.LastError}";
            return ok;
        }
        catch (Exception ex) { Log.Error("CreateScheduledJob", ex); StatusText = $"Schedule create failed: {ex.Message}"; return false; }
    }

    public bool DeleteScheduledJob(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = "Select a scheduled job first.";
            return false;
        }

        try
        {
            var manager = new ScheduleManager();
            var result = manager.DeleteJobDetailed(name);
            var ok = result.Succeeded;
            RefreshScheduledJobs();
            StatusText = ok
                ? $"Removed scheduled job '{name}'."
                : result.Reason is { Length: > 0 } reason
                    ? $"Scheduled job '{name}' was not changed: {reason}"
                    : $"Failed to remove scheduled job '{name}': {manager.LastError}";
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("DeleteScheduledJob", ex);
            StatusText = $"Schedule remove failed: {ex.Message}";
            return false;
        }
    }

    public bool MigrateLegacyScheduledJobs()
    {
        var cliPath = ResolveCliPath();
        if (cliPath == null)
        {
            StatusText = "DeepPurgeCli.exe not found. Run BUILD.bat or publish the CLI first.";
            return false;
        }

        try
        {
            var results = new ScheduleManager().MigrateLegacyJobs(cliPath);
            RefreshScheduledJobs();
            if (results.Count == 0)
            {
                StatusText = "No legacy scheduled wrappers require migration.";
                return true;
            }

            var migrated = results.Count(result => result.Migrated);
            var failures = results.Count - migrated;
            StatusText = failures == 0
                ? $"Migrated {migrated} legacy job(s) to protected disabled dry-run actions."
                : $"Migrated {migrated}; {failures} failed. Review schedule diagnostics.";
            return failures == 0;
        }
        catch (Exception ex)
        {
            Log.Error("MigrateLegacyScheduledJobs", ex);
            StatusText = $"Schedule migration failed: {ex.Message}";
            return false;
        }
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
    //  DELETION MANIFEST RECOVERY
    // ═══════════════════════════════════════════════════════
    public sealed record DeletionRestoreDetail(string Message);

    public ObservableCollection<ManifestSummary> DeletionManifests { get; } = new();
    public ObservableCollection<DeletionEntry> DeletionManifestEntries { get; } = new();
    public ObservableCollection<DeletionRestoreDetail> DeletionRestoreDetails { get; } = new();

    [ObservableProperty] public partial ManifestSummary? SelectedDeletionManifest { get; set; }
    [ObservableProperty] public partial string DeletionManifestSummary { get; set; } = "Load deletion manifests to inspect rollback options.";
    [ObservableProperty] public partial string DeletionRestoreSummary { get; set; } = "";
    [ObservableProperty] public partial int DeletionRegistryRestored { get; set; }
    [ObservableProperty] public partial int DeletionFilesRecoverable { get; set; }
    [ObservableProperty] public partial int DeletionUnrecoverable { get; set; }

    partial void OnSelectedDeletionManifestChanged(ManifestSummary? value)
    {
        if (value != null) PreviewSelectedDeletionManifest();
    }

    [RelayCommand]
    private void LoadDeletionManifests()
    {
        try
        {
            var summaries = DeletionManifest.ListManifests();
            _dispatcher.Invoke(() =>
            {
                DeletionManifests.Clear();
                foreach (var manifest in summaries) DeletionManifests.Add(manifest);

                if (summaries.Count == 0)
                {
                    SelectedDeletionManifest = null;
                    DeletionManifestEntries.Clear();
                    DeletionRestoreDetails.Clear();
                    SetDeletionRestoreCounts(new RestoreResult(0, 0, 0, new()));
                    DeletionManifestSummary = "No deletion manifests found. Cleanup runs will appear here once they record rollback data.";
                    DeletionRestoreSummary = "";
                    StatusText = "No deletion manifests found.";
                    return;
                }

                SelectedDeletionManifest = summaries[0];
                StatusText = $"Loaded {summaries.Count} deletion manifest(s).";
            });
        }
        catch (Exception ex)
        {
            Log.Error("LoadDeletionManifests", ex);
            StatusText = $"Deletion manifest load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PreviewSelectedDeletionManifest()
    {
        var selected = SelectedDeletionManifest;
        if (selected == null)
        {
            DeletionManifestSummary = "Select a deletion manifest to preview rollback data.";
            DeletionManifestEntries.Clear();
            return;
        }

        try
        {
            var entries = DeletionManifest.LoadManifest(selected.Date);
            var redact = AppSettings.Current.ScrubSensitivePathsInReports;
            _dispatcher.Invoke(() =>
            {
                DeletionManifestEntries.Clear();
                foreach (var entry in entries.OrderByDescending(e => e.TimestampUtc))
                {
                    DeletionManifestEntries.Add(redact
                        ? entry with { Path = PrivacyRedactor.RedactPaths(entry.Path) }
                        : entry);
                }

                DeletionRestoreDetails.Clear();
                SetDeletionRestoreCounts(new RestoreResult(0, 0, 0, new()));
                DeletionRestoreSummary = "";
                DeletionManifestSummary = $"{entries.Count} valid deletion record(s) from {Path.GetFileName(selected.FilePath)}.";
                StatusText = DeletionManifestSummary;
            });
        }
        catch (Exception ex)
        {
            Log.Error("PreviewSelectedDeletionManifest", ex);
            StatusText = $"Deletion manifest preview failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task DryRunRestoreDeletionManifestAsync()
        => RestoreDeletionManifestAsync(dryRun: true);

    [RelayCommand]
    private Task RestoreSelectedDeletionManifestAsync()
        => RestoreDeletionManifestAsync(dryRun: false);

    private async Task RestoreDeletionManifestAsync(bool dryRun)
    {
        var selected = SelectedDeletionManifest;
        if (selected == null)
        {
            StatusText = "Select a deletion manifest first.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await Task.Run(() => DeletionManifest.RestoreFromManifest(selected.Date, dryRun));
            _dispatcher.Invoke(() =>
            {
                SetDeletionRestoreCounts(result);
                DeletionRestoreDetails.Clear();
                foreach (var detail in result.Details)
                    DeletionRestoreDetails.Add(new DeletionRestoreDetail(AppSettings.Current.ScrubSensitivePathsInReports
                        ? PrivacyRedactor.RedactPaths(detail)
                        : detail));

                DeletionRestoreSummary = $"{(dryRun ? "Dry-run" : "Restore")} result: " +
                                         $"{result.RegistryRestored} registry, " +
                                         $"{result.FilesRecoverable} recoverable file path(s), " +
                                         $"{result.Unrecoverable} unrecoverable.";
                StatusText = DeletionRestoreSummary;
            });
        }
        catch (Exception ex)
        {
            Log.Error("RestoreDeletionManifest", ex);
            StatusText = $"Deletion manifest restore failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDeletionManifestFolder()
        => OpenFolder(DataPaths.Logs);

    [RelayCommand]
    private void OpenDeletionBackupFolder()
        => OpenBackupFolder();

    private void SetDeletionRestoreCounts(RestoreResult result)
    {
        DeletionRegistryRestored = result.RegistryRestored;
        DeletionFilesRecoverable = result.FilesRecoverable;
        DeletionUnrecoverable = result.Unrecoverable;
    }

    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WindowsExecutableResolver.ResolveSystemHelper("explorer.exe"),
                Arguments = path,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
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
            var redact = AppSettings.Current.ScrubSensitivePathsInReports;
            var entries = ActivityLog.LoadRecent(200)
                .Select(e => redact ? e with { Summary = PrivacyRedactor.RedactPaths(e.Summary) } : e)
                .ToList();
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

    [ObservableProperty] public partial string HealthTrendDisplay { get; set; } = "";

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

                var trendIcon = report.Trend switch
                {
                    HealthTrend.Improved => "↑",
                    HealthTrend.Worsened => "↓",
                    HealthTrend.Stable => "→",
                    _ => "",
                };
                var trendText = report.Trend switch
                {
                    HealthTrend.Improved => "improved",
                    HealthTrend.Worsened => "worsened",
                    HealthTrend.Stable => "stable",
                    _ => "",
                };

                HealthTrendDisplay = report.Trend != HealthTrend.Unknown
                    ? $"{trendIcon} {trendText} since last check"
                    : "";
                HealthSummary = $"Overall: {report.Grade} ({report.OverallScore}/100)" +
                               (string.IsNullOrEmpty(HealthTrendDisplay) ? "" : $" — {HealthTrendDisplay}") +
                               $" — {report.StatusDisplay}";
                StatusText = HealthSummary +
                    (report.FailedSources is { Count: > 0 }
                        ? $"; sources: {string.Join(", ", report.FailedSources.Select(issue => issue.Source).Take(3))}"
                        : report.Warnings is { Count: > 0 }
                            ? $"; {report.Warnings[0]}"
                            : "");
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
        if (!ExpertMode)
        {
            StatusText = "Enable Expert mode in Settings / Privacy to remove Windows components.";
            return;
        }

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
    [ObservableProperty] public partial string ExecutablePathDisplay { get; set; } = "Open About to inspect the running executable.";
    [ObservableProperty] public partial string LocalSignatureDisplay { get; set; } = "Not inspected yet.";
    [ObservableProperty] public partial string LocalSha256Display { get; set; } = "Not calculated yet.";

    public string ReleaseVerificationText =>
        "Before trusting a downloaded build, compare this SHA256 with the matching release checksum and confirm the signing status is expected.";

    public void RefreshAboutTrustFacts()
    {
        var exePath = ResolveCurrentExecutablePath();
        ExecutablePathDisplay = string.IsNullOrWhiteSpace(exePath)
            ? "Unavailable - executable path could not be resolved."
            : exePath;

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            LocalSignatureDisplay = "Unavailable - executable file was not found.";
            LocalSha256Display = "Unavailable - executable file was not found.";
            StatusText = "Executable trust facts are unavailable.";
            return;
        }

        try
        {
            var signature = DigitalSignatureInspector.Inspect(exePath);
            LocalSignatureDisplay = FormatSignatureStatus(signature);
        }
        catch (Exception ex)
        {
            Log.Warn($"Signature inspection failed for {exePath}: {ex.Message}");
            LocalSignatureDisplay = "Unavailable - signature inspection failed.";
        }

        try
        {
            LocalSha256Display = ComputeSha256(exePath);
        }
        catch (Exception ex)
        {
            Log.Warn($"SHA256 calculation failed for {exePath}: {ex.Message}");
            LocalSha256Display = "Unavailable - SHA256 calculation failed.";
        }

        StatusText = "Executable trust facts refreshed.";
    }

    private static string ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
            return Environment.ProcessPath;

        try
        {
            var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(modulePath) && File.Exists(modulePath))
                return modulePath;
        }
        catch
        {
            // Best-effort only; About still renders an actionable unavailable state.
        }

        return "";
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FormatSignatureStatus(SignatureInfo signature) => signature.Status switch
    {
        SignatureStatus.Signed => string.IsNullOrWhiteSpace(signature.Subject)
            ? "Signed."
            : $"Signed by {signature.Subject}.",
        SignatureStatus.Unsigned => "Unsigned. Expected for local development builds; verify release hashes before installing.",
        SignatureStatus.Invalid => "Invalid signature. Do not trust this executable until the source is verified.",
        SignatureStatus.ChainInvalid => "Signed, but the certificate chain is not trusted.",
        SignatureStatus.Revoked => "Revoked signature. Do not trust this executable.",
        SignatureStatus.Missing => "Missing executable. Signature could not be inspected.",
        _ => "Unknown signature status.",
    };

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
            var fwTask = Task.Run(() => FirewallRuleScanner.GetAllRulesDetailed(orphanedOnly: true));
            var pathTask = Task.Run(() => PathCleaner.ScanPathEntriesDetailed(orphanedOnly: true));
            var svcTask = Task.Run(() => Core.Services.ServiceScanner.GetAllServicesDetailed(orphanedOnly: true));
            var taskTask = Task.Run(() => Core.Tasks.ScheduledTaskScanner.GetAllTasksDetailed());

            await Task.WhenAll(fwTask, pathTask, svcTask, taskTask);

            var fwScan = fwTask.Result;
            var pathScan = pathTask.Result;
            var serviceScan = svcTask.Result;
            var taskScan = taskTask.Result;
            var fwRules = fwScan.Items.ToList();
            var pathEntries = pathScan.Items.ToList();
            var orphanedSvcs = serviceScan.Items.ToList();
            var orphanedTasks = taskScan.Items.Where(t => t.IsOrphaned).ToList();

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
                var degraded = new[]
                {
                    ScanConfidenceSuffix(fwScan),
                    ScanConfidenceSuffix(pathScan),
                    ScanConfidenceSuffix(serviceScan),
                    ScanConfidenceSuffix(taskScan),
                }.FirstOrDefault(suffix => !string.IsNullOrEmpty(suffix)) ?? "";
                StatusText = $"Orphan scan: {total} orphaned artifacts — {OrphanSummary}{degraded}";
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
            var results = FirewallRuleScanner.DeleteRulesDetailed(selected);
            for (var i = 0; i < selected.Count && i < results.Count; i++)
            {
                if (results[i].Succeeded)
                    OrphanedFirewallRules.Remove(selected[i]);
            }
            var changed = results.Count(result => result.Succeeded);
            var reviewed = results.Count(result => result.IsReviewOnly || result.Outcome == AdministrativeMutationOutcome.Failed);
            ActivityLog.Record("orphans", $"Deleted {changed} orphaned firewall rule(s); {reviewed} not changed", itemCount: changed);
            StatusText = $"Deleted {changed} orphaned firewall rule(s); {reviewed} not changed.";
        }
        catch (Exception ex) { Log.Error("DeleteOrphanedFirewallRules", ex); StatusText = $"Delete failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void RemoveOrphanedPathEntries()
    {
        try
        {
            var selected = OrphanedPathEntries.Where(p => p.IsSelected).ToList();
            var results = PathCleaner.RemoveOrphanedEntriesDetailed(selected);
            var changedScopes = results
                .Where(result => result.Succeeded)
                .Select(result => result.Target.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
                    ? "System"
                    : "User")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in selected.Where(entry => changedScopes.Contains(entry.Source)))
                OrphanedPathEntries.Remove(entry);
            var changed = results.Where(result => result.Succeeded).Sum(result => result.ItemsAffected);
            var reviewed = results.Count(result => result.IsReviewOnly || result.Outcome == AdministrativeMutationOutcome.Failed);
            ActivityLog.Record("orphans", $"Removed {changed} orphaned PATH entr(ies); {reviewed} not changed", itemCount: changed);
            StatusText = $"Removed {changed} orphaned PATH entr(ies); {reviewed} not changed.";
        }
        catch (Exception ex) { Log.Error("RemoveOrphanedPathEntries", ex); StatusText = $"Remove failed: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════════════════
    //  UPDATE CHECK
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] public partial string UpdateText { get; set; } = "Not checked yet.";

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
    //  RELEASE CHECKSUM VERIFICATION
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] public partial string ChecksumVerifyDisplay { get; set; } = "Not verified yet.";

    [RelayCommand]
    private async Task VerifyReleaseChecksumAsync()
    {
        ChecksumVerifyDisplay = "Verifying against latest release...";
        StatusText = ChecksumVerifyDisplay;
        try
        {
            var exePath = ResolveCurrentExecutablePath();
            var result = await new ReleaseChecksumVerifier().VerifyAsync(
                string.IsNullOrWhiteSpace(exePath) ? null : exePath);
            _dispatcher.Invoke(() =>
            {
                ChecksumVerifyDisplay = result.Status switch
                {
                    ChecksumVerifyStatus.Match =>
                        $"MATCH — release {result.ReleaseTag}, asset {result.AssetName}.\nLocal: {result.LocalHash}\nRemote: {result.RemoteHash}",
                    ChecksumVerifyStatus.Mismatch =>
                        $"MISMATCH — release {result.ReleaseTag}, asset {result.AssetName}.\nLocal: {result.LocalHash}\nRemote: {result.RemoteHash}\nThe running executable does not match the published checksum.",
                    _ => result.StatusDisplay,
                };
                StatusText = $"Checksum: {result.StatusDisplay}";
            });
        }
        catch (Exception ex)
        {
            Log.Error("VerifyReleaseChecksumAsync", ex);
            ChecksumVerifyDisplay = $"Verification failed: {ex.Message}";
            StatusText = ChecksumVerifyDisplay;
        }
    }

    //  SUPPORT BUNDLE
    // ═══════════════════════════════════════════════════════
    [ObservableProperty] public partial string SupportBundleStatus { get; set; } = "";

    [RelayCommand]
    private async Task ExportSupportBundleAsync()
    {
        IsBusy = true;
        StatusText = "Collecting diagnostic data for support bundle...";
        try
        {
            var outputDir = DataPaths.Logs;
            var fileName = $"deeppurge-support-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            var outputPath = Path.Combine(outputDir, fileName);

            var result = await Task.Run(() => SupportBundleExporter.Export(outputPath));
            _dispatcher.Invoke(() =>
            {
                if (result.Success)
                {
                    SupportBundleStatus = $"Bundle saved: {Path.GetFileName(result.OutputPath)} ({FormatBytes(result.ByteCount)}, {result.SectionCount} sections)";
                    StatusText = SupportBundleStatus;
                    OpenFolder(Path.GetDirectoryName(result.OutputPath)!);
                }
                else
                {
                    SupportBundleStatus = $"Bundle export failed: {result.ErrorMessage}";
                    StatusText = SupportBundleStatus;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("ExportSupportBundleAsync", ex);
            SupportBundleStatus = $"Bundle export failed: {ex.Message}";
            StatusText = SupportBundleStatus;
        }
        finally { IsBusy = false; }
    }

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
        sb.AppendLine("Published\tOriginal\tProvider\tVersion\tSize\tOld\tSafety\tRollback");
        foreach (var d in DriverPackages)
            sb.AppendLine($"{d.PublishedName}\t{d.OriginalName}\t{d.ProviderName}\t{d.DriverVersion}\t{FormatBytes(d.SizeBytes)}\t{d.IsOldVersion}\t{d.SafetyStatus}\t{d.RollbackStatus}");
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
