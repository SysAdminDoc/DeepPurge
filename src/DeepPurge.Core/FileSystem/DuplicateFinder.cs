using System.Buffers;
using System.ComponentModel;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.FileSystem;

public sealed record DuplicateFileIdentity(
    long SizeBytes,
    long LastWriteUtcTicks,
    ulong FullHash);

public sealed class DuplicateGroup : INotifyPropertyChanged
{
    private string _keeperPath = string.Empty;

    public long FileSize { get; set; }
    public List<string> Paths { get; set; } = new();
    public ulong ContentHash { get; set; }
    public Dictionary<string, DuplicateFileIdentity> ScannedIdentities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The copy explicitly retained for this group. An empty value means the
    /// group is review-only until a user selects one or supplies a protected
    /// reference folder to the deletion policy.
    /// </summary>
    public string KeeperPath
    {
        get => _keeperPath;
        set
        {
            _keeperPath = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasExplicitKeeper));
            OnPropertyChanged(nameof(KeeperDisplay));
        }
    }

    public IReadOnlyList<string> KeeperOptions => Paths;
    public bool HasExplicitKeeper => Paths.Contains(
        KeeperPath,
        StringComparer.OrdinalIgnoreCase);
    public string KeeperDisplay => HasExplicitKeeper
        ? KeeperPath
        : "Select a keeper";
    public long WastedBytes => Paths.Count <= 1 ? 0 : FileSize * (Paths.Count - 1);

    public bool RemovePath(string path)
    {
        var removed = Paths.Remove(path);
        if (!removed) return false;
        if (path.Equals(KeeperPath, StringComparison.OrdinalIgnoreCase))
            KeeperPath = string.Empty;
        OnPropertyChanged(nameof(KeeperOptions));
        OnPropertyChanged(nameof(HasExplicitKeeper));
        OnPropertyChanged(nameof(KeeperDisplay));
        OnPropertyChanged(nameof(WastedBytes));
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Deletion may use a per-group keeper selected in <see cref="DuplicateGroup.KeeperPath"/>
/// or a directory whose files are protected from duplicate removal. No
/// timestamp-based keeper is selected by this policy.
/// </summary>
public sealed class DuplicateKeeperPolicy
{
    public DuplicateKeeperPolicy(string? referenceFolder = null)
    {
        if (string.IsNullOrWhiteSpace(referenceFolder)) return;
        if (!Path.IsPathFullyQualified(referenceFolder))
            throw new ArgumentException(
                "The duplicate reference folder must be an absolute path.",
                nameof(referenceFolder));
        ReferenceFolder = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(referenceFolder));
    }

    public string? ReferenceFolder { get; }

    public bool HasReferenceFolder => !string.IsNullOrWhiteSpace(ReferenceFolder);

    public bool IsProtectedReferencePath(string path)
        => HasReferenceFolder && IsUnder(path, ReferenceFolder!);

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(
                       normalizedRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public class DuplicateDirectoryGroup
{
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public int MatchPercent { get; set; }
    public List<string> Paths { get; set; } = new();
    public long WastedBytes => Paths.Count <= 1 ? 0 : TotalSize * (Paths.Count - 1);
}

/// <summary>
/// Three-stage duplicate finder:
///   1. Group files by exact byte-size. Different size = not duplicates.
///   2. XXH3 hash the first 1 MB of each remaining candidate. Mismatch = not duplicates.
///   3. For any group still colliding, XXH3 the full file.
///
/// Uses <see cref="ArrayPool{T}"/> for the head-hash buffer so a scan of a
/// million files doesn't allocate a million 1 MB arrays. Matches the
/// algorithm used by Czkawka / dupeGuru / fdupes.
/// </summary>
public class HashCacheEntry
{
    public long Size { get; set; }
    public long LastWriteTicks { get; set; }
    public ulong HeadHash { get; set; }
    public ulong FullHash { get; set; }
    public bool HasFullHash { get; set; }
}

public class DuplicateFinder
{
    private const int FirstChunkBytes = 1 * 1024 * 1024;
    private const long MinFileBytes   = 4 * 1024;
    private static string CachePath => Path.Combine(DataPaths.Root, "hash-cache.json");
    private ConcurrentDictionary<string, HashCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<DuplicateGroup>> FindAsync(
        IEnumerable<string> roots,
        long minBytes = MinFileBytes,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        LoadCache();
        var bySize = await Task.Run(() => GroupBySize(roots, minBytes, progress, ct), ct);
        progress?.Report($"Stage 1: {bySize.Count} size-collision groups");

        var finalGroups = new List<DuplicateGroup>();
        int groupIndex = 0;
        foreach (var kv in bySize)
        {
            ct.ThrowIfCancellationRequested();
            groupIndex++;
            if (groupIndex % 25 == 0) progress?.Report($"Stage 2/3: {groupIndex}/{bySize.Count} groups...");

            long size = kv.Key;
            var candidates = kv.Value;
            if (candidates.Count < 2) continue;

            // Stage 2: head-hash.
            var byHead = new Dictionary<ulong, List<string>>();
            foreach (var f in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var head = await HashHeadAsync(f, ct);
                if (head == null) continue;
                if (!byHead.TryGetValue(head.Value, out var list))
                    byHead[head.Value] = list = new List<string>();
                list.Add(f);
            }

            // Stage 3: full hash only for head-collision clusters ≥ 2.
            foreach (var headEntry in byHead)
            {
                var headCluster = headEntry.Value;
                if (headCluster.Count < 2) continue;

                // Files ≤ head-chunk size are already fully hashed by stage 2.
                if (size <= FirstChunkBytes)
                {
                    finalGroups.Add(new DuplicateGroup
                    {
                        FileSize = size,
                        ContentHash = headEntry.Key,
                        Paths = headCluster,
                        ScannedIdentities = CaptureScanIdentities(headCluster, size, headEntry.Key),
                    });
                    continue;
                }

                var byFull = new Dictionary<ulong, List<string>>();
                foreach (var f in headCluster)
                {
                    ct.ThrowIfCancellationRequested();
                    var full = await HashFullAsync(f, ct);
                    if (full == null) continue;
                    if (!byFull.TryGetValue(full.Value, out var list))
                        byFull[full.Value] = list = new List<string>();
                    list.Add(f);
                }

                foreach (var fullEntry in byFull.Where(kv => kv.Value.Count >= 2))
                    finalGroups.Add(new DuplicateGroup
                    {
                        FileSize = size,
                        ContentHash = fullEntry.Key,
                        Paths = fullEntry.Value,
                        ScannedIdentities = CaptureScanIdentities(fullEntry.Value, size, fullEntry.Key),
                    });
            }
        }

        SaveCache();
        return finalGroups
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
    }

    /// <summary>
    /// Compatibility wrapper for callers that explicitly choose the old
    /// timestamp policy. New callers should select <see cref="DuplicateGroup.KeeperPath"/>
    /// and use <see cref="DuplicateKeeperPolicy"/> so age is never an
    /// accidental keeper decision.
    /// </summary>
    public int DeleteDuplicates(
        IEnumerable<DuplicateGroup> groups,
        DeleteOptions opt,
        bool keepNewest)
    {
        var selectedGroups = groups.ToList();
        foreach (var group in selectedGroups.Where(g => g.Paths.Count >= 2 && !g.HasExplicitKeeper))
            group.KeeperPath = SelectTimestampKeeper(group.Paths, keepNewest);
        return DeleteDuplicatesDetailed(selectedGroups, opt, new DuplicateKeeperPolicy()).ItemsDeleted;
    }

    /// <summary>
    /// Compatibility overload for the former timestamp-based API. The
    /// timestamp choice is materialized as an explicit keeper before the
    /// safety validation begins.
    /// </summary>
    public DeleteSummary DeleteDuplicatesDetailed(
        IEnumerable<DuplicateGroup> groups,
        DeleteOptions opt,
        bool? keepNewest = null,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        var selectedGroups = groups.ToList();
        if (keepNewest.HasValue)
        {
            foreach (var group in selectedGroups.Where(g => g.Paths.Count >= 2 && !g.HasExplicitKeeper))
                group.KeeperPath = SelectTimestampKeeper(group.Paths, keepNewest.Value);
        }
        return DeleteDuplicatesDetailed(selectedGroups, opt, new DuplicateKeeperPolicy(), progress, ct);
    }

    /// <summary>
    /// Removes duplicate candidates only after resolving an explicit keeper
    /// and revalidating every remaining path's size, timestamp, and full
    /// content hash. A changed group aborts its remaining removals.
    /// </summary>
    public DeleteSummary DeleteDuplicatesDetailed(
        IEnumerable<DuplicateGroup> groups,
        DeleteOptions opt,
        DuplicateKeeperPolicy policy,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var selectedGroups = groups.ToList();
        var estimatedTotal = selectedGroups.Sum(g => Math.Max(g.Paths.Count, 1));
        var executor = new DeletionExecutor();
        var results = new List<DeletionResult>();
        var processed = 0;

        void AddResult(DeletionResult result)
        {
            results.Add(result);
            processed++;
            progress?.Report(new DeleteProgress(
                processed,
                estimatedTotal,
                opt.DryRun
                    ? results.Where(r => r.IsPreview).Sum(r => r.SizeBytes)
                    : results.Where(r => r.IsConfirmed).Sum(r => r.SizeBytes),
                result.Path,
                !result.IsConfirmed && !result.IsPreview));
        }

        void AddSkipped(IEnumerable<string> paths, DuplicateGroup group, string reason)
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                AddResult(new DeletionResult(
                    path,
                    DeletionOutcomeKind.Skipped,
                    group.FileSize,
                    "duplicate-clean",
                    reason));
        }

        for (var groupIndex = 0; groupIndex < selectedGroups.Count; groupIndex++)
        {
            var group = selectedGroups[groupIndex];
            var paths = group.Paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count < 2) continue;

            if (ct.IsCancellationRequested)
            {
                AddCancelled(paths, group, AddResult);
                foreach (var remainingGroup in selectedGroups.Skip(groupIndex + 1))
                    AddCancelled(remainingGroup.Paths, remainingGroup, AddResult);
                break;
            }

            var protectedPaths = paths
                .Where(policy.IsProtectedReferencePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keeper = group.HasExplicitKeeper
                ? group.KeeperPath
                : protectedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(keeper) ||
                !paths.Contains(keeper, StringComparer.OrdinalIgnoreCase))
            {
                AddSkipped(
                    paths,
                    group,
                    policy.HasReferenceFolder
                        ? "No duplicate is inside the protected reference folder and no explicit keeper was selected."
                        : "No explicit keeper was selected for this duplicate group.");
                continue;
            }

            var victims = paths
                .Where(p => !p.Equals(keeper, StringComparison.OrdinalIgnoreCase) &&
                            !protectedPaths.Contains(p))
                .ToList();
            AddSkipped(
                protectedPaths.Where(p => !p.Equals(keeper, StringComparison.OrdinalIgnoreCase)),
                group,
                "Protected reference-folder copy retained.");

            if (!TryCaptureGroup(paths, group, out var expected, out var expectedHash, out var validationReason))
            {
                AddSkipped(paths.Where(p => !protectedPaths.Contains(p) || p.Equals(keeper, StringComparison.OrdinalIgnoreCase)), group, validationReason);
                continue;
            }

            if (group.ContentHash == 0)
                group.ContentHash = expectedHash;

            var pendingVictims = new HashSet<string>(victims, StringComparer.OrdinalIgnoreCase);
            foreach (var victim in victims)
            {
                if (ct.IsCancellationRequested)
                {
                    AddCancelled(pendingVictims, group, AddResult);
                    break;
                }

                var pendingPaths = paths.Where(p =>
                    p.Equals(keeper, StringComparison.OrdinalIgnoreCase) ||
                    pendingVictims.Contains(p) ||
                    protectedPaths.Contains(p)).ToList();
                if (!TryValidateGroup(pendingPaths, expected, out validationReason))
                {
                    AddSkipped(pendingVictims, group, validationReason);
                    break;
                }

                var identity = expected[victim];
                var result = executor.Execute(
                    new DeletionRequest(
                        victim,
                        IsDirectory: false,
                        ExpectedSizeBytes: group.FileSize,
                        Operation: "duplicate-clean",
                        ExpectedContentHash: identity.FullHash,
                        ExpectedLastWriteUtcTicks: identity.LastWriteUtcTicks),
                    opt,
                    ct);
                AddResult(result);

                if (result.IsConfirmed)
                {
                    pendingVictims.Remove(victim);
                    expected.Remove(victim);
                }
                else if (!result.IsPreview)
                {
                    pendingVictims.Remove(victim);
                    AddSkipped(pendingVictims, group, "Duplicate group stopped after a removal failed or was skipped.");
                    break;
                }
            }
        }

        return DeleteSummary.FromResults(results, opt.DryRun);
    }

    private static void AddCancelled(
        IEnumerable<string> paths,
        DuplicateGroup group,
        Action<DeletionResult> addResult)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            addResult(new DeletionResult(
                path,
                DeletionOutcomeKind.Cancelled,
                group.FileSize,
                "duplicate-clean",
                "Cancellation requested."));
    }

    private static string SelectTimestampKeeper(IEnumerable<string> paths, bool keepNewest)
    {
        var annotated = paths.Select(path =>
        {
            try { return (Path: path, Stamp: new FileInfo(path).LastWriteTimeUtc); }
            catch { return (Path: path, Stamp: DateTime.MinValue); }
        });
        return (keepNewest
                ? annotated.OrderByDescending(x => x.Stamp)
                : annotated.OrderBy(x => x.Stamp))
            .Select(x => x.Path)
            .First();
    }

    private static bool TryCaptureGroup(
        IReadOnlyList<string> paths,
        DuplicateGroup group,
        out Dictionary<string, DuplicateFileIdentity> identities,
        out ulong expectedHash,
        out string reason)
    {
        identities = new Dictionary<string, DuplicateFileIdentity>(StringComparer.OrdinalIgnoreCase);
        expectedHash = group.ContentHash;
        foreach (var path in paths)
        {
            if (!TryReadFullIdentity(path, out var identity, out reason))
                return false;
            if (identity.SizeBytes != group.FileSize)
            {
                reason = "Duplicate group changed size since it was scanned.";
                return false;
            }
            if (group.ScannedIdentities.TryGetValue(path, out var scannedIdentity) &&
                identity != scannedIdentity)
            {
                reason = "Duplicate candidate changed identity since it was scanned.";
                return false;
            }
            if (expectedHash == 0)
                expectedHash = identity.FullHash;
            if (identity.FullHash != expectedHash)
            {
                reason = "Duplicate group no longer has one full content identity.";
                return false;
            }
            identities[path] = identity;
        }

        reason = string.Empty;
        return true;
    }

    private static Dictionary<string, DuplicateFileIdentity> CaptureScanIdentities(
        IEnumerable<string> paths,
        long size,
        ulong fullHash)
    {
        var identities = new Dictionary<string, DuplicateFileIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                    identities[path] = new DuplicateFileIdentity(
                        size,
                        info.LastWriteTimeUtc.Ticks,
                        fullHash);
            }
            catch { /* the delete boundary will report a precise skip */ }
        }
        return identities;
    }

    private static bool TryValidateGroup(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, DuplicateFileIdentity> expected,
        out string reason)
    {
        reason = string.Empty;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!expected.TryGetValue(path, out var expectedIdentity) ||
                !TryReadFullIdentity(path, out var actual, out reason))
                return false;
            if (actual != expectedIdentity)
            {
                reason = "Duplicate group changed after hashing; no further copies were removed.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryReadFullIdentity(
        string path,
        out DuplicateFileIdentity identity,
        out string reason)
    {
        identity = new DuplicateFileIdentity(0, 0, 0);
        reason = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                reason = "Duplicate candidate is missing.";
                return false;
            }
            if (SafetyGuard.IsReparsePoint(path))
            {
                reason = "Duplicate candidate became a reparse point.";
                return false;
            }

            var before = new FileInfo(path);
            var hash = new XxHash3();
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 256 * 1024,
                options: FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        hash.Append(buffer.AsSpan(0, read));
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            var after = new FileInfo(path);
            if (!after.Exists ||
                before.Length != after.Length ||
                before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks)
            {
                reason = "Duplicate candidate changed while it was being hashed.";
                return false;
            }

            identity = new DuplicateFileIdentity(
                after.Length,
                after.LastWriteTimeUtc.Ticks,
                hash.GetCurrentHashAsUInt64());
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            reason = $"Duplicate candidate could not be revalidated: {ex.Message}";
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════

    private static Dictionary<long, List<string>> GroupBySize(
        IEnumerable<string> roots, long minBytes, IProgress<string>? progress, CancellationToken ct)
    {
        var map = new Dictionary<long, List<string>>();
        int seen = 0;
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            var fs = VolumeFileSystem.GetForPath(root);
            if (fs.UsesFallbackEnumeration)
                progress?.Report($"Stage 1: scanning {root} (fallback mode: {FallbackName(fs)})...");

            foreach (var file in SafeEnumerate(root, ct))
            {
                ct.ThrowIfCancellationRequested();
                seen++;
                if (seen % 5000 == 0) progress?.Report($"Stage 1: scanned {seen:N0} files...");
                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }
                if (size < minBytes) continue;
                if (!map.TryGetValue(size, out var list))
                    map[size] = list = new List<string>();
                list.Add(file);
            }
        }
        // Drop unique sizes up front — can't be dupes by definition.
        return map.Where(kv => kv.Value.Count >= 2)
                  .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static string FallbackName(VolumeFileSystemInfo fs)
        => fs.IsKnown ? fs.FileSystemName : "unknown filesystem";

    /// <summary>
    /// Iterative walker that skips reparse points (junctions / symlinks) to
    /// prevent <c>C:\Users\All Users → C:\ProgramData → C:\Users\All Users</c>
    /// infinite recursion. Does not use <see cref="Directory.EnumerateFiles"/>
    /// with <see cref="SearchOption.AllDirectories"/> for exactly that reason.
    /// </summary>
    private static IEnumerable<string> SafeEnumerate(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var cur = stack.Pop();
            string[] files, dirs;
            try
            {
                var attr = File.GetAttributes(cur);
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                files = Directory.GetFiles(cur);
                dirs  = Directory.GetDirectories(cur);
            }
            catch { continue; }

            foreach (var f in files) yield return f;
            foreach (var d in dirs) stack.Push(d);
        }
    }

    private async Task<ulong?> HashHeadAsync(string path, CancellationToken ct)
    {
        if (TryGetCachedHead(path, out var cached)) return cached;
        byte[]? rented = null;
        try
        {
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024, useAsync: true);

            rented = ArrayPool<byte>.Shared.Rent(FirstChunkBytes);
            int total = 0;
            while (total < FirstChunkBytes)
            {
                int n = await fs.ReadAsync(rented.AsMemory(total, FirstChunkBytes - total), ct);
                if (n == 0) break;
                total += n;
            }
            var hash = new XxHash3();
            hash.Append(rented.AsSpan(0, total));
            var result = hash.GetCurrentHashAsUInt64();
            UpdateCache(path, headHash: result);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task<ulong?> HashFullAsync(string path, CancellationToken ct)
    {
        if (TryGetCachedFull(path, out var cached)) return cached;
        const int BufferBytes = 256 * 1024;
        byte[]? rented = null;
        try
        {
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: BufferBytes, useAsync: true);

            rented = ArrayPool<byte>.Shared.Rent(BufferBytes);
            var hash = new XxHash3();
            int read;
            while ((read = await fs.ReadAsync(rented.AsMemory(0, BufferBytes), ct)) > 0)
                hash.Append(rented.AsSpan(0, read));
            var result = hash.GetCurrentHashAsUInt64();
            UpdateCache(path, fullHash: result);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool TryGetCachedHead(string path, out ulong hash)
    {
        hash = 0;
        if (!_cache.TryGetValue(path, out var entry)) return false;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length != entry.Size || fi.LastWriteTimeUtc.Ticks != entry.LastWriteTicks) return false;
            hash = entry.HeadHash;
            return true;
        }
        catch { return false; }
    }

    private bool TryGetCachedFull(string path, out ulong hash)
    {
        hash = 0;
        if (!_cache.TryGetValue(path, out var entry)) return false;
        if (!entry.HasFullHash) return false;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length != entry.Size || fi.LastWriteTimeUtc.Ticks != entry.LastWriteTicks) return false;
            hash = entry.FullHash;
            return true;
        }
        catch { return false; }
    }

    private void UpdateCache(string path, ulong? headHash = null, ulong? fullHash = null)
    {
        try
        {
            var fi = new FileInfo(path);
            var entry = _cache.GetOrAdd(path, _ => new HashCacheEntry());
            entry.Size = fi.Length;
            entry.LastWriteTicks = fi.LastWriteTimeUtc.Ticks;
            if (headHash.HasValue) entry.HeadHash = headHash.Value;
            if (fullHash.HasValue) { entry.FullHash = fullHash.Value; entry.HasFullHash = true; }
        }
        catch (Exception ex) { Log.Warn($"UpdateCache '{path}': {ex.Message}"); }
    }

    public async Task<List<DuplicateDirectoryGroup>> FindDuplicateDirectoriesAsync(
        IEnumerable<string> roots,
        int minFiles = 2,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        LoadCache();
        var dirs = new List<(string Path, long Size, int FileCount, string Fingerprint)>();

        int scanned = 0;
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            foreach (var dir in SafetyGuard.SafeEnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;
                if (scanned % 100 == 0) progress?.Report($"Scanning directories: {scanned:N0}...");

                var fingerprint = await ComputeDirectoryFingerprintAsync(dir, ct);
                if (fingerprint == null || fingerprint.FileCount < minFiles) continue;
                dirs.Add((dir, fingerprint.TotalSize, fingerprint.FileCount, fingerprint.Hash));
            }
        }

        progress?.Report($"Grouping {dirs.Count} directories...");

        var groups = dirs
            .GroupBy(d => d.Fingerprint)
            .Where(g => g.Count() >= 2)
            .Select(g => new DuplicateDirectoryGroup
            {
                TotalSize = g.First().Size,
                FileCount = g.First().FileCount,
                MatchPercent = 100,
                Paths = g.Select(d => d.Path).ToList(),
            })
            .OrderByDescending(g => g.WastedBytes)
            .ToList();

        SaveCache();
        return groups;
    }

    private record DirectoryFingerprint(string Hash, long TotalSize, int FileCount);

    private async Task<DirectoryFingerprint?> ComputeDirectoryFingerprintAsync(string dir, CancellationToken ct)
    {
        try
        {
            var entries = new SortedList<string, (long Size, ulong Hash)>(StringComparer.OrdinalIgnoreCase);
            long totalSize = 0;

            foreach (var file in SafeEnumerate(dir, ct))
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(dir, file);
                long size;
                try { size = new FileInfo(file).Length; }
                catch { continue; }
                totalSize += size;

                var hash = await HashHeadAsync(file, ct);
                if (hash == null) continue;
                entries[relativePath] = (size, hash.Value);
            }

            if (entries.Count == 0) return null;

            var combinedHash = new XxHash3();
            foreach (var kv in entries)
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(kv.Key);
                combinedHash.Append(nameBytes);
                combinedHash.Append(BitConverter.GetBytes(kv.Value.Size));
                combinedHash.Append(BitConverter.GetBytes(kv.Value.Hash));
            }

            return new DirectoryFingerprint(
                combinedHash.GetCurrentHashAsUInt64().ToString("X16"),
                totalSize,
                entries.Count);
        }
        catch { return null; }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, HashCacheEntry>>(json);
            if (dict != null)
                _cache = new ConcurrentDictionary<string, HashCacheEntry>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch { _cache = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache);
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex) { Log.Warn($"SaveCache: {ex.Message}"); }
    }
}
