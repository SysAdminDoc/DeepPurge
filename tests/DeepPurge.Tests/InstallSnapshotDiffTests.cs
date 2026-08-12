using DeepPurge.Core.InstallMonitor;
using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// Diff operates on pure data — no FS or registry calls — so tests can be
/// synthesised entirely in-memory. Locks in the add/remove partitioning
/// that was expanded during the hardening pass.
/// </summary>
public class InstallSnapshotDiffTests
{
    private static InstallSnapshot Snap(IEnumerable<string>? files = null, IEnumerable<string>? keys = null)
        => new()
        {
            ProgramName = "test",
            Files = (files ?? Array.Empty<string>())
                .Select(p => new SnapshotEntry(p, 100, DateTime.UtcNow)).ToList(),
            RegistryKeys = (keys ?? Array.Empty<string>())
                .Select(k => new RegistryKeyEntry(k)).ToList(),
        };

    [Fact]
    public void Detects_added_files_and_keys()
    {
        var before = Snap(files: new[] { @"C:\A" });
        var after  = Snap(
            files: new[] { @"C:\A", @"C:\B", @"C:\C" },
            keys:  new[] { @"HKLM\X", @"HKCU\Y" });

        var d = new InstallSnapshotEngine().Diff(before, after);

        Assert.Equal(2, d.AddedFiles.Count);
        Assert.Contains(d.AddedFiles, f => f.Path == @"C:\B");
        Assert.Contains(d.AddedFiles, f => f.Path == @"C:\C");
        Assert.Equal(2, d.AddedRegistryKeys.Count);
    }

    [Fact]
    public void Detects_removed_files_and_keys()
    {
        var before = Snap(files: new[] { @"C:\A", @"C:\B" }, keys: new[] { @"HKLM\X" });
        var after  = Snap(files: new[] { @"C:\A" });

        var d = new InstallSnapshotEngine().Diff(before, after);

        Assert.Single(d.RemovedFiles);
        Assert.Equal(@"C:\B", d.RemovedFiles[0]);
        Assert.Single(d.RemovedRegistryKeys);
        Assert.Equal(@"HKLM\X", d.RemovedRegistryKeys[0]);
    }

    [Fact]
    public void No_changes_yields_empty_delta()
    {
        var s = Snap(files: new[] { @"C:\A" }, keys: new[] { @"HKLM\X" });
        var d = new InstallSnapshotEngine().Diff(s, s);

        Assert.Empty(d.AddedFiles);
        Assert.Empty(d.RemovedFiles);
        Assert.Empty(d.AddedRegistryKeys);
        Assert.Empty(d.RemovedRegistryKeys);
    }

    [Fact]
    public void TotalAddedBytes_sums_added_sizes()
    {
        var after = new InstallSnapshot
        {
            Files = new()
            {
                new SnapshotEntry(@"C:\A", 1000, DateTime.UtcNow),
                new SnapshotEntry(@"C:\B", 2000, DateTime.UtcNow),
                new SnapshotEntry(@"C:\C", 500, DateTime.UtcNow),
            },
        };
        var d = new InstallSnapshotEngine().Diff(new InstallSnapshot(), after);
        Assert.Equal(3500, d.TotalAddedBytes);
    }

    [Fact]
    public void Path_comparison_is_case_insensitive()
    {
        var before = Snap(files: new[] { @"C:\PROGRAM FILES\app.exe" });
        var after  = Snap(files: new[] { @"c:\program files\app.exe" });

        var d = new InstallSnapshotEngine().Diff(before, after);

        Assert.Empty(d.AddedFiles);
        Assert.Empty(d.RemovedFiles);
    }

    [Fact]
    public void Registry_key_comparison_is_case_insensitive()
    {
        var before = Snap(keys: new[] { @"HKLM\SOFTWARE\Vendor\App" });
        var after  = Snap(keys: new[] { @"hklm\software\vendor\app" });

        var d = new InstallSnapshotEngine().Diff(before, after);

        Assert.Empty(d.AddedRegistryKeys);
        Assert.Empty(d.RemovedRegistryKeys);
    }

