using System.Collections.Concurrent;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.InstallMonitor;

public record SnapshotEntry(string Path, long SizeBytes, DateTime LastWriteUtc);
public record RegistryKeyEntry(string Path);

public class InstallSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProgramName { get; set; } = "";
    public string InstallerPath { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public List<SnapshotEntry>    Files        { get; set; } = new();
    public List<RegistryKeyEntry> RegistryKeys { get; set; } = new();
}

public class InstallDelta
{
    public List<SnapshotEntry> AddedFiles         { get; set; } = new();
    public List<string>        AddedRegistryKeys  { get; set; } = new();
    public List<string>        RemovedFiles       { get; set; } = new();
    public List<string>        RemovedRegistryKeys{ get; set; } = new();
    public long TotalAddedBytes => AddedFiles.Sum(f => f.SizeBytes);
}

/// <summary>
/// Captures before/after snapshots of the filesystem + registry around an
/// installer run. The delta is persisted per-application so "forced uninstall"
/// can reference an exact manifest rather than heuristic name-matching.
///
/// Scope tradeoff: we only walk a curated list of high-signal roots
/// (Program Files, ProgramData, user AppData) + the three registry hives
/// most installers touch. Full-disk USN-journal tracking is deliberately
/// out of scope — it's an order of magnitude more work and storage.
///
/// Persistence safeguards:
///   - Snapshots are gzipped to keep Program Files walks &lt; 5 MB.
///   - Only the most recent <see cref="MaxSnapshotsPerProgram"/> entries are
///     retained per program; older ones are pruned on save.
///   - Old snapshot files across all programs are trimmed to
///     <see cref="MaxTotalSnapshots"/> on every capture to avoid unbounded disk use.
/// </summary>
public class InstallSnapshotEngine
{
    private const int MaxSnapshotsPerProgram = 3;
    private const int MaxTotalSnapshots      = 30;

    private static readonly string[] FsRoots =
    {
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData",
    };

    private static readonly (string Hive, string Sub)[] RegRoots =
    {
        ("HKLM", @"SOFTWARE"),
        ("HKLM", @"SOFTWARE\WOW6432Node"),
        ("HKCU", @"SOFTWARE"),
    };

    public static string SnapshotDir => DataPaths.Snapshots;

