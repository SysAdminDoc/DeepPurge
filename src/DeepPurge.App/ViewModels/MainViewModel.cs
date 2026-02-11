using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPurge.Core.Models;
using DeepPurge.Core.Registry;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Uninstall;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Browsers;
using DeepPurge.Core.Icons;
using DeepPurge.Core.Privacy;
using DeepPurge.Core.Shell;
using DeepPurge.Core.Services;
using DeepPurge.Core.Tasks;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Export;

namespace DeepPurge.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly UninstallEngine _engine = new();
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

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

    // ═══════════════════════════════════════════════════════
    //  CONSTRUCTOR + AUTO-SCAN
    // ═══════════════════════════════════════════════════════

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _engine.StatusChanged += s => _dispatcher.BeginInvoke(() => StatusText = s);
        _engine.ProgressChanged += p => _dispatcher.BeginInvoke(() => OverlayScanProgress = p);
    }

    public async Task RunInitialScanAsync()
    {
        IsInitialScanRunning = true;
        ScanOverlayText = "DeepPurge is analyzing your system...";

        try
        {
            // ── Phase 1: Programs (most critical) ──
            ScanOverlayText = "Scanning installed programs...";
            ProgramsScanProgress = 0;
            var programs = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms());
            _dispatcher.Invoke(() =>
            {
                Programs.Clear(); FilteredPrograms.Clear();
                foreach (var p in programs) { Programs.Add(p); FilteredPrograms.Add(p); }
                ProgramCountText = $"{programs.Count} programs";
                ProgramsBadge = programs.Count.ToString();
                ProgramsScanProgress = 100;
                _loadedPanels.Add("Programs");
            });

            // Fire-and-forget icon loading
            _ = Task.Run(() =>
            {
                foreach (var prog in programs)
                {
                    var icon = IconExtractor.GetProgramIcon(prog.DisplayIconPath, prog.UninstallString, prog.InstallLocation);
                    if (icon != null) _dispatcher.BeginInvoke(() => prog.Icon = icon);
                }
            });

            // ── Phase 2: All panels in parallel ──
            ScanOverlayText = "Scanning junk, tasks, autorun, extensions...";

            var junkTask = Task.Run(() =>
            {
                _dispatcher.BeginInvoke(() => JunkScanProgress = 10);
                var result = JunkFilesCleaner.ScanForJunk();
                _dispatcher.BeginInvoke(() => JunkScanProgress = 100);
                return result;
            });

            var tasksTask = Task.Run(() =>
            {
                _dispatcher.BeginInvoke(() => TasksScanProgress = 10);
                var result = ScheduledTaskScanner.GetAllTasks();
                _dispatcher.BeginInvoke(() => TasksScanProgress = 100);
                return result;
            });

            var autorunTask = Task.Run(() => AutorunScanner.GetAllAutoruns());
            var extTask = Task.Run(() => BrowserExtensionScanner.GetAllExtensions());
            var appsTask = WindowsAppManager.GetInstalledAppsAsync();
            var evidenceTask = Task.Run(() => EvidenceRemover.ScanAllTraces());
            var emptyTask = Task.Run(() => EmptyFolderScanner.ScanCommonLocations());
            var ctxTask = Task.Run(() => ContextMenuCleaner.ScanContextMenuEntries());
            var svcTask = Task.Run(() => ServiceScanner.GetAllServices());
            var restoreTask = Task.Run(() => SystemRestoreManager.GetRestorePoints());

            await Task.WhenAll(junkTask, tasksTask, autorunTask, extTask, appsTask,
                               evidenceTask, emptyTask, ctxTask, svcTask, restoreTask);

            _dispatcher.Invoke(() =>
            {
                // Junk
                var junkCategories = junkTask.Result;
                JunkCategories.Clear();
                foreach (var c in junkCategories) JunkCategories.Add(c);
                _totalJunkBytes = junkCategories.Sum(c => c.TotalSize);
                JunkBadge = FormatSize(_totalJunkBytes);
                TotalJunkDisplay = $"Junk: {FormatSize(_totalJunkBytes)}";
                _loadedPanels.Add("Junk");

                // Tasks
                var tasks = tasksTask.Result;
                ScheduledTasks.Clear();
                foreach (var t in tasks) ScheduledTasks.Add(t);
                var orphanedTasks = tasks.Count(t => t.IsOrphaned);
                TasksBadge = orphanedTasks > 0 ? $"{orphanedTasks} orphaned" : $"{tasks.Count}";
                _loadedPanels.Add("Tasks");

                // Autorun
                var autoruns = autorunTask.Result;
                Autoruns.Clear();
                foreach (var a in autoruns) Autoruns.Add(a);
                AutorunBadge = autoruns.Count.ToString();
                _loadedPanels.Add("Autorun");

                // Browser Extensions
                var exts = extTask.Result;
                BrowserExtensions.Clear();
                foreach (var ex in exts) BrowserExtensions.Add(ex);
                BrowserExtBadge = exts.Count.ToString();
                _loadedPanels.Add("BrowserExt");

                // Windows Apps
                var apps = appsTask.Result;
                WindowsApps.Clear();
                foreach (var a in apps) WindowsApps.Add(a);
                WindowsAppsBadge = apps.Count.ToString();
                _loadedPanels.Add("WindowsApps");

                // Evidence
                var traces = evidenceTask.Result;
                TraceCategories.Clear();
                foreach (var t in traces) TraceCategories.Add(t);
                EvidenceBadge = traces.Sum(t => t.ItemCount) > 0 ? $"{traces.Sum(t => t.ItemCount)}" : "";
                _loadedPanels.Add("Evidence");

                // Empty Folders
                var empty = emptyTask.Result;
                EmptyFolders.Clear();
                foreach (var f in empty) EmptyFolders.Add(f);
                EmptyFoldersBadge = empty.Count > 0 ? empty.Count.ToString() : "";
                _loadedPanels.Add("EmptyFolders");

                // Context Menu
                var ctx = ctxTask.Result;
                ContextMenuEntries.Clear();
                foreach (var c in ctx) ContextMenuEntries.Add(c);
                var orphanedCtx = ctx.Count(c => c.IsOrphaned);
                ContextMenuBadge = orphanedCtx > 0 ? $"{orphanedCtx} orphaned" : $"{ctx.Count}";
                _loadedPanels.Add("ContextMenu");

                // Services
                var svcs = svcTask.Result;
                Services.Clear();
                foreach (var s in svcs) Services.Add(s);
                var orphanedSvc = svcs.Count(s => s.IsOrphaned);
                ServicesBadge = orphanedSvc > 0 ? $"{orphanedSvc} orphaned" : $"{svcs.Count}";
                _loadedPanels.Add("Services");

                // Restore Points
                var pts = restoreTask.Result;
                RestorePoints.Clear();
                foreach (var p in pts) RestorePoints.Add(p);
                _loadedPanels.Add("Restore");
            });

            ScanOverlayText = "Scan complete!";
            StatusText = $"Loaded {programs.Count} programs | {FormatSize(_totalJunkBytes)} junk | All panels ready";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan error: {ex.Message}";
        }
        finally
        {
            await Task.Delay(600);
            IsInitialScanRunning = false;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  LAZY PANEL LOADING (kept for fallback)
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
                _dispatcher.Invoke(() =>
                {
                    Autoruns.Clear(); foreach (var e in entries) Autoruns.Add(e);
                    AutorunBadge = entries.Count.ToString();
                    StatusText = $"Loaded {entries.Count} autorun entries";
                });
                IsBusy = false;
                break;
            case "BrowserExt":
                IsBusy = true; StatusText = "Scanning browser extensions...";
                var exts = await Task.Run(() => BrowserExtensionScanner.GetAllExtensions());
                _dispatcher.Invoke(() =>
                {
                    BrowserExtensions.Clear(); foreach (var e in exts) BrowserExtensions.Add(e);
                    BrowserExtBadge = exts.Count.ToString();
                    StatusText = $"Found {exts.Count} browser extensions";
                });
                IsBusy = false;
                break;
            case "WindowsApps":
                IsBusy = true; StatusText = "Loading Windows apps...";
                var apps = await WindowsAppManager.GetInstalledAppsAsync();
                _dispatcher.Invoke(() =>
                {
                    WindowsApps.Clear(); foreach (var a in apps) WindowsApps.Add(a);
                    WindowsAppsBadge = apps.Count.ToString();
                    StatusText = $"Found {apps.Count} Windows apps";
                });
                IsBusy = false;
                break;
            case "Restore":
                IsBusy = true; StatusText = "Loading restore points...";
                var pts = await Task.Run(() => SystemRestoreManager.GetRestorePoints());
                _dispatcher.Invoke(() =>
                {
                    RestorePoints.Clear(); foreach (var p in pts) RestorePoints.Add(p);
                    StatusText = $"Found {pts.Count} restore points";
                });
                IsBusy = false;
                break;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  SEARCH / FILTER
    // ═══════════════════════════════════════════════════════

    public void ApplyFilter(string filter)
    {
        _searchFilter = filter;
        FilteredPrograms.Clear();
        var source = string.IsNullOrWhiteSpace(filter) ? Programs :
            new ObservableCollection<InstalledProgram>(Programs.Where(p =>
                p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        foreach (var p in source) FilteredPrograms.Add(p);
    }

    // ═══════════════════════════════════════════════════════
    //  SCAN ACTIONS
    // ═══════════════════════════════════════════════════════

    public async Task ScanJunkAsync()
    {
        IsBusy = true; StatusText = "Scanning for junk files...";
        try
        {
            var cats = await Task.Run(() => JunkFilesCleaner.ScanForJunk());
            JunkCategories.Clear();
            foreach (var c in cats) JunkCategories.Add(c);
            _totalJunkBytes = cats.Sum(c => c.TotalSize);
            JunkBadge = FormatSize(_totalJunkBytes);
            TotalJunkDisplay = $"Junk: {FormatSize(_totalJunkBytes)}";
            StatusText = $"Found {cats.Sum(c => c.Files.Count)} junk items, {FormatSize(_totalJunkBytes)}";
        }
        finally { IsBusy = false; }
    }

    public async Task CleanJunkAsync(IEnumerable<JunkCategory> selected)
    {
        IsBusy = true; StatusText = "Cleaning junk files...";
        try
        {
            var freed = await Task.Run(() => JunkFilesCleaner.DeleteJunk(selected));
            StatusText = $"Freed {FormatSize(freed)} of disk space";
            await ScanJunkAsync();
        }
        finally { IsBusy = false; }
    }

    public async Task ScanEvidenceAsync()
    {
        IsBusy = true; StatusText = "Scanning for traces...";
        try
        {
            var cats = await Task.Run(() => EvidenceRemover.ScanAllTraces());
            TraceCategories.Clear();
            foreach (var c in cats) TraceCategories.Add(c);
            EvidenceBadge = cats.Sum(c => c.ItemCount) > 0 ? $"{cats.Sum(c => c.ItemCount)}" : "";
            StatusText = $"Found {cats.Sum(c => c.ItemCount)} trace items across {cats.Count} categories";
        }
        finally { IsBusy = false; }
    }

    public async Task CleanEvidenceAsync(IEnumerable<TraceCategory> selected)
    {
        IsBusy = true; StatusText = "Cleaning traces...";
        try
        {
            var freed = await Task.Run(() => EvidenceRemover.CleanTraces(selected));
            StatusText = $"Cleaned traces, freed {FormatSize(freed)}";
            await ScanEvidenceAsync();
        }
        finally { IsBusy = false; }
    }

    public async Task ScanEmptyFoldersAsync()
    {
        IsBusy = true; StatusText = "Scanning for empty folders...";
        try
        {
            var folders = await Task.Run(() => EmptyFolderScanner.ScanCommonLocations());
            EmptyFolders.Clear();
            foreach (var f in folders) EmptyFolders.Add(f);
            EmptyFoldersBadge = folders.Count > 0 ? folders.Count.ToString() : "";
            StatusText = $"Found {folders.Count} empty folders";
        }
        finally { IsBusy = false; }
    }

    public async Task DeleteEmptyFoldersAsync(IEnumerable<EmptyFolderInfo> selected)
    {
        IsBusy = true; StatusText = "Deleting empty folders...";
        try
        {
            var list = selected.ToList();
            var deleted = await Task.Run(() => EmptyFolderScanner.DeleteEmptyFolders(list));
            StatusText = $"Deleted {deleted} empty folders";
            foreach (var f in list) EmptyFolders.Remove(f);
            EmptyFoldersBadge = EmptyFolders.Count > 0 ? EmptyFolders.Count.ToString() : "";
        }
        finally { IsBusy = false; }
    }

    public async Task ScanDiskAsync()
    {
        IsBusy = true; StatusText = "Analyzing disk space...";
        try
        {
            var folders = await Task.Run(() => DiskSpaceAnalyzer.AnalyzeFolder(@"C:\", 1));
            DiskFolders.Clear();
            foreach (var f in folders) DiskFolders.Add(f);
            var large = await Task.Run(() => DiskSpaceAnalyzer.FindLargeFiles(@"C:\", 50 * 1024 * 1024, 200));
            LargeFiles.Clear();
            foreach (var f in large) LargeFiles.Add(f);
            StatusText = $"Found {folders.Count} top-level folders, {large.Count} large files ({FormatSize(large.Sum(f => f.SizeBytes))})";
        }
        finally { IsBusy = false; }
    }

    public async Task ScanContextMenuAsync()
    {
        IsBusy = true; StatusText = "Scanning context menu entries...";
        try
        {
            var entries = await Task.Run(() => ContextMenuCleaner.ScanContextMenuEntries());
            ContextMenuEntries.Clear();
            foreach (var e in entries) ContextMenuEntries.Add(e);
            var orphaned = entries.Count(e => e.IsOrphaned);
            ContextMenuBadge = orphaned > 0 ? $"{orphaned} orphaned" : $"{entries.Count}";
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
            Services.Clear();
            foreach (var s in services) Services.Add(s);
            var orphaned = services.Count(s => s.IsOrphaned);
            ServicesBadge = orphaned > 0 ? $"{orphaned} orphaned" : $"{services.Count}";
            StatusText = $"Found {services.Count} services, {orphaned} orphaned";
        }
        finally { IsBusy = false; }
    }

    public async Task ScanTasksAsync()
    {
        IsBusy = true; StatusText = "Scanning scheduled tasks...";
        try
        {
            var tasks = await Task.Run(() => ScheduledTaskScanner.GetAllTasks());
            ScheduledTasks.Clear();
            foreach (var t in tasks) ScheduledTasks.Add(t);
            var orphaned = tasks.Count(t => t.IsOrphaned);
            TasksBadge = orphaned > 0 ? $"{orphaned} orphaned" : $"{tasks.Count}";
            StatusText = $"Found {tasks.Count} tasks, {orphaned} orphaned";
        }
        finally { IsBusy = false; }
    }

    // ═══════════════════════════════════════════════════════
    //  UNINSTALL / LEFTOVERS
    // ═══════════════════════════════════════════════════════

    public async Task<UninstallResult?> UninstallAsync(InstalledProgram program, ScanMode mode)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true; StatusText = $"Uninstalling {program.DisplayName}...";
        try { return await _engine.UninstallAsync(program, mode, createRestorePoint: true, runBuiltInUninstaller: true, ct: _cts.Token); }
        finally { IsBusy = false; _cts = null; }
    }

    public async Task<ScanResult> ScanLeftoversAsync(InstalledProgram program, ScanMode mode)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true; StatusText = $"Scanning leftovers for {program.DisplayName}...";
        try
        {
            var regScanner = new RegistryLeftoverScanner();
            var fileScanner = new FileLeftoverScanner();
            var regLeftovers = await Task.Run(() => regScanner.ScanForLeftovers(program, mode), _cts.Token);
            var fileLeftovers = await Task.Run(() => fileScanner.ScanForLeftovers(program, mode), _cts.Token);
            return new ScanResult { Program = program, RegistryLeftovers = regLeftovers, FileLeftovers = fileLeftovers, ScanTime = DateTime.Now, Mode = mode };
        }
        finally { IsBusy = false; _cts = null; }
    }

    public async Task<ScanResult> ForcedScanAsync(string name, string? folder, ScanMode mode)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true; StatusText = $"Forced scan: {name}...";
        try { return await _engine.ForcedScanAsync(name, folder, mode, _cts.Token); }
        finally { IsBusy = false; _cts = null; }
    }

    public async Task<(int regDel, int fileDel)> DeleteLeftoversAsync()
    {
        if (CurrentScanResult == null) return (0, 0);
        IsBusy = true; StatusText = "Deleting leftovers...";
        try
        {
            var (regDel, fileDel) = await _engine.DeleteLeftoversAsync(CurrentScanResult.RegistryLeftovers, CurrentScanResult.FileLeftovers, true);
            CurrentScanResult.RegistryLeftovers.RemoveAll(i => i.IsSelected);
            CurrentScanResult.FileLeftovers.RemoveAll(i => i.IsSelected);
            RefreshLeftoverCollections();
            StatusText = $"Deleted {regDel} registry + {fileDel} file items";
            return (regDel, fileDel);
        }
        finally { IsBusy = false; }
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
        if (CurrentScanResult == null) return;
        var regSel = CurrentScanResult.RegistryLeftovers.Count(i => i.IsSelected);
        var fileSel = CurrentScanResult.FileLeftovers.Count(i => i.IsSelected);
        var sizeBytes = CurrentScanResult.FileLeftovers.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        var sizeStr = sizeBytes > 0 ? $" ({FormatSize(sizeBytes)})" : "";
        LeftoverStats = $"{regSel} registry + {fileSel} files selected{sizeStr}";
    }

    public void SelectAllSafe()
    {
        if (CurrentScanResult == null) return;
        foreach (var i in CurrentScanResult.RegistryLeftovers.Where(x => x.Confidence == LeftoverConfidence.Safe)) i.IsSelected = true;
        foreach (var i in CurrentScanResult.FileLeftovers.Where(x => x.Confidence == LeftoverConfidence.Safe)) i.IsSelected = true;
        RefreshLeftoverCollections(); UpdateLeftoverStats();
    }

    public void DeselectAll()
    {
        if (CurrentScanResult == null) return;
        foreach (var i in CurrentScanResult.RegistryLeftovers) i.IsSelected = false;
        foreach (var i in CurrentScanResult.FileLeftovers) i.IsSelected = false;
        RefreshLeftoverCollections(); UpdateLeftoverStats();
    }

    // ═══════════════════════════════════════════════════════
    //  REFRESH / RELOAD
    // ═══════════════════════════════════════════════════════

    public async Task RefreshAsync()
    {
        IconExtractor.ClearCache();
        _loadedPanels.Clear();
        IsBusy = true; StatusText = "Refreshing...";
        try
        {
            var programs = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms());
            Programs.Clear(); FilteredPrograms.Clear();
            foreach (var p in programs) { Programs.Add(p); FilteredPrograms.Add(p); }
            ProgramCountText = $"{programs.Count} programs";
            ProgramsBadge = programs.Count.ToString();
            _loadedPanels.Add("Programs");
            StatusText = $"Refreshed {programs.Count} programs";
            _ = Task.Run(() =>
            {
                foreach (var prog in programs)
                {
                    var icon = IconExtractor.GetProgramIcon(prog.DisplayIconPath, prog.UninstallString, prog.InstallLocation);
                    if (icon != null) _dispatcher.BeginInvoke(() => prog.Icon = icon);
                }
            });
        }
        finally { IsBusy = false; }
    }

    // ═══════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════

    public string SearchFilter
    {
        get => _searchFilter;
        set { _searchFilter = value; ApplyFilter(value); }
    }

    public void UpdateSelectedCount()
    {
        int count = 0;
        try
        {
            switch (CurrentPanel)
            {
                case "Programs": count = FilteredPrograms.Count(p => p.IsSelected); break;
                case "Junk": count = JunkCategories.Count(c => c.IsSelected); break;
                case "Evidence": count = TraceCategories.Count(c => c.IsSelected); break;
                case "EmptyFolders": count = EmptyFolders.Count(f => f.IsSelected); break;
                case "ContextMenu": count = ContextMenuEntries.Count(c => c.IsSelected); break;
                case "Services": count = Services.Count(s => s.IsSelected); break;
                case "Tasks": count = ScheduledTasks.Count(t => t.IsSelected); break;
            }
        }
        catch { }
        SelectedCountBadge = count > 0 ? $"{count} selected" : "";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double kb = bytes / 1024.0;
        if (kb < 1) return $"{bytes} B";
        double mb = kb / 1024.0;
        if (mb < 1) return $"{kb:F0} KB";
        double gb = mb / 1024.0;
        if (gb < 1) return $"{mb:F1} MB";
        return $"{gb:F2} GB";
    }

    public void CancelOperation() => _cts?.Cancel();
}
