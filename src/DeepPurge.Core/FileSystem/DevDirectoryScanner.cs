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
        long freed = 0;
        int cleaned = 0, skipped = 0;

        for (int i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var dir = selected[i];

            if (!SafetyGuard.IsPathSafeToDelete(dir.Path))
            {
                skipped++;
                progress?.Report(new DeleteProgress(i + 1, selected.Count, freed, dir.Path, true));
                continue;
            }

            if (options.DryRun)
            {
                freed += dir.SizeBytes;
                cleaned++;
                progress?.Report(new DeleteProgress(i + 1, selected.Count, freed, dir.Path, false));
                continue;
            }

            try
            {
                if (Directory.Exists(dir.Path) && !SafetyGuard.IsReparsePoint(dir.Path))
                {
                    SafetyGuard.SafeDeleteDirectory(dir.Path);
                    freed += dir.SizeBytes;
                    cleaned++;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Dev directory cleanup failed for '{dir.Path}': {ex.Message}");
                skipped++;
            }
            progress?.Report(new DeleteProgress(i + 1, selected.Count, freed, dir.Path, false));
        }

        if (!options.DryRun && cleaned > 0)
            ActivityLog.Record("dev-clean", $"Removed {cleaned} dev directories", freed, cleaned);

        return new DeleteSummary(cleaned, skipped, freed, options.DryRun);
    }

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
