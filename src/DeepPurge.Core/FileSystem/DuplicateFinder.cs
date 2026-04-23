using System.Buffers;
using System.IO.Hashing;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.FileSystem;

public class DuplicateGroup
{
    public long FileSize { get; set; }
    public List<string> Paths { get; set; } = new();
    public long WastedBytes => Paths.Count <= 1 ? 0 : FileSize * (Paths.Count - 1);
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
public class DuplicateFinder
{
    private const int FirstChunkBytes = 1 * 1024 * 1024;
    private const long MinFileBytes   = 4 * 1024;

    public async Task<List<DuplicateGroup>> FindAsync(
        IEnumerable<string> roots,
        long minBytes = MinFileBytes,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
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
            foreach (var headCluster in byHead.Values)
            {
                if (headCluster.Count < 2) continue;

                // Files ≤ head-chunk size are already fully hashed by stage 2.
                if (size <= FirstChunkBytes)
                {
                    finalGroups.Add(new DuplicateGroup { FileSize = size, Paths = headCluster });
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

                foreach (var fullCluster in byFull.Values.Where(c => c.Count >= 2))
                    finalGroups.Add(new DuplicateGroup { FileSize = size, Paths = fullCluster });
            }
        }

        return finalGroups
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
    }

    /// <summary>
    /// Delete all-but-one file in each group. Returns count deleted.
    /// Sorting on <see cref="FileInfo.LastWriteTimeUtc"/> is wrapped so a
    /// file that disappears between scan and delete doesn't throw.
    /// </summary>
    public int DeleteDuplicates(IEnumerable<DuplicateGroup> groups, DeleteOptions opt, bool keepNewest = true)
    {
        int n = 0;
        foreach (var group in groups)
        {
            if (group.Paths.Count < 2) continue;

            var annotated = new List<(string Path, DateTime Stamp)>(group.Paths.Count);
            foreach (var p in group.Paths)
            {
                DateTime stamp;
                try { stamp = new FileInfo(p).LastWriteTimeUtc; }
                catch { stamp = DateTime.MinValue; }  // missing files sort to "keep last"
                annotated.Add((p, stamp));
            }

            var sorted = keepNewest
                ? annotated.OrderByDescending(x => x.Stamp).Select(x => x.Path).ToList()
                : annotated.OrderBy(x => x.Stamp).Select(x => x.Path).ToList();

            foreach (var victim in sorted.Skip(1))
            {
                if (!SafetyGuard.IsPathSafeToDelete(victim)) continue;
                try
                {
                    if (!File.Exists(victim)) continue;
                    if (opt.IsDestructive)
                    {
                        if (opt.SecureDelete) SecureDelete.Wipe(victim);
                        else File.Delete(victim);
                    }
                    n++;
                }
                catch (Exception ex) { Log.Warn($"DeleteDuplicates '{victim}': {ex.Message}"); }
            }
        }
        return n;
    }

    // ═══════════════════════════════════════════════════════

    private static Dictionary<long, List<string>> GroupBySize(
        IEnumerable<string> roots, long minBytes, IProgress<string>? progress, CancellationToken ct)
    {
        var map = new Dictionary<long, List<string>>();
        int seen = 0;
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
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

    private static async Task<ulong?> HashHeadAsync(string path, CancellationToken ct)
    {
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
            return hash.GetCurrentHashAsUInt64();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task<ulong?> HashFullAsync(string path, CancellationToken ct)
    {
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
            return hash.GetCurrentHashAsUInt64();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
