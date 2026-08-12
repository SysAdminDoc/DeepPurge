using System.Collections.Concurrent;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.InstallMonitor;

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
    private const int MaxTotalSnapshots = 30;
    private const long MaxReplayHashBytes = 256L * 1024 * 1024;

    private static readonly string[] FsRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
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
        var keysBag = new ConcurrentBag<RegistryKeyEntry>();

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
                    catch (Exception ex) { Log.Warn($"Snapshot file info '{file}': {ex.Message}"); }
                }
            }, ct))
            .ToArray();

        var regTasks = RegRoots
            .Select(t => Task.Run(() => EnumerateRegKeys(t.Hive, t.Sub, keysBag, maxDepth: 3, ct), ct))
            .ToArray();

        await Task.WhenAll(fsTasks.Concat(regTasks));

        snap.Files = filesBag.ToList();
        snap.RegistryKeys = keysBag.ToList();

        SaveSnapshot(snap);
        PruneSnapshots(programName);
        return snap;
    }

    public InstallDelta Diff(InstallSnapshot before, InstallSnapshot after)
    {
        var beforeByPath = before.Files.ToDictionary(
            f => f.Path,
            StringComparer.OrdinalIgnoreCase);
        var afterByPath = after.Files.ToDictionary(
            f => f.Path,
            StringComparer.OrdinalIgnoreCase);
        var beforeKeys = new HashSet<string>(before.RegistryKeys.Select(k => k.Path), StringComparer.OrdinalIgnoreCase);
        var afterKeys = new HashSet<string>(after.RegistryKeys.Select(k => k.Path), StringComparer.OrdinalIgnoreCase);

        var delta = new InstallDelta();
        foreach (var f in after.Files)
        {
            if (!beforeByPath.TryGetValue(f.Path, out var prior))
            {
                delta.AddedFiles.Add(StampReplayIdentity(
                    f with { ChangeKind = InstallObjectChangeKind.Created }));
            }
            else if (HasChanged(prior, f))
            {
                delta.ModifiedFiles.Add(
                    f with { ChangeKind = InstallObjectChangeKind.Modified });
            }
        }
        foreach (var k in after.RegistryKeys) if (!beforeKeys.Contains(k.Path)) delta.AddedRegistryKeys.Add(k.Path);
        foreach (var f in before.Files) if (!afterByPath.ContainsKey(f.Path)) delta.RemovedFiles.Add(f.Path);
        foreach (var k in before.RegistryKeys) if (!afterKeys.Contains(k.Path)) delta.RemovedRegistryKeys.Add(k.Path);
        return delta;
    }

    private static bool HasChanged(SnapshotEntry before, SnapshotEntry after)
        => before.SizeBytes != after.SizeBytes ||
           before.LastWriteUtc != after.LastWriteUtc ||
           (before.VolumeSerialNumber.HasValue &&
            after.VolumeSerialNumber.HasValue &&
            before.VolumeSerialNumber != after.VolumeSerialNumber) ||
           (before.FileIndex.HasValue &&
            after.FileIndex.HasValue &&
            before.FileIndex != after.FileIndex) ||
           (!string.IsNullOrWhiteSpace(before.Sha256) &&
            !string.IsNullOrWhiteSpace(after.Sha256) &&
            !string.Equals(before.Sha256, after.Sha256, StringComparison.OrdinalIgnoreCase));

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

        var traceStartedUtc = DateTime.UtcNow;
        var before = await CaptureAsync(programName, installerPath, ct);
        var installerIdentity = CaptureInstallerIdentity(installerPath);
        var run = await RunInstallerAsync(installerPath, installerArgs, ct);
        var after = await CaptureAsync(programName, installerPath, ct);
        var delta = Diff(before, after);
        SaveManifest(programName, BuildManifest(
            programName,
            installerIdentity,
            delta,
            InstallTraceMode.PrePostSnapshot,
            traceStartedUtc,
            run.EndedAtUtc,
            run.ProcessId,
            diagnostics: null));
        return delta;
    }

    public void SaveManifest(string programName, InstallDelta delta)
    {
        SaveManifest(
            programName,
            BuildManifest(
                programName,
                installer: null,
                delta,
                InstallTraceMode.Unknown,
                DateTime.MinValue,
                DateTime.MinValue,
                0,
                diagnostics: null));
    }

    public void SaveManifest(string programName, InstallManifest manifest)
    {
        var path = ManifestPath(programName);
        manifest.ProgramName = programName;
        manifest.SchemaVersion = InstallManifestSchema.Current;
        var json = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });
        AtomicWrite(path, json);
    }

    public InstallDelta? LoadManifest(string programName)
    {
        var manifest = LoadInstallManifest(programName);
        return manifest?.ReplayEligible == true ? manifest.Delta : null;
    }

    public InstallManifest? LoadInstallManifest(string programName)
    {
        var path = ManifestPath(programName);
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("SchemaVersion", out var schemaElement))
            {
                if (!schemaElement.TryGetInt32(out var schemaVersion) ||
                    schemaVersion != InstallManifestSchema.Current)
                {
                    Log.Warn($"LoadManifest '{path}': unsupported schema version.");
                    return null;
                }
                var manifest = document.RootElement.Deserialize<InstallManifest>();
                if (manifest == null) return null;
                manifest.LoadedFromTrustedStore = true;
                return manifest;
            }

            // Legacy delta files are retained for inspection only. They lack
            // launch identity, created-vs-modified provenance, and a replay
            // eligibility decision, so they must never drive deletion.
            var legacyDelta = document.RootElement.Deserialize<InstallDelta>();
            if (legacyDelta == null) return null;
            return new InstallManifest
            {
                SchemaVersion = 1,
                ProgramName = programName,
                TraceMode = InstallTraceMode.Unknown,
                ReplayEligible = false,
                ReplayEligibilityReason = "Legacy manifest lacks trusted trace provenance.",
                Delta = legacyDelta,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"LoadManifest '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Remove every file in a saved install manifest. SafetyGuard is applied
    /// per-item, so protected paths are skipped with a count.
    /// </summary>
    public async Task<InstallReplayResult> ReplayRemoveAsync(
        InstallDelta delta,
        DeleteOptions opt,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
        => await ReplayRemoveCoreAsync(
            delta,
            opt,
            progress,
            ct,
            requireCreatedEvidence: true);

    public async Task<InstallReplayResult> ReplayRemoveAsync(
        InstallManifest manifest,
        DeleteOptions opt,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!manifest.ReplayEligible)
        {
            return new InstallReplayResult(
                0,
                manifest.Delta.AddedFiles.Count,
                0,
                new[] { manifest.ReplayEligibilityReason },
                manifest.ReplayEligibilityReason);
        }

        return await ReplayRemoveCoreAsync(
            manifest.Delta,
            opt,
            progress,
            ct,
            requireCreatedEvidence: true);
    }

    private static async Task<InstallReplayResult> ReplayRemoveCoreAsync(
        InstallDelta delta,
        DeleteOptions opt,
        IProgress<DeleteProgress>? progress,
        CancellationToken ct,
        bool requireCreatedEvidence)
    {
        int removed = 0, skipped = 0;
        long freed = 0;
        int total = delta.AddedFiles.Count, i = 0;
        var skippedReasons = new ConcurrentBag<string>();

        await Task.Run(() =>
        {
            foreach (var f in delta.AddedFiles)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress?.Report(new DeleteProgress(i, total, freed, f.Path, false));
                if (!SafetyGuard.IsPathSafeToDelete(f.Path))
                {
                    skipped++;
                    skippedReasons.Add($"Unsafe path: {f.Path}");
                    continue;
                }
                if (requireCreatedEvidence &&
                    f.ChangeKind != InstallObjectChangeKind.Created)
                {
                    skipped++;
                    skippedReasons.Add(
                        $"Not created by the traced installer: {f.Path}");
                    continue;
                }
                try
                {
                    if (!File.Exists(f.Path))
                    {
                        skipped++;
                        skippedReasons.Add($"Missing: {f.Path}");
                        continue;
                    }
                    var fi = new FileInfo(f.Path);
                    var skipReason = GetReplaySkipReason(f, fi);
                    if (skipReason != null)
                    {
                        skipped++;
                        skippedReasons.Add($"{skipReason}: {f.Path}");
                        continue;
                    }

                    long size = fi.Length;
                    if (opt.IsDestructive)
                    {
                        var deleted = opt.SecureDelete
                            ? SecureDelete.Wipe(f.Path)
                            : SafetyGuard.SafeDeleteFile(f.Path);
                        if (!deleted)
                        {
                            skipped++;
                            skippedReasons.Add($"Delete failed: {f.Path}");
                            continue;
                        }
                    }
                    freed += size;
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Replay '{f.Path}': {ex.Message}");
                    skipped++;
                    skippedReasons.Add($"Error: {f.Path} ({ex.Message})");
                }
            }
        }, ct);

        return new InstallReplayResult(
            removed,
            skipped,
            freed,
            skippedReasons.ToArray());
    }

    // ═══════════════════════════════════════════════════════
    //  USN JOURNAL TRACE (v2 — catches every filesystem change)
    // ═══════════════════════════════════════════════════════

    public async Task<InstallDelta> TraceInstallV2Async(
        string programName,
        string installerPath,
        string? installerArgs = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(programName))
            throw new ArgumentException("programName is required", nameof(programName));
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            throw new FileNotFoundException("installer not found", installerPath);

        var traceStartedUtc = DateTime.UtcNow;
        var volumeRoot = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ?? @"C:\";
        var installerIdentity = CaptureInstallerIdentity(installerPath);
        var before = await CaptureAsync(programName, installerPath, ct);
        var startUsn = UsnJournalReader.GetCurrentUsn(volumeRoot);
        var useUsn = startUsn >= 0;
        var useSysmon = SysmonReader.IsAvailable();
        var run = await RunInstallerAsync(installerPath, installerArgs, ct);
        var after = await CaptureAsync(programName, installerPath, ct);
        var delta = Diff(before, after);

        var diagnostics = new InstallTraceDiagnostics
        {
            WindowStartedUtc = traceStartedUtc,
            WindowEndedUtc = run.EndedAtUtc,
            InstallerProcessId = run.ProcessId,
            InstallerImage = installerPath,
            InstallerIdentityCaptured = installerIdentity != null,
            UsnAvailable = useUsn,
            SysmonAvailable = useSysmon,
            UsnAttribution =
                "USN records are diagnostic-only; pre/post snapshots determine replay eligibility.",
        };

        if (useUsn)
        {
            try
            {
                diagnostics.FileChanges = UsnJournalReader
                    .ReadChangesSince(volumeRoot, startUsn)
                    .Where(c => c.TimestampUtc >= traceStartedUtc &&
                                c.TimestampUtc <= run.EndedAtUtc)
                    .ToList();
                if (diagnostics.FileChanges.Any(c => !c.PathResolved))
                {
                    diagnostics.Warnings.Add(
                        "Some USN records could not be resolved through their parent FRNs.");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Warnings.Add($"USN diagnostics unavailable: {ex.Message}");
            }
        }
        else
        {
            diagnostics.Warnings.Add(
                "USN journal unavailable; authoritative pre/post snapshot diff used.");
        }

        if (useSysmon && run.ProcessId > 0)
        {
            try
            {
                diagnostics.RegistryChanges = SysmonReader
                    .ReadCorrelatedRegistryChanges(
                        traceStartedUtc,
                        run.EndedAtUtc,
                        run.ProcessId,
                        installerPath,
                        out var correlated)
                    .Where(c => c.TimeCreated >= traceStartedUtc &&
                                c.TimeCreated <= run.EndedAtUtc)
                    .ToList();
                diagnostics.SysmonProcessTreeCorrelated = correlated;
                if (!correlated)
                {
                    diagnostics.Warnings.Add(
                        "Sysmon was present but the installer process tree could not be proven; registry events remain diagnostic-only.");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Warnings.Add($"Sysmon diagnostics unavailable: {ex.Message}");
            }
        }
        else if (useSysmon)
        {
            diagnostics.Warnings.Add(
                "Sysmon was present but no installer process identity was captured.");
        }

        SaveManifest(programName, BuildManifest(
            programName,
            installerIdentity,
            delta,
            InstallTraceMode.PrePostSnapshotWithDiagnostics,
            traceStartedUtc,
            run.EndedAtUtc,
            run.ProcessId,
            diagnostics));
        return delta;
    }

    private sealed record InstallerRun(int ProcessId, DateTime EndedAtUtc);

    private static async Task<InstallerRun> RunInstallerAsync(
        string installerPath,
        string? installerArgs,
        CancellationToken ct)
    {
        var endedAtUtc = DateTime.UtcNow;
        var processId = 0;
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = installerArgs ?? "",
            UseShellExecute = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process != null)
            {
                processId = process.Id;
                await process.WaitForExitAsync(ct);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    // Capture the post-state even when the caller cancels the
                    // installer wait; the trace is explicitly partial.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Installer launch failed: {ex.Message}");
        }

        endedAtUtc = DateTime.UtcNow;
        return new InstallerRun(processId, endedAtUtc);
    }

    private static InstallManifest BuildManifest(
        string programName,
        InstallerIdentity? installer,
        InstallDelta delta,
        InstallTraceMode traceMode,
        DateTime traceStartedUtc,
        DateTime traceEndedUtc,
        int installerProcessId,
        InstallTraceDiagnostics? diagnostics)
    {
        var replayable = installer != null &&
                         traceMode != InstallTraceMode.Unknown &&
                         delta.AddedFiles.All(IsReplayEligibleEntry);
        var reason = replayable
            ? "Only created files with captured replay identity are eligible."
            : installer == null
                ? "Installer identity was not captured before launch."
                : traceMode == InstallTraceMode.Unknown
                    ? "Manifest was not produced by an authoritative trace."
                    : "One or more added files lack created-object identity.";

        diagnostics ??= new InstallTraceDiagnostics
        {
            WindowStartedUtc = traceStartedUtc,
            WindowEndedUtc = traceEndedUtc,
            InstallerProcessId = installerProcessId,
            InstallerImage = installer?.Path ?? "",
            InstallerIdentityCaptured = installer != null,
        };

        return new InstallManifest
        {
            SchemaVersion = InstallManifestSchema.Current,
            ProgramName = programName,
            TraceMode = traceMode,
            TraceStartedUtc = traceStartedUtc,
            TraceEndedUtc = traceEndedUtc,
            Installer = installer,
            ReplayEligible = replayable,
            ReplayEligibilityReason = reason,
            Delta = delta,
            Diagnostics = diagnostics,
        };
    }

    private static bool IsReplayEligibleEntry(SnapshotEntry entry)
        => entry.HasStableIdentity &&
           !string.IsNullOrWhiteSpace(entry.Sha256) &&
           entry.SizeBytes >= 0;

    private static InstallerIdentity? CaptureInstallerIdentity(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var hash = TryComputeSha256(file);
            if (string.IsNullOrWhiteSpace(hash)) return null;

            uint volumeSerial = 0;
            ulong fileIndex = 0;
            try
            {
                using var stream = file.Open(
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (HandleBoundFileOperations.TryReadMetadata(
                        stream.SafeFileHandle,
                        out var objectIdentity,
                        out _,
                        out _,
                        out _))
                {
                    volumeSerial = objectIdentity.VolumeSerialNumber;
                    fileIndex = objectIdentity.FileIndex;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Installer object identity '{path}': {ex.Message}");
            }

            return new InstallerIdentity(
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc,
                hash,
                volumeSerial,
                fileIndex);
        }
        catch (Exception ex)
        {
            Log.Warn($"Installer identity '{path}': {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════

    private static SnapshotEntry StampReplayIdentity(SnapshotEntry entry)
    {
        try
        {
            var fi = new FileInfo(entry.Path);
            if (!fi.Exists) return entry;
            var stamped = entry with
            {
                SizeBytes = fi.Length,
                LastWriteUtc = fi.LastWriteTimeUtc,
                Sha256 = TryComputeSha256(fi),
            };

            try
            {
                using var stream = fi.Open(
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (HandleBoundFileOperations.TryReadMetadata(
                        stream.SafeFileHandle,
                        out var identity,
                        out _,
                        out _,
                        out _))
                {
                    stamped = stamped with
                    {
                        VolumeSerialNumber = identity.VolumeSerialNumber,
                        FileIndex = identity.FileIndex,
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Snapshot object identity '{entry.Path}': {ex.Message}");
            }

            return stamped;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snapshot replay identity '{entry.Path}': {ex.Message}");
            return entry;
        }
    }

    private static string? GetReplaySkipReason(SnapshotEntry expected, FileInfo current)
    {
        current.Refresh();
        if (!current.Exists) return "Missing";
        if (current.Length != expected.SizeBytes)
        {
            Log.Warn($"Replay skipped changed file '{expected.Path}': size changed from {expected.SizeBytes} to {current.Length}");
            return "Size changed";
        }

        if (expected.VolumeSerialNumber.HasValue &&
            expected.FileIndex.HasValue)
        {
            try
            {
                using var stream = current.Open(
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (!HandleBoundFileOperations.TryReadMetadata(
                        stream.SafeFileHandle,
                        out var identity,
                        out _,
                        out _,
                        out _) ||
                    identity.VolumeSerialNumber != expected.VolumeSerialNumber ||
                    identity.FileIndex != expected.FileIndex)
                {
                    return "Filesystem identity changed";
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Replay identity '{expected.Path}': {ex.Message}");
                return "Filesystem identity unavailable";
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.Sha256))
        {
            var currentHash = TryComputeSha256(current);
            if (!string.Equals(currentHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Replay skipped changed file '{expected.Path}': SHA256 mismatch");
                return "SHA256 mismatch";
            }
            return null;
        }

        var delta = (current.LastWriteTimeUtc - expected.LastWriteUtc).Duration();
        if (delta > TimeSpan.FromSeconds(2))
        {
            Log.Warn($"Replay skipped changed file '{expected.Path}': last-write time changed");
            return "Last-write time changed";
        }
        return null;
    }

    private static string? TryComputeSha256(FileInfo file)
    {
        if (file.Length > MaxReplayHashBytes) return null;

        try
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Log.Warn($"SHA256 '{file.FullName}': {ex.Message}");
            return null;
        }
    }

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
                try { f.Delete(); } catch (Exception ex) { Log.Warn($"Prune per-program snapshot '{f.Name}': {ex.Message}"); }

            // Global cap.
            var all = dir.EnumerateFiles("*.snapshot.json.gz")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(MaxTotalSnapshots);
            foreach (var f in all)
                try { f.Delete(); } catch (Exception ex) { Log.Warn($"Prune global snapshot '{f.Name}': {ex.Message}"); }
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
                dirs = Directory.GetDirectories(cur);
            }
            catch (Exception ex) { Log.Warn($"Enumerate directory '{cur}': {ex.Message}"); continue; }
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
                "HKCU" => Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
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
        try { subs = key.GetSubKeyNames(); } catch (Exception ex) { Log.Warn($"Registry subkey enumeration '{prefix}': {ex.Message}"); return; }
        foreach (var name in subs)
        {
            try
            {
                using var child = key.OpenSubKey(name);
                if (child != null) WalkKey(child, prefix + "\\" + name, bucket, depth + 1, maxDepth, ct);
            }
            catch (Exception ex) { Log.Warn($"Registry open subkey '{prefix}\\{name}': {ex.Message}"); }
        }
    }
}
