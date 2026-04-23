using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeepPurge.Core.Browsers;
using DeepPurge.Core.Export;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Icons;
using DeepPurge.Core.Models;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Privacy;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Services;
using DeepPurge.Core.Shell;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Tasks;
using DeepPurge.Core.Uninstall;

namespace DeepPurge.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly UninstallEngine _engine = new();
    private readonly Dispatcher _dispatcher;

    /// <summary>Cancellation for long-running operations (initial scan, uninstall).</summary>
    private CancellationTokenSource? _cts;

    /// <summary>Separate cancellation for the fire-and-forget icon backfill.</summary>
    private CancellationTokenSource? _iconCts;

    // ═══════════════════════════════════════════════════════
    //  OBSERVABLE STATE
    // ═══════════════════════════════════════════════════════

    [ObservableProperty] private bool _isInitialScanRunning = true;
    [ObservableProperty] private string _scanOverlayText = "DeepPurge is analyzing your system...";
    [ObservableProperty] private double _overlayScanProgress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _programCountText = "0 programs";
    [ObservableProperty] private string _currentPanel = "Programs";

    // Status bar metrics
    [ObservableProperty] private string _totalJunkDisplay = "Junk: --";
    [ObservableProperty] private string _selectedCountBadge = "";

    // Per-nav scan badges
    [ObservableProperty] private string _programsBadge = "";
    [ObservableProperty] private string _junkBadge = "";
    [ObservableProperty] private string _tasksBadge = "";
    [ObservableProperty] private string _autorunBadge = "";
    [ObservableProperty] private string _browserExtBadge = "";
    [ObservableProperty] private string _windowsAppsBadge = "";
    [ObservableProperty] private string _contextMenuBadge = "";
    [ObservableProperty] private string _servicesBadge = "";
    [ObservableProperty] private string _evidenceBadge = "";
    [ObservableProperty] private string _emptyFoldersBadge = "";

    // Scan progress per category (0-100)
    [ObservableProperty] private double _programsScanProgress;
    [ObservableProperty] private double _junkScanProgress;
    [ObservableProperty] private double _tasksScanProgress;

    // Destructive-action options. Bound to status-bar toggles so the user
    // can flip dry-run or secure-delete before any cleanup call.
    [ObservableProperty] private bool _dryRunEnabled;
    [ObservableProperty] private bool _secureDeleteEnabled;

    // Live progress bar for the current long-running delete.
    [ObservableProperty] private double _operationProgress;
    [ObservableProperty] private string _operationProgressText = "";
    [ObservableProperty] private bool _operationProgressVisible;

    // ═══════════════════════════════════════════════════════
    //  DATA COLLECTIONS
    // ═══════════════════════════════════════════════════════

    public ObservableCollection<InstalledProgram> Programs { get; } = new();
    public ObservableCollection<InstalledProgram> FilteredPrograms { get; } = new();
    public ObservableCollection<JunkCategory> JunkCategories { get; } = new();
    public ObservableCollection<ScheduledTaskInfo> ScheduledTasks { get; } = new();
    public ObservableCollection<AutorunEntry> Autoruns { get; } = new();
    public ObservableCollection<BrowserExtension> BrowserExtensions { get; } = new();
    public ObservableCollection<WindowsApp> WindowsApps { get; } = new();
    public ObservableCollection<TraceCategory> TraceCategories { get; } = new();
    public ObservableCollection<EmptyFolderInfo> EmptyFolders { get; } = new();
    public ObservableCollection<ContextMenuEntry> ContextMenuEntries { get; } = new();
    public ObservableCollection<ServiceEntry> Services { get; } = new();
    public ObservableCollection<RestorePointInfo> RestorePoints { get; } = new();
    public ObservableCollection<DiskFolderInfo> DiskFolders { get; } = new();
    public ObservableCollection<LargeFileInfo> LargeFiles { get; } = new();

    // Leftover overlay
    public ObservableCollection<LeftoverItem> RegistryLeftovers { get; } = new();
    public ObservableCollection<LeftoverItem> FileLeftovers { get; } = new();

    [ObservableProperty] private ScanResult? _currentScanResult;
    [ObservableProperty] private string _leftoverTitle = "";
    [ObservableProperty] private string _leftoverInfo = "";
    [ObservableProperty] private string _leftoverStats = "";
    [ObservableProperty] private bool _showLeftovers;

    private readonly HashSet<string> _loadedPanels = new();
    private string _searchFilter = "";
    private long _totalJunkBytes;

    /// <summary>Exposed for the view to reuse the same engine instance.</summary>
    public UninstallEngine Engine => _engine;

    // ═══════════════════════════════════════════════════════
    //  CONSTRUCTOR
    // ═══════════════════════════════════════════════════════

    public MainViewModel()
    {
        // Use Application.Current.Dispatcher so this VM works even if constructed
        // off the UI thread (e.g. in a designer/test harness). Falls back to the
        // current dispatcher as a last resort.
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _engine.StatusChanged += s => _dispatcher.BeginInvoke(() => StatusText = s);
        _engine.ProgressChanged += p => _dispatcher.BeginInvoke(() => OverlayScanProgress = p);
    }

    // ═══════════════════════════════════════════════════════
    //  INITIAL SCAN
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Allocate a fresh cancellation source for a new long-running operation,
    /// disposing any previous one first. Prevents leaking CTS instances when
    /// the user rapidly starts new operations, and prevents accidentally
    /// re-using a cancelled CTS (which would immediately abort the new op).
    /// </summary>
    private CancellationToken RenewCts()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        try { old?.Cancel(); old?.Dispose(); } catch { /* disposing a cancelled CTS can't throw but guard anyway */ }
        return _cts!.Token;
    }

    public async Task RunInitialScanAsync()
    {
        var ct = RenewCts();

        IsInitialScanRunning = true;
        ScanOverlayText = "DeepPurge is analyzing your system...";
        OverlayScanProgress = 0;

        // 11 phases: programs + 10 parallel scanners. Each tick moves the
        // overlay progress a fixed fraction so the user sees real movement
        // instead of a perpetually-spinning circle.
        const int totalSteps = 11;
        int completedSteps = 0;
        void Tick(string? stepLabel = null)
        {
            var done = Interlocked.Increment(ref completedSteps);
            _dispatcher.BeginInvoke(() =>
            {
                OverlayScanProgress = 100.0 * done / totalSteps;
                if (!string.IsNullOrEmpty(stepLabel)) ScanOverlayText = stepLabel;
            });
        }

        try
        {
            ScanOverlayText = "Scanning installed programs...";
            ProgramsScanProgress = 0;
            var programs = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms(), ct);
            ct.ThrowIfCancellationRequested();

            _dispatcher.Invoke(() =>
            {
                Programs.Clear(); FilteredPrograms.Clear();
                foreach (var p in programs) { Programs.Add(p); FilteredPrograms.Add(p); }
                ProgramCountText = $"{programs.Count} programs";
                ProgramsBadge = programs.Count.ToString();
                ProgramsScanProgress = 100;
                _loadedPanels.Add("Programs");
            });
            Tick($"Loaded {programs.Count} programs — scanning the rest...");

            StartIconBackfill(programs);
            StartPackageEnrichment(programs);

            // Each parallel task calls Tick() on completion so the progress
            // bar advances as individual scanners finish, not just at the end.
            var junkTask = Task.Run(() =>
            {
                _dispatcher.BeginInvoke(() => JunkScanProgress = 10);
                var result = JunkFilesCleaner.ScanForJunk();
                _dispatcher.BeginInvoke(() => JunkScanProgress = 100);
                Tick("Junk categories discovered...");
                return result;
            }, ct);

            var tasksTask = Task.Run(() =>
            {
                _dispatcher.BeginInvoke(() => TasksScanProgress = 10);
                var result = ScheduledTaskScanner.GetAllTasks();
                _dispatcher.BeginInvoke(() => TasksScanProgress = 100);
                Tick("Scheduled tasks enumerated...");
                return result;
            }, ct);

            var autorunTask = Task.Run(() => { var r = AutorunScanner.GetAllAutoruns(); Tick("Autoruns + signatures checked..."); return r; }, ct);
            var extTask     = Task.Run(() => { var r = BrowserExtensionScanner.GetAllExtensions(); Tick("Browser extensions scanned..."); return r; }, ct);
            var appsTask    = WindowsAppManager.GetInstalledAppsAsync().ContinueWith(t => { Tick("Windows Apps enumerated..."); return t.Result; }, ct);
            var evidenceTask= Task.Run(() => { var r = EvidenceRemover.ScanAllTraces(); Tick("Privacy traces scanned..."); return r; }, ct);
            var emptyTask   = Task.Run(() => { var r = EmptyFolderScanner.ScanCommonLocations(); Tick("Empty folders scanned..."); return r; }, ct);
            var ctxTask     = Task.Run(() => { var r = ContextMenuCleaner.ScanContextMenuEntries(); Tick("Shell context menu scanned..."); return r; }, ct);
            var svcTask     = Task.Run(() => { var r = ServiceScanner.GetAllServices(); Tick("Services + signatures checked..."); return r; }, ct);
            var restoreTask = Task.Run(() => { var r = SystemRestoreManager.GetRestorePoints(); Tick("Restore points loaded..."); return r; }, ct);

            await Task.WhenAll(junkTask, tasksTask, autorunTask, extTask, appsTask,
                               evidenceTask, emptyTask, ctxTask, svcTask, restoreTask);

            _dispatcher.Invoke(() =>
            {
                ApplyJunkResults(junkTask.Result);
                ApplyTaskResults(tasksTask.Result);
                ApplyAutorunResults(autorunTask.Result);
                ApplyExtensionResults(extTask.Result);
                ApplyWindowsAppResults(appsTask.Result);
                ApplyEvidenceResults(evidenceTask.Result);
                ApplyEmptyFolderResults(emptyTask.Result);
                ApplyContextMenuResults(ctxTask.Result);
                ApplyServiceResults(svcTask.Result);
                ApplyRestoreResults(restoreTask.Result);
                OverlayScanProgress = 100;
            });

            ScanOverlayText = "Scan complete!";
            StatusText = $"Loaded {programs.Count} programs | {FormatSize(_totalJunkBytes)} junk | All panels ready";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Initial scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan error: {ex.Message}";
        }
        finally
        {
            await Task.Delay(400);
            IsInitialScanRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  LAZY PANEL LOADING (fallback when initial scan skipped)
    // ═══════════════════════════════════════════════════════

    public async Task EnsurePanelLoadedAsync(string panel)
    {
        if (_loadedPanels.Contains(panel)) return;
        _loadedPanels.Add(panel);

        switch (panel)
        {
            case "Autorun":
                IsBusy = true; StatusText = "Loading autorun entries...";
                var entries = await Task.Run(() => AutorunScanner.GetAllAutoruns());
                _dispatcher.Invoke(() => ApplyAutorunResults(entries));
                StatusText = $"Loaded {entries.Count} autorun entries";
                IsBusy = false;
                break;

            case "BrowserExt":
                IsBusy = true; StatusText = "Scanning browser extensions...";
                var exts = await Task.Run(() => BrowserExtensionScanner.GetAllExtensions());
                _dispatcher.Invoke(() => ApplyExtensionResults(exts));
                StatusText = $"Found {exts.Count} browser extensions";
                IsBusy = false;
                break;

            case "WindowsApps":
                IsBusy = true; StatusText = "Loading Windows apps...";
                var apps = await WindowsAppManager.GetInstalledAppsAsync();
                _dispatcher.Invoke(() => ApplyWindowsAppResults(apps));
                StatusText = $"Found {apps.Count} Windows apps";
                IsBusy = false;
                break;

            case "Restore":
                IsBusy = true; StatusText = "Loading restore points...";
                var pts = await Task.Run(() => SystemRestoreManager.GetRestorePoints());
                _dispatcher.Invoke(() => ApplyRestoreResults(pts));
                StatusText = $"Found {pts.Count} restore points";
                IsBusy = false;
                break;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  SEARCH / FILTER
    // ═══════════════════════════════════════════════════════

    public void ApplyFilter(string filter)
    {
        _searchFilter = filter ?? "";
        FilteredPrograms.Clear();

        if (string.IsNullOrWhiteSpace(_searchFilter))
        {
            foreach (var p in Programs) FilteredPrograms.Add(p);
            return;
        }

        foreach (var p in Programs)
        {
            if (p.DisplayName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                p.Publisher.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredPrograms.Add(p);
            }
        }
    }

    public string SearchFilter
    {
        get => _searchFilter;
        set { _searchFilter = value ?? ""; ApplyFilter(_searchFilter); }
    }

    // ═══════════════════════════════════════════════════════
    //  SCAN ACTIONS
    // ═══════════════════════════════════════════════════════

    public async Task ScanJunkAsync()
    {
        IsBusy = true; StatusText = "Scanning for junk files...";
        try
        {
            var cats = await Task.Run(JunkFilesCleaner.ScanForJunk);
            ApplyJunkResults(cats);
            StatusText = $"Found {cats.Sum(c => c.Files.Count)} junk items, {FormatSize(_totalJunkBytes)}";
        }
        finally { IsBusy = false; }
    }

    public async Task CleanJunkAsync(IEnumerable<JunkCategory> selected)
    {
        var list = selected.ToList();
        IsBusy = true;
        ShowOperationProgress(DryRunEnabled ? "Previewing junk cleanup..." : "Cleaning junk files...");

        var progress = new Progress<DeleteProgress>(p => UpdateOperationProgress(p,
            verb: DryRunEnabled ? "Would clean" : "Cleaning"));

        try
        {
            var options = CurrentDeleteOptions();
            var ct = RenewCts();
            var summary = await Task.Run(() =>
                JunkFilesCleaner.DeleteJunkSafe(list, options, progress, ct), ct);

            var verb = summary.DryRun ? "Would free" : "Freed";
            var skip = summary.ItemsSkipped > 0 ? $", {summary.ItemsSkipped} skipped" : "";
            StatusText = $"{verb} {FormatSize(summary.BytesFreed)} of disk space ({summary.ItemsDeleted} items{skip})";
            if (!summary.DryRun) await ScanJunkAsync();
        }
        finally
        {
            IsBusy = false;
            HideOperationProgress();
        }
    }

    public async Task ScanEvidenceAsync()
    {
        IsBusy = true; StatusText = "Scanning for traces...";
        try
        {
            var cats = await Task.Run(EvidenceRemover.ScanAllTraces);
            ApplyEvidenceResults(cats);
            StatusText = $"Found {cats.Sum(c => c.ItemCount)} trace items across {cats.Count} categories";
        }
        finally { IsBusy = false; }
    }

    public async Task CleanEvidenceAsync(IEnumerable<TraceCategory> selected)
    {
        var list = selected.ToList();
        IsBusy = true;
        ShowOperationProgress(DryRunEnabled ? "Previewing trace cleanup..." : "Cleaning traces...");

        var progress = new Progress<DeleteProgress>(p => UpdateOperationProgress(p,
            verb: DryRunEnabled ? "Would clean" : "Cleaning"));

        try
        {
            var options = CurrentDeleteOptions();
            var ct = RenewCts();
            var summary = await Task.Run(() =>
                EvidenceRemover.CleanTracesSafe(list, options, progress, ct), ct);

            var verb = summary.DryRun ? "Would clean" : "Cleaned";
            StatusText = $"{verb} {summary.ItemsDeleted} trace items, {FormatSize(summary.BytesFreed)}";
            if (!summary.DryRun) await ScanEvidenceAsync();
        }
        finally
        {
            IsBusy = false;
            HideOperationProgress();
        }
    }

    public async Task ScanEmptyFoldersAsync()
    {
        IsBusy = true; StatusText = "Scanning for empty folders...";
        try
        {
            var folders = await Task.Run(EmptyFolderScanner.ScanCommonLocations);
            ApplyEmptyFolderResults(folders);
            StatusText = $"Found {folders.Count} empty folders";
        }
        finally { IsBusy = false; }
    }

    public async Task DeleteEmptyFoldersAsync(IEnumerable<EmptyFolderInfo> selected)
    {
        var list = selected.ToList();
        IsBusy = true; StatusText = "Deleting empty folders...";
        try
        {
            var deleted = await Task.Run(() => EmptyFolderScanner.DeleteEmptyFolders(list));
            foreach (var f in list) EmptyFolders.Remove(f);
            EmptyFoldersBadge = EmptyFolders.Count > 0 ? EmptyFolders.Count.ToString() : "";
            StatusText = $"Deleted {deleted} empty folders";
        }
        finally { IsBusy = false; }
    }

    public async Task ScanDiskAsync()
    {
        IsBusy = true;
        StatusText = "Analyzing disk space (WizTree-style MFT scan)...";

        var sw = Stopwatch.StartNew();
        try
        {
            // FastDiskAnalyzer tries a raw NTFS MFT scan first (single sequential
            // read per volume) and falls back to FindFirstFileExW with the
            // large-fetch flag — both substantially faster than the old
            // Directory.EnumerateFiles path.
            var folders = await Task.Run(() => FastDiskAnalyzer.AnalyzeDrive(@"C:\"));
            var large = await Task.Run(() => FastDiskAnalyzer.FindLargeFiles(@"C:\", 50 * 1024 * 1024, 200));

            DiskFolders.Clear();
            foreach (var f in folders) DiskFolders.Add(f);

            LargeFiles.Clear();
            foreach (var f in large) LargeFiles.Add(f);

            sw.Stop();
            StatusText = $"Found {folders.Count} top-level folders, {large.Count} large files " +
                         $"({FormatSize(large.Sum(f => f.SizeBytes))}) in {sw.Elapsed.TotalSeconds:F1}s";
        }
        finally { IsBusy = false; }
    }

    public async Task ScanContextMenuAsync()
    {
        IsBusy = true; StatusText = "Scanning context menu entries...";
        try
        {
            var entries = await Task.Run(ContextMenuCleaner.ScanContextMenuEntries);
            ApplyContextMenuResults(entries);
            var orphaned = entries.Count(e => e.IsOrphaned);
            StatusText = $"Found {entries.Count} entries, {orphaned} orphaned";
        }
        finally { IsBusy = false; }
    }

    public async Task ScanServicesAsync()
    {
        IsBusy = true; StatusText = "Scanning Windows services...";
        try
        {
            var services = await Task.Run(() => ServiceScanner.GetAllServices());
            ApplyServiceResults(services);
            var orphaned = services.Count(s => s.IsOrphaned);
            StatusText = $"Found {services.Count} services, {orphaned} orphaned";
        }
        finally { IsBusy = false; }
    }

    public async Task ScanTasksAsync()
    {
        IsBusy = true; StatusText = "Scanning scheduled tasks...";
        try
        {
            var tasks = await Task.Run(ScheduledTaskScanner.GetAllTasks);
            ApplyTaskResults(tasks);
            var orphaned = tasks.Count(t => t.IsOrphaned);
            StatusText = $"Found {tasks.Count} tasks, {orphaned} orphaned";
        }
        finally { IsBusy = false; }
    }

    // ═══════════════════════════════════════════════════════
    //  UNINSTALL / LEFTOVERS
    // ═══════════════════════════════════════════════════════

    public async Task<UninstallResult?> UninstallAsync(InstalledProgram program, ScanMode mode)
    {
        var ct = RenewCts();
        IsBusy = true; StatusText = $"Uninstalling {program.DisplayName}...";
        try
        {
            return await _engine.UninstallAsync(
                program, mode, createRestorePoint: true, runBuiltInUninstaller: true, ct: ct);
        }
        finally { IsBusy = false; }
    }

    public async Task<ScanResult> ScanLeftoversAsync(InstalledProgram program, ScanMode mode)
    {
        var ct = RenewCts();
        IsBusy = true; StatusText = $"Scanning leftovers for {program.DisplayName}...";
        try
        {
            var regScanner = new RegistryLeftoverScanner();
            var fileScanner = new FileLeftoverScanner();
            var regLeftovers = await Task.Run(() => regScanner.ScanForLeftovers(program, mode), ct);
            var fileLeftovers = await Task.Run(() => fileScanner.ScanForLeftovers(program, mode), ct);
            return new ScanResult
            {
                Program = program,
                RegistryLeftovers = regLeftovers,
                FileLeftovers = fileLeftovers,
                ScanTime = DateTime.Now,
                Mode = mode,
            };
        }
        finally { IsBusy = false; }
    }

    public async Task<ScanResult> ForcedScanAsync(string name, string? folder, ScanMode mode)
    {
        var ct = RenewCts();
        IsBusy = true; StatusText = $"Forced scan: {name}...";
        try { return await _engine.ForcedScanAsync(name, folder, mode, ct); }
        finally { IsBusy = false; }
    }

    public async Task<(int regDel, int fileDel)> DeleteLeftoversAsync()
    {
        if (CurrentScanResult == null) return (0, 0);

        IsBusy = true;
        ShowOperationProgress(DryRunEnabled ? "Previewing leftover deletion..." : "Deleting leftovers...");

        var progress = new Progress<DeleteProgress>(p => UpdateOperationProgress(p,
            verb: DryRunEnabled ? "Would delete" : "Deleting"));

        try
        {
            var options = CurrentDeleteOptions();
            var ct = RenewCts();

            var (summary, regDel, fileDel) = await _engine.DeleteLeftoversAsync(
                CurrentScanResult.RegistryLeftovers,
                CurrentScanResult.FileLeftovers,
                options,
                progress,
                ct);

            if (!summary.DryRun)
            {
                CurrentScanResult.RegistryLeftovers.RemoveAll(
                    i => i.IsSelected && i.Confidence != LeftoverConfidence.Risky);
                CurrentScanResult.FileLeftovers.RemoveAll(
                    i => i.IsSelected && i.Confidence != LeftoverConfidence.Risky);
                RefreshLeftoverCollections();
                UpdateLeftoverStats();
            }

            var verb = summary.DryRun ? "Would delete" : "Deleted";
            StatusText = $"{verb} {regDel} registry + {fileDel} file items ({FormatSize(summary.BytesFreed)})";
            return (regDel, fileDel);
        }
        finally
        {
            IsBusy = false;
            HideOperationProgress();
        }
    }

    public void ShowLeftoverResults(ScanResult result)
    {
        CurrentScanResult = result;
        RefreshLeftoverCollections();
        LeftoverTitle = $"Leftovers: {result.Program.DisplayName}";
        LeftoverInfo = $"Mode: {result.Mode}  |  {result.RegistryLeftovers.Count} registry + {result.FileLeftovers.Count} files";
        UpdateLeftoverStats();
        ShowLeftovers = true;
    }

    public void RefreshLeftoverCollections()
    {
        if (CurrentScanResult == null) return;
        RegistryLeftovers.Clear();
        foreach (var i in CurrentScanResult.RegistryLeftovers) RegistryLeftovers.Add(i);
        FileLeftovers.Clear();
        foreach (var i in CurrentScanResult.FileLeftovers) FileLeftovers.Add(i);
    }

    public void UpdateLeftoverStats()
    {
        if (CurrentScanResult == null) { LeftoverStats = ""; return; }
        var regSel = CurrentScanResult.RegistryLeftovers.Count(i => i.IsSelected);
        var fileSel = CurrentScanResult.FileLeftovers.Count(i => i.IsSelected);
        var sizeBytes = CurrentScanResult.FileLeftovers.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        var sizeStr = sizeBytes > 0 ? $" ({FormatSize(sizeBytes)})" : "";
        LeftoverStats = $"{regSel} registry + {fileSel} files selected{sizeStr}";
    }

    public void SelectAllSafe()
    {
        if (CurrentScanResult == null) return;
        foreach (var i in CurrentScanResult.RegistryLeftovers)
            i.IsSelected = i.Confidence == LeftoverConfidence.Safe;
        foreach (var i in CurrentScanResult.FileLeftovers)
            i.IsSelected = i.Confidence == LeftoverConfidence.Safe;
        UpdateLeftoverStats();
    }

    public void DeselectAll()
    {
        if (CurrentScanResult == null) return;
        foreach (var i in CurrentScanResult.RegistryLeftovers) i.IsSelected = false;
        foreach (var i in CurrentScanResult.FileLeftovers) i.IsSelected = false;
        UpdateLeftoverStats();
    }

    // ═══════════════════════════════════════════════════════
    //  REFRESH / RELOAD
    // ═══════════════════════════════════════════════════════

    public async Task RefreshAsync()
    {
        IconExtractor.ClearCache();
        _loadedPanels.Clear();
        IsBusy = true; StatusText = "Refreshing programs...";
        try
        {
            var programs = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms());
            Programs.Clear(); FilteredPrograms.Clear();
            foreach (var p in programs)
            {
                Programs.Add(p);
                if (string.IsNullOrWhiteSpace(_searchFilter) ||
                    p.DisplayName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                    p.Publisher.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredPrograms.Add(p);
                }
            }
            ProgramCountText = $"{programs.Count} programs";
            ProgramsBadge = programs.Count.ToString();
            _loadedPanels.Add("Programs");
            StatusText = $"Refreshed {programs.Count} programs";

            StartIconBackfill(programs);
            StartPackageEnrichment(programs);
        }
        finally { IsBusy = false; }
    }

    // ═══════════════════════════════════════════════════════
    //  BULK UNINSTALL (BCUninstaller-inspired)
    // ═══════════════════════════════════════════════════════

    public async Task<List<UninstallResult>?> UninstallSelectedAsync(ScanMode mode)
    {
        var selected = Programs.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return null;

        var ct = RenewCts();
        IsBusy = true;
        ShowOperationProgress($"Uninstalling {selected.Count} programs...");

        var progress = new Progress<DeleteProgress>(p =>
            UpdateOperationProgress(p, verb: "Uninstalling"));

        try
        {
            var results = await _engine.UninstallBatchAsync(
                selected, mode, createRestorePoint: true, progress, ct);

            // Remove successfully-uninstalled programs from the list.
            for (int i = 0; i < selected.Count; i++)
            {
                if (results[i].Success)
                {
                    Programs.Remove(selected[i]);
                    FilteredPrograms.Remove(selected[i]);
                }
                else
                {
                    selected[i].IsSelected = false;
                }
            }
            ProgramCountText = $"{Programs.Count} programs";
            ProgramsBadge = Programs.Count.ToString();

            var ok = results.Count(r => r.Success);
            StatusText = $"Uninstalled {ok}/{results.Count} programs";
            return results;
        }
        finally
        {
            IsBusy = false;
            HideOperationProgress();
            // CTS is now owned by RenewCts — next operation will dispose it.
        }
    }

    // ═══════════════════════════════════════════════════════
    //  PACKAGE-MANAGER ENRICHMENT (winget + scoop)
    // ═══════════════════════════════════════════════════════

    private CancellationTokenSource? _enrichCts;

    private void StartPackageEnrichment(IReadOnlyList<InstalledProgram> programs)
    {
        _enrichCts?.Cancel();
        _enrichCts?.Dispose();
        _enrichCts = new CancellationTokenSource();
        var ct = _enrichCts.Token;
        var list = programs.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await PackageManagerScanner.EnrichAsync(list, ct).ConfigureAwait(false);

                // Add any new synthetic scoop entries back to the VM lists.
                var existingNames = new HashSet<string>(Programs.Select(p => p.DisplayName),
                    StringComparer.OrdinalIgnoreCase);
                var newcomers = list.Where(p => !existingNames.Contains(p.DisplayName)).ToList();
                if (newcomers.Count > 0)
                {
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        foreach (var n in newcomers)
                        {
                            Programs.Add(n);
                            FilteredPrograms.Add(n);
                        }
                        ProgramCountText = $"{Programs.Count} programs";
                        ProgramsBadge = Programs.Count.ToString();
                    });
                }

                _ = _dispatcher.BeginInvoke(() =>
                {
                    var upgradeable = Programs.Count(p => !string.IsNullOrEmpty(p.UpgradeAvailable));
                    if (upgradeable > 0)
                        StatusText = $"{upgradeable} programs have winget upgrades available";
                });
            }
            catch { /* non-fatal */ }
        }, ct);
    }

    // ═══════════════════════════════════════════════════════
    //  REGISTRY HUNTER (Revo-style trace search)
    // ═══════════════════════════════════════════════════════

    public ObservableCollection<RegistryHit> RegistryHits { get; } = new();

    [ObservableProperty] private bool _hunterUseRegex;
    [ObservableProperty] private bool _hunterSearchKeys = true;
    [ObservableProperty] private bool _hunterSearchNames = true;
    [ObservableProperty] private bool _hunterSearchData = true;
    [ObservableProperty] private int _hunterLiveCount;

    public async Task<int> HuntRegistryAsync(string needle)
    {
        RegistryHits.Clear();
        HunterLiveCount = 0;

        if (string.IsNullOrWhiteSpace(needle) || needle.Length < 3)
        {
            StatusText = "Enter at least 3 characters to search the registry";
            return 0;
        }

        var scope = RegistrySearchScope.All;
        scope = (HunterSearchKeys ? scope : scope & ~RegistrySearchScope.Keys);
        scope = (HunterSearchNames ? scope : scope & ~RegistrySearchScope.Names);
        scope = (HunterSearchData ? scope : scope & ~RegistrySearchScope.Data);

        if (scope == 0)
        {
            StatusText = "Pick at least one search target (keys / names / data)";
            return 0;
        }

        var options = new RegistryHuntOptions(
            Scope: scope,
            UseRegex: HunterUseRegex,
            MaxHits: 500);

        var ct = RenewCts();
        IsBusy = true;
        StatusText = $"Searching registry for '{needle}'...";
        var sw = Stopwatch.StartNew();

        try
        {
            var progress = new Progress<int>(count =>
                _dispatcher.BeginInvoke(() => HunterLiveCount = count));

            var hits = await Task.Run(
                () => RegistryHunter.Search(needle, options, progress, ct),
                ct);

            foreach (var h in hits) RegistryHits.Add(h);
            HunterLiveCount = hits.Count;

            sw.Stop();
            StatusText = hits.Count >= options.MaxHits
                ? $"Registry hunter: {options.MaxHits}+ matches for '{needle}' (capped) in {sw.Elapsed.TotalSeconds:F1}s"
                : $"Registry hunter: {hits.Count} matches for '{needle}' in {sw.Elapsed.TotalSeconds:F1}s";
            return hits.Count;
        }
        finally
        {
            IsBusy = false;
            // CTS ownership moves to RenewCts — no manual dispose here.
        }
    }

    // ═══════════════════════════════════════════════════════
    //  DELETE-OPTIONS plumbing
    // ═══════════════════════════════════════════════════════

    public DeleteOptions CurrentDeleteOptions() => new(
        DryRun: DryRunEnabled,
        SecureDelete: SecureDeleteEnabled,
        UseRecycleBin: !SecureDeleteEnabled);

    private void ShowOperationProgress(string initialText)
    {
        OperationProgress = 0;
        OperationProgressText = initialText;
        OperationProgressVisible = true;
    }

    private void HideOperationProgress()
    {
        OperationProgressVisible = false;
        OperationProgress = 0;
        OperationProgressText = "";
    }

    private void UpdateOperationProgress(DeleteProgress p, string verb)
    {
        _dispatcher.BeginInvoke(() =>
        {
            OperationProgress = p.Percent;
            var short_ = string.IsNullOrEmpty(p.CurrentItem)
                ? ""
                : p.CurrentItem.Length > 60 ? "…" + p.CurrentItem[^60..] : p.CurrentItem;
            OperationProgressText = $"{verb} {p.ItemsProcessed}/{p.ItemsTotal} · {short_}";
        });
    }

    /// <summary>
    /// Remove a program from the visible lists without re-scanning the entire
    /// registry. Called by the view after a successful uninstall so the user
    /// sees the program disappear immediately instead of staring at a stale
    /// row. A full <see cref="RefreshAsync"/> is still available behind the
    /// Refresh button if the uninstaller lied about success.
    /// </summary>
    public void RemoveProgramFromList(InstalledProgram program)
    {
        Programs.Remove(program);
        FilteredPrograms.Remove(program);
        ProgramCountText = $"{Programs.Count} programs";
        ProgramsBadge = Programs.Count.ToString();
    }

    public void OpenBackupFolder()
    {
        var dir = new Core.Safety.BackupManager().BackupDirectory;
        try { Directory.CreateDirectory(dir); } catch { }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    // ═══════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════

    public void UpdateSelectedCount()
    {
        int count = CurrentPanel switch
        {
            "Programs"      => FilteredPrograms.Count(p => p.IsSelected),
            "Junk"          => JunkCategories.Count(c => c.IsSelected),
            "Evidence"      => TraceCategories.Count(c => c.IsSelected),
            "EmptyFolders"  => EmptyFolders.Count(f => f.IsSelected),
            "ContextMenu"   => ContextMenuEntries.Count(c => c.IsSelected),
            "Services"      => Services.Count(s => s.IsSelected),
            "Tasks"         => ScheduledTasks.Count(t => t.IsSelected),
            _ => 0,
        };
        SelectedCountBadge = count > 0 ? $"{count} selected" : "";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F1} MB";
        return $"{mb / 1024.0:F2} GB";
    }

    public void CancelOperation()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        try { _iconCts?.Cancel(); } catch { /* already disposed */ }
    }

    // ═══════════════════════════════════════════════════════
    //  INTERNAL: apply result sets
    // ═══════════════════════════════════════════════════════

    private void StartIconBackfill(IReadOnlyList<InstalledProgram> programs)
    {
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = new CancellationTokenSource();
        var ct = _iconCts.Token;

        _ = Task.Run(() =>
        {
            foreach (var prog in programs)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var icon = IconExtractor.GetProgramIcon(
                        prog.DisplayIconPath, prog.UninstallString, prog.InstallLocation);
                    if (icon != null)
                        _dispatcher.BeginInvoke(() => prog.Icon = icon);
                }
                catch { /* icon extraction should never crash the app */ }
            }
        }, ct);
    }

    private void ApplyJunkResults(List<JunkCategory> cats)
    {
        JunkCategories.Clear();
        foreach (var c in cats) JunkCategories.Add(c);
        _totalJunkBytes = cats.Sum(c => c.TotalSize);
        JunkBadge = FormatSize(_totalJunkBytes);
        TotalJunkDisplay = $"Junk: {FormatSize(_totalJunkBytes)}";
        _loadedPanels.Add("Junk");
    }

    private void ApplyTaskResults(List<ScheduledTaskInfo> tasks)
    {
        ScheduledTasks.Clear();
        foreach (var t in tasks) ScheduledTasks.Add(t);
        var orphaned = tasks.Count(t => t.IsOrphaned);
        TasksBadge = orphaned > 0 ? $"{orphaned} orphaned" : $"{tasks.Count}";
        _loadedPanels.Add("Tasks");
    }

    private void ApplyAutorunResults(List<AutorunEntry> autoruns)
    {
        Autoruns.Clear();
        foreach (var a in autoruns) Autoruns.Add(a);
        AutorunBadge = autoruns.Count.ToString();
        _loadedPanels.Add("Autorun");
    }

    private void ApplyExtensionResults(List<BrowserExtension> exts)
    {
        BrowserExtensions.Clear();
        foreach (var e in exts) BrowserExtensions.Add(e);
        BrowserExtBadge = exts.Count.ToString();
        _loadedPanels.Add("BrowserExt");
    }

    private void ApplyWindowsAppResults(List<WindowsApp> apps)
    {
        WindowsApps.Clear();
        foreach (var a in apps) WindowsApps.Add(a);
        WindowsAppsBadge = apps.Count.ToString();
        _loadedPanels.Add("WindowsApps");
    }

    private void ApplyEvidenceResults(List<TraceCategory> traces)
    {
        TraceCategories.Clear();
        foreach (var t in traces) TraceCategories.Add(t);
        var total = traces.Sum(t => t.ItemCount);
        EvidenceBadge = total > 0 ? total.ToString() : "";
        _loadedPanels.Add("Evidence");
    }

    private void ApplyEmptyFolderResults(List<EmptyFolderInfo> empty)
    {
        EmptyFolders.Clear();
        foreach (var f in empty) EmptyFolders.Add(f);
        EmptyFoldersBadge = empty.Count > 0 ? empty.Count.ToString() : "";
        _loadedPanels.Add("EmptyFolders");
    }

    private void ApplyContextMenuResults(List<ContextMenuEntry> ctx)
    {
        ContextMenuEntries.Clear();
        foreach (var c in ctx) ContextMenuEntries.Add(c);
        var orphaned = ctx.Count(c => c.IsOrphaned);
        ContextMenuBadge = orphaned > 0 ? $"{orphaned} orphaned" : $"{ctx.Count}";
        _loadedPanels.Add("ContextMenu");
    }

    private void ApplyServiceResults(List<ServiceEntry> svcs)
    {
        Services.Clear();
        foreach (var s in svcs) Services.Add(s);
        var orphaned = svcs.Count(s => s.IsOrphaned);
        ServicesBadge = orphaned > 0 ? $"{orphaned} orphaned" : $"{svcs.Count}";
        _loadedPanels.Add("Services");
    }

    private void ApplyRestoreResults(List<RestorePointInfo> pts)
    {
        RestorePoints.Clear();
        foreach (var p in pts) RestorePoints.Add(p);
        _loadedPanels.Add("Restore");
    }
}
