using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.FileSystem;

public record DevDirectory(string Path, string Type, long SizeBytes, bool IsSelected);

public static class DevDirectoryScanner
{
    private static readonly HashSet<string> DevDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".next", "__pycache__", ".pytest_cache",
        "venv", ".venv", "env",
        ".gradle", ".m2",
        "target",
        "dist", "build",
    };

    private static readonly HashSet<string> DotnetBuildDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj",
    };

    public static List<DevDirectory> Scan(string root, CancellationToken ct = default)
    {
        var results = new List<DevDirectory>();
        if (!Directory.Exists(root)) return results;

        ScanRecursive(root, results, ct, depth: 0, maxDepth: 12);
        return results.OrderByDescending(d => d.SizeBytes).ToList();
    }

    public static DeleteSummary Delete(IEnumerable<DevDirectory> directories, DeleteOptions options,
        IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        var selected = directories.Where(d => d.IsSelected).ToList();
        var executor = new DeletionExecutor();
        var results = new List<DeletionResult>(selected.Count);

        for (int i = 0; i < selected.Count; i++)
        {
            var dir = selected[i];
            var request = new DeletionRequest(
                dir.Path,
                IsDirectory: true,
                ExpectedSizeBytes: dir.SizeBytes,
                Operation: "dev-clean");

            if (ct.IsCancellationRequested)
            {
                results.Add(new DeletionResult(
                    dir.Path,
                    DeletionOutcomeKind.Cancelled,
                    dir.SizeBytes,
                    request.Operation,
                    "Cancellation requested."));
                break;
            }

            if (!SafetyGuard.IsPathSafeToDelete(dir.Path))
            {
                results.Add(DeletionExecutor.Skipped(request, "The path is protected or invalid."));
                progress?.Report(new DeleteProgress(
                    i + 1,
                    selected.Count,
                    CurrentBytes(results, options.DryRun),
                    dir.Path,
                    true));
                continue;
            }

            if (Directory.Exists(dir.Path) && SafetyGuard.IsReparsePoint(dir.Path))
            {
                results.Add(DeletionExecutor.Skipped(request, "Reparse point."));
                progress?.Report(new DeleteProgress(
                    i + 1,
                    selected.Count,
                    CurrentBytes(results, options.DryRun),
                    dir.Path,
                    true));
                continue;
            }

            var result = executor.Execute(request, options, ct);
            results.Add(result);
            progress?.Report(new DeleteProgress(
                i + 1,
                selected.Count,
                CurrentBytes(results, options.DryRun),
                dir.Path,
                !result.IsConfirmed && !result.IsPreview));
        }

        var summary = DeleteSummary.FromResults(results, options.DryRun);
        if (!options.DryRun && summary.ItemsConfirmed > 0)
            ActivityLog.Record("dev-clean", $"Removed {summary.ItemsConfirmed} dev directories", summary.BytesConfirmed, summary.ItemsConfirmed);

        return summary;
    }

    private static long CurrentBytes(
        IReadOnlyList<DeletionResult> results,
        bool dryRun)
        => dryRun
            ? results.Where(r => r.IsPreview).Sum(r => r.SizeBytes)
            : results.Where(r => r.IsConfirmed).Sum(r => r.SizeBytes);

    private static void ScanRecursive(string dir, List<DevDirectory> results, CancellationToken ct, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        ct.ThrowIfCancellationRequested();

        try
        {
            foreach (var sub in SafetyGuard.SafeEnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);

                if (DevDirNames.Contains(name))
                {
                    var size = ComputeSize(sub, ct);
                    if (size > 0)
                        results.Add(new DevDirectory(sub, name, size, IsSelected: true));
                    continue;
                }

                if (DotnetBuildDirs.Contains(name) && HasCsprojSibling(dir))
                {
                    var size = ComputeSize(sub, ct);
                    if (size > 0)
                        results.Add(new DevDirectory(sub, name, size, IsSelected: true));
                    continue;
                }

                ScanRecursive(sub, results, ct, depth + 1, maxDepth);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Dev directory scan failed in '{dir}': {ex.Message}"); }
    }

    private static bool HasCsprojSibling(string parentDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(parentDir, "*.csproj"))
                return true;
            foreach (var file in Directory.EnumerateFiles(parentDir, "*.fsproj"))
                return true;
        }
        catch (Exception ex) { Log.Warn($"Csproj sibling check failed in '{parentDir}': {ex.Message}"); }
        return false;
    }

    private static long ComputeSize(string dir, CancellationToken ct)
    {
        long total = 0;
        try
        {
            foreach (var file in SafetyGuard.SafeEnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) { Log.Warn($"File size read failed for '{file}': {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Size computation failed for '{dir}': {ex.Message}"); }
        return total;
    }
}