    [Fact]
    public void Diff_stamps_added_files_with_replay_hash_when_file_exists()
    {
        var root = NewTempDir();
        try
        {
            var file = Path.Combine(root, "added.bin");
            File.WriteAllText(file, "original");
            var info = new FileInfo(file);

            var before = new InstallSnapshot();
            var after = new InstallSnapshot
            {
                Files = new() { new SnapshotEntry(file, info.Length, info.LastWriteTimeUtc) },
            };

            var d = new InstallSnapshotEngine().Diff(before, after);

            var entry = Assert.Single(d.AddedFiles);
            Assert.False(string.IsNullOrWhiteSpace(entry.Sha256));
            Assert.Equal(info.Length, entry.SizeBytes);
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public async Task ReplayRemoveAsync_deletes_unchanged_manifest_file()
    {
        var root = NewTempDir();
        try
        {
            var file = Path.Combine(root, "unchanged.bin");
            File.WriteAllText(file, "original");
            var delta = BuildDeltaFor(file);

            var result = await new InstallSnapshotEngine()
                .ReplayRemoveAsync(
                    delta,
                    new DeleteOptions(DryRun: false, UseRecycleBin: false),
                    ct: TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Removed);
            Assert.Equal(0, result.Skipped);
            Assert.False(File.Exists(file));
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public async Task ReplayRemoveAsync_skips_changed_manifest_file()
    {
        var root = NewTempDir();
        try
        {
            var file = Path.Combine(root, "changed.bin");
            File.WriteAllText(file, "original");
            var delta = BuildDeltaFor(file);
            File.WriteAllText(file, "modified");

            var result = await new InstallSnapshotEngine()
                .ReplayRemoveAsync(
                    delta,
                    new DeleteOptions(DryRun: false, UseRecycleBin: false),
                    ct: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.Skipped);
            Assert.Contains(result.SkippedReasons, r => r.Contains("SHA256 mismatch"));
            Assert.True(File.Exists(file));
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public async Task ReplayRemoveAsync_skips_missing_manifest_file()
    {
        var root = NewTempDir();
        try
        {
            var file = Path.Combine(root, "missing.bin");
            File.WriteAllText(file, "original");
            var delta = BuildDeltaFor(file);
            File.Delete(file);

            var result = await new InstallSnapshotEngine()
                .ReplayRemoveAsync(
                    delta,
                    new DeleteOptions(DryRun: false, UseRecycleBin: false),
                    ct: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.Skipped);
            Assert.Contains(result.SkippedReasons, r => r.Contains("Missing"));
        }
        finally { TryDeleteDir(root); }
    }

    private static InstallDelta BuildDeltaFor(string file)
    {
        var info = new FileInfo(file);
        var after = new InstallSnapshot
        {
            Files = new() { new SnapshotEntry(file, info.Length, info.LastWriteTimeUtc) },
        };
        return new InstallSnapshotEngine().Diff(new InstallSnapshot(), after);
    }

    private static string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "deeppurge-install-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    [Fact]
    public void Diff_classifies_existing_changed_file_as_modified_not_created()
    {
        var before = new InstallSnapshot
        {
            Files = new() { new SnapshotEntry(@"C:\app.exe", 10, DateTime.UtcNow) },
        };
        var after = new InstallSnapshot
        {
            Files = new() { new SnapshotEntry(@"C:\app.exe", 20, DateTime.UtcNow.AddSeconds(1)) },
        };

        var delta = new InstallSnapshotEngine().Diff(before, after);

        Assert.Empty(delta.AddedFiles);
        var modified = Assert.Single(delta.ModifiedFiles);
        Assert.Equal(InstallObjectChangeKind.Modified, modified.ChangeKind);
    }

    [Fact]
    public void Legacy_delta_manifest_is_diagnostic_only()
    {
        var name = $"legacy-{Guid.NewGuid():N}";
        var path = Path.Combine(
            DeepPurge.Core.App.DataPaths.Snapshots,
            $"{name}.manifest.json");
        try
        {
            File.WriteAllText(
                path,
                "{\"AddedFiles\":[{\"Path\":\"C:\\\\old.exe\",\"SizeBytes\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\"}]}");

            var engine = new InstallSnapshotEngine();
            var manifest = engine.LoadInstallManifest(name);

            Assert.NotNull(manifest);
            Assert.False(manifest.ReplayEligible);
            Assert.Null(engine.LoadManifest(name));
            Assert.Contains("Legacy", manifest.ReplayEligibilityReason);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    [Fact]
    public async Task ReplayRemoveAsync_skips_entries_without_created_provenance()
    {
        var root = NewTempDir();
        try
        {
            var file = Path.Combine(root, "modified.bin");
            File.WriteAllText(file, "keep");
            var info = new FileInfo(file);
            var delta = new InstallDelta
            {
                AddedFiles = new()
                {
                    new SnapshotEntry(
                        file,
                        info.Length,
                        info.LastWriteTimeUtc,
                        ChangeKind: InstallObjectChangeKind.Modified),
                },
            };

            var result = await new InstallSnapshotEngine().ReplayRemoveAsync(
                delta,
                new DeleteOptions(DryRun: false, UseRecycleBin: false),
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.Skipped);
            Assert.Contains(result.SkippedReasons, reason =>
                reason.Contains("Not created", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(file));
        }
        finally { TryDeleteDir(root); }
    }
}
