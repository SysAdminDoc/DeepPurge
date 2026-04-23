using DeepPurge.Core.InstallMonitor;
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
}
