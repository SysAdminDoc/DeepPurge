using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;
using System.ComponentModel;

namespace DeepPurge.Core.Cleaning;

public sealed class SlimmableComponent : INotifyPropertyChanged
{
    private bool _isSelected;

    public SlimmableComponent(
        string name,
        string description,
        string category,
        string path,
        long sizeBytes,
        bool isSelected)
    {
        Name = name;
        Description = description;
        Category = category;
        Path = path;
        SizeBytes = sizeBytes;
        _isSelected = isSelected;
    }

    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public string Path { get; }
    public long SizeBytes { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class SystemSlimmer
{
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static List<SlimmableComponent> Scan()
    {
        var items = new List<SlimmableComponent>();

        AddDir(items, "Windows Wallpapers", "Sample wallpapers shipped with Windows", "Media",
            Path.Combine(WinDir, "Web", "Wallpaper"), selected: false);
        AddDir(items, "Windows Screen Savers", "Legacy screen saver files", "Media",
            Path.Combine(WinDir, "Web", "Screen"), selected: false);
        AddDir(items, "Sample Music", "Pre-installed music samples", "Media",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonMusic)), selected: true);
        AddDir(items, "Sample Videos", "Pre-installed video samples", "Media",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos)), selected: true);
        AddDir(items, "Sample Pictures", "Pre-installed picture samples", "Media",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPictures)), selected: true);

        AddDir(items, "Windows Help Files", "CHM/HLP help files (rarely used)", "System",
            Path.Combine(WinDir, "Help"), selected: true);
        AddDir(items, "Installer Cache ($PatchCache$)", "MSI patch cache — safe to remove after install", "System",
            Path.Combine(WinDir, "Installer", "$PatchCache$"), selected: true);
        AddDir(items, "Delivery Optimization Cache", "Windows Update peer-to-peer cache", "System",
            Path.Combine(WinDir, "SoftwareDistribution", "DeliveryOptimization"), selected: true);
        AddDir(items, "Windows Update Download Cache", "Downloaded but applied update packages", "System",
            Path.Combine(WinDir, "SoftwareDistribution", "Download"), selected: false);
        AddDir(items, "Windows.old", "Previous Windows installation backup", "System",
            Path.Combine(Path.GetPathRoot(WinDir)?.TrimEnd('\\') ?? "C:", "Windows.old"), selected: false);
        AddDir(items, "Windows Error Reports", "Queued and archived WER crash reports", "System",
            Path.Combine(ProgramData, "Microsoft", "Windows", "WER"), selected: true);
        AddDir(items, "RetailDemo Content", "Retail demo assets (OEM)", "OEM",
            Path.Combine(WinDir, "SystemApps", "Microsoft.Windows.SecondaryTileExperience_cw5n1h2txyewy"), selected: true);
        AddDir(items, "IME Cache", "Input Method Editor cached data", "Language",
            Path.Combine(WinDir, "IME", "SHARED"), selected: false);
        AddDir(items, "Font Cache", "Cached font rendering data (rebuilds on demand)", "System",
            Path.Combine(WinDir, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache"), selected: true);

        ScanLogFolders(items);

        items.RemoveAll(i => i.SizeBytes <= 0);
        return items;
    }

    public static DeleteSummary Delete(IEnumerable<SlimmableComponent> components, DeleteOptions options,
        IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        var selected = components.Where(c => c.IsSelected).ToList();
        var executor = new DeletionExecutor();
        var results = new List<DeletionResult>(selected.Count);

        for (int i = 0; i < selected.Count; i++)
        {
            var comp = selected[i];
            var request = new DeletionRequest(
                comp.Path,
                IsDirectory: true,
                ExpectedSizeBytes: comp.SizeBytes,
                Operation: "system-slim");

            if (ct.IsCancellationRequested)
            {
                results.Add(new DeletionResult(
                    comp.Path,
                    DeletionOutcomeKind.Cancelled,
                    comp.SizeBytes,
                    request.Operation,
                    "Cancellation requested."));
                break;
            }

            if (!SafetyGuard.IsPathSafeToDelete(comp.Path))
            {
                results.Add(DeletionExecutor.Skipped(request, "The path is protected or invalid."));
                progress?.Report(new DeleteProgress(
                    i + 1,
                    selected.Count,
                    CurrentBytes(results, options.DryRun),
                    comp.Path,
                    true));
                continue;
            }

            if (Directory.Exists(comp.Path) && SafetyGuard.IsReparsePoint(comp.Path))
            {
                results.Add(DeletionExecutor.Skipped(request, "Reparse point."));
                progress?.Report(new DeleteProgress(
                    i + 1,
                    selected.Count,
                    CurrentBytes(results, options.DryRun),
                    comp.Path,
                    true));
                continue;
            }

            var result = executor.Execute(request, options, ct);
            results.Add(result);
            progress?.Report(new DeleteProgress(
                i + 1,
                selected.Count,
                CurrentBytes(results, options.DryRun),
                comp.Path,
                !result.IsConfirmed && !result.IsPreview));
        }

        var summary = DeleteSummary.FromResults(results, options.DryRun);
        if (!options.DryRun && summary.ItemsConfirmed > 0)
            ActivityLog.Record("slim", $"Removed {summary.ItemsConfirmed} Windows components", summary.BytesConfirmed, summary.ItemsConfirmed);

        return summary;
    }

    private static long CurrentBytes(
        IReadOnlyList<DeletionResult> results,
        bool dryRun)
        => dryRun
            ? results.Where(r => r.IsPreview).Sum(r => r.SizeBytes)
            : results.Where(r => r.IsConfirmed).Sum(r => r.SizeBytes);

    private static void ScanLogFolders(List<SlimmableComponent> items)
    {
        var logDirs = new[]
        {
            (Path.Combine(WinDir, "Logs"), "Windows Logs"),
            (Path.Combine(WinDir, "Panther"), "Windows Setup Logs"),
            (Path.Combine(WinDir, "Debug"), "Windows Debug Logs"),
        };
        foreach (var (path, name) in logDirs)
            AddDir(items, name, "System log files older than 7 days", "Logs", path, selected: true);
    }

    private static void AddDir(List<SlimmableComponent> items, string name, string desc, string category,
        string path, bool selected)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            var size = GetDirSize(path);
            if (size > 0)
                items.Add(new SlimmableComponent(name, desc, category, path, size, selected));
        }
        catch (Exception ex) { Log.Warn($"System slimming scan failed for '{name}': {ex.Message}"); }
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
}