    public async Task<InstallSnapshot> CaptureAsync(string programName, string installerPath, CancellationToken ct = default)
    {
        var snap = new InstallSnapshot
        {
            ProgramName = programName,
            InstallerPath = installerPath,
            CapturedAt = DateTime.UtcNow,
        };

        var fsRoots = new List<string>(FsRoots)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        // Parallel across roots — the individual trees are independent and
        // Program Files is IO-bound. ConcurrentBag collects from workers.
        var filesBag = new ConcurrentBag<SnapshotEntry>();
        var keysBag  = new ConcurrentBag<RegistryKeyEntry>();

        var fsTasks = fsRoots
            .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
            .Select(r => Task.Run(() =>
            {
                foreach (var file in SafeEnumerate(r, ct))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        filesBag.Add(new SnapshotEntry(file, fi.Length, fi.LastWriteTimeUtc));
                    }
                    catch { /* access denied: skip */ }
                }
            }, ct))
            .ToArray();

        var regTasks = RegRoots
            .Select(t => Task.Run(() => EnumerateRegKeys(t.Hive, t.Sub, keysBag, maxDepth: 3, ct), ct))
            .ToArray();

        await Task.WhenAll(fsTasks.Concat(regTasks));

        snap.Files        = filesBag.ToList();
        snap.RegistryKeys = keysBag.ToList();

        SaveSnapshot(snap);
        PruneSnapshots(programName);
        return snap;
    }

    public InstallDelta Diff(InstallSnapshot before, InstallSnapshot after)
    {
        var beforeFiles = new HashSet<string>(before.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var afterFiles  = new HashSet<string>(after.Files.Select(f => f.Path),  StringComparer.OrdinalIgnoreCase);
        var beforeKeys  = new HashSet<string>(before.RegistryKeys.Select(k => k.Path), StringComparer.OrdinalIgnoreCase);
        var afterKeys   = new HashSet<string>(after.RegistryKeys.Select(k => k.Path),  StringComparer.OrdinalIgnoreCase);

        var delta = new InstallDelta();
        foreach (var f in after.Files) if (!beforeFiles.Contains(f.Path)) delta.AddedFiles.Add(f);
        foreach (var k in after.RegistryKeys) if (!beforeKeys.Contains(k.Path)) delta.AddedRegistryKeys.Add(k.Path);
        foreach (var f in before.Files) if (!afterFiles.Contains(f.Path)) delta.RemovedFiles.Add(f.Path);
        foreach (var k in before.RegistryKeys) if (!afterKeys.Contains(k.Path)) delta.RemovedRegistryKeys.Add(k.Path);
        return delta;
    }

    /// <summary>
    /// Snapshot → launch installer → wait for exit + idle → snapshot → diff.
    /// If the user cancels the installer process (UAC deny, manual kill) we
    /// still capture the "after" state and surface an empty/partial delta
    /// rather than hanging.
    /// </summary>
    public async Task<InstallDelta> TraceInstallAsync(
        string programName,
        string installerPath,
        string? installerArgs = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(programName))
            throw new ArgumentException("programName is required", nameof(programName));
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            throw new FileNotFoundException("installer not found", installerPath);

        var before = await CaptureAsync(programName, installerPath, ct);

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = installerArgs ?? "",
            UseShellExecute = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p != null)
            {
                await p.WaitForExitAsync(ct);
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { /* snapshot anyway */ }
            }
        }
        catch (Exception ex) { Log.Warn($"Installer launch failed: {ex.Message}"); }

        var after = await CaptureAsync(programName, installerPath, ct);
        var delta = Diff(before, after);
        SaveManifest(programName, delta);
        return delta;
    }

    public void SaveManifest(string programName, InstallDelta delta)
    {
        var path = ManifestPath(programName);
        var json = JsonSerializer.Serialize(delta, new JsonSerializerOptions { WriteIndented = true });
        AtomicWrite(path, json);
    }

    public InstallDelta? LoadManifest(string programName)
    {
        var path = ManifestPath(programName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<InstallDelta>(File.ReadAllText(path)); }
        catch (Exception ex) { Log.Warn($"LoadManifest '{path}': {ex.Message}"); return null; }
    }

    /// <summary>
    /// Remove every file in a saved install manifest. SafetyGuard is applied
    /// per-item, so protected paths are skipped with a count.
    /// </summary>
    public async Task<(int Removed, int Skipped, long Freed)> ReplayRemoveAsync(
        InstallDelta delta,
        DeleteOptions opt,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        int removed = 0, skipped = 0;
        long freed = 0;
        int total = delta.AddedFiles.Count, i = 0;

        await Task.Run(() =>
        {
            foreach (var f in delta.AddedFiles)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress?.Report(new DeleteProgress(i, total, freed, f.Path, false));
                if (!SafetyGuard.IsPathSafeToDelete(f.Path)) { skipped++; continue; }
                try
                {
                    if (!File.Exists(f.Path)) { skipped++; continue; }
                    long size = new FileInfo(f.Path).Length;
                    if (opt.IsDestructive)
                    {
                        if (opt.SecureDelete) SecureDelete.Wipe(f.Path);
                        else File.Delete(f.Path);
                    }
                    freed += size;
                    removed++;
                }
                catch (Exception ex) { Log.Warn($"Replay '{f.Path}': {ex.Message}"); skipped++; }
            }
        }, ct);

        return (removed, skipped, freed);
    }

    // ═══════════════════════════════════════════════════════

    private static string ManifestPath(string programName)
        => Path.Combine(SnapshotDir, $"{SanitizeFilename(programName)}.manifest.json");

    private static void SaveSnapshot(InstallSnapshot snap)
    {
        var path = Path.Combine(SnapshotDir, $"{SanitizeFilename(snap.ProgramName)}_{snap.Id}.snapshot.json.gz");
        try
        {
            using var fs = File.Create(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
            JsonSerializer.Serialize(gz, snap);
        }
        catch (Exception ex) { Log.Warn($"SaveSnapshot: {ex.Message}"); }
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void PruneSnapshots(string programName)
    {
        try
        {
            var safeName = SanitizeFilename(programName);
            var dir = new DirectoryInfo(SnapshotDir);
            if (!dir.Exists) return;

            // Per-program pruning.
            var mine = dir.EnumerateFiles($"{safeName}_*.snapshot.json.gz")
                          .OrderByDescending(f => f.LastWriteTimeUtc)
                          .Skip(MaxSnapshotsPerProgram);
            foreach (var f in mine)
                try { f.Delete(); } catch { }

            // Global cap.
            var all = dir.EnumerateFiles("*.snapshot.json.gz")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(MaxTotalSnapshots);
            foreach (var f in all)
                try { f.Delete(); } catch { }
        }
        catch (Exception ex) { Log.Warn($"PruneSnapshots: {ex.Message}"); }
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name?.Length ?? 0);
        foreach (var c in name ?? "")
            sb.Append(invalid.Contains(c) ? '_' : c);
        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0) return "unknown";
        // Guard against Windows reserved device names.
        var reserved = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9" };
        if (reserved.Any(r => cleaned.Equals(r, StringComparison.OrdinalIgnoreCase))) cleaned = "_" + cleaned;
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

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

    private static void EnumerateRegKeys(string hiveName, string sub, ConcurrentBag<RegistryKeyEntry> bucket, int maxDepth, CancellationToken ct)
    {
        try
        {
            using var hive = hiveName switch
            {
                "HKLM" => Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Default),
                "HKCU" => Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser,  Microsoft.Win32.RegistryView.Default),
                _ => throw new ArgumentException(hiveName),
            };
            using var start = hive.OpenSubKey(sub);
            if (start == null) return;
            WalkKey(start, $"{hiveName}\\{sub}", bucket, 0, maxDepth, ct);
        }
        catch (Exception ex) { Log.Warn($"EnumerateRegKeys {hiveName}\\{sub}: {ex.Message}"); }
    }

    private static void WalkKey(Microsoft.Win32.RegistryKey key, string prefix, ConcurrentBag<RegistryKeyEntry> bucket, int depth, int maxDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bucket.Add(new RegistryKeyEntry(prefix));
        if (depth >= maxDepth) return;

        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch { return; }
        foreach (var name in subs)
        {
            try
            {
                using var child = key.OpenSubKey(name);
                if (child != null) WalkKey(child, prefix + "\\" + name, bucket, depth + 1, maxDepth, ct);
            }
            catch { /* perms */ }
        }
    }
}
