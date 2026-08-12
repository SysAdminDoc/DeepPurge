using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

/// <summary>
/// End-to-end tests for the duplicate finder against a throwaway temp
/// directory. We deliberately do NOT mock the filesystem — the three-stage
/// hash (size → first-MB → full) is only meaningful against real streams.
/// </summary>
public class DuplicateFinderTests : IDisposable
{
    private readonly string _root;

    public DuplicateFinderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deeppurge-dupe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task Detects_identical_byte_content_as_duplicate()
    {
        var payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);

        Write("a.bin", payload);
        Write("sub/b.bin", payload);
        Write("other.bin", new byte[] { 1, 2, 3, 4, 5 }); // different size → skipped stage 1

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Paths.Count);
        Assert.Equal(payload.Length, groups[0].FileSize);
    }

    [Fact]
    public async Task Same_size_different_content_is_not_a_duplicate()
    {
        // Identical size, different content — head hash must separate them.
        var a = new byte[8 * 1024]; new Random(1).NextBytes(a);
        var b = new byte[8 * 1024]; new Random(2).NextBytes(b);
        Write("a.bin", a);
        Write("b.bin", b);

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task Files_above_head_chunk_size_still_classify_correctly()
    {
        // 2 MB identical → forces stage 3 (full-file hash).
        var payload = new byte[2 * 1024 * 1024];
        new Random(7).NextBytes(payload);
        Write("big1.bin", payload);
        Write("big2.bin", payload);

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Paths.Count);
        Assert.True(groups[0].WastedBytes == payload.Length);
    }

    [Fact]
    public async Task Unique_sizes_are_dropped_without_hashing()
    {
        Write("a.bin", new byte[100]);
        Write("b.bin", new byte[200]);
        Write("c.bin", new byte[300]);

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);

        Assert.Empty(groups);
    }

    [Fact]
    public void WastedBytes_is_zero_for_singleton_groups()
    {
        var g = new DuplicateGroup { FileSize = 1000, Paths = new List<string> { "x" } };
        Assert.Equal(0, g.WastedBytes);
    }

    [Fact]
    public void WastedBytes_scales_with_copies()
    {
        var g = new DuplicateGroup
        {
            FileSize = 1000,
            Paths = new List<string> { "a", "b", "c", "d" }, // 4 copies
        };
        Assert.Equal(3000, g.WastedBytes); // keep 1, 3 copies wasted
    }

    [Fact]
    public void DeleteDuplicates_skips_safety_protected_paths()
    {
        // Synthetic group: one real temp file + one impossible system path.
        // The impossible path must be skipped by SafetyGuard.
        var realFile = Write("real.bin", new byte[] { 1, 2, 3 });
        var group = new DuplicateGroup
        {
            FileSize = 3,
            Paths = new List<string> { realFile, @"C:\Windows\System32\kernel32.dll" },
        };

        var finder = new DuplicateFinder();
        var deleted = finder.DeleteDuplicates(new[] { group }, DeleteOptions.Default, keepNewest: true);

        // Only the real file can be a candidate; System32 is blocked.
        // keepNewest=true keeps realFile (newer), tries to delete kernel32.dll,
        // which SafetyGuard rejects → 0 deletes.
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(realFile)); // real file untouched
    }

    [Fact]
    public async Task Delete_requires_an_explicit_keeper()
    {
        var payload = new byte[12 * 1024];
        new Random(81).NextBytes(payload);
        Write("a.bin", payload);
        Write("b.bin", payload);

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);
        var summary = finder.DeleteDuplicatesDetailed(
            groups,
            new DeleteOptions(UseRecycleBin: false),
            new DuplicateKeeperPolicy());

        Assert.Equal(0, summary.ItemsDeleted);
        Assert.Equal(2, summary.ItemsSkipped);
        Assert.All(groups[0].Paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Changed_content_aborts_the_entire_group()
    {
        var payload = new byte[12 * 1024];
        new Random(82).NextBytes(payload);
        var keeper = Write("a.bin", payload);
        var victim = Write("b.bin", payload);

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);
        var group = Assert.Single(groups);
        group.KeeperPath = keeper;

        payload[0] ^= 0xFF;
        File.WriteAllBytes(victim, payload);

        var summary = finder.DeleteDuplicatesDetailed(
            groups,
            new DeleteOptions(UseRecycleBin: false),
            new DuplicateKeeperPolicy());

        Assert.Equal(0, summary.ItemsDeleted);
        Assert.True(summary.ItemsSkipped >= 1);
        Assert.True(File.Exists(keeper));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task Explicit_keeper_remains_while_other_copy_is_removed()
    {
        var payload = new byte[12 * 1024];
        new Random(83).NextBytes(payload);
        var keeper = Write("a.bin", payload);
        var victim = Write("b.bin", payload);

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);
        var group = Assert.Single(groups);
        group.KeeperPath = keeper;

        var summary = finder.DeleteDuplicatesDetailed(
            groups,
            new DeleteOptions(UseRecycleBin: false),
            new DuplicateKeeperPolicy());

        Assert.Equal(1, summary.ItemsDeleted);
        Assert.Equal(0, summary.ItemsFailed);
        Assert.True(File.Exists(keeper));
        Assert.False(File.Exists(victim));
    }

    [Fact]
    public async Task Reference_folder_protects_a_copy_without_group_selection()
    {
        var reference = Path.Combine(_root, "reference");
        Directory.CreateDirectory(reference);
        var payload = new byte[12 * 1024];
        new Random(84).NextBytes(payload);
        var keeper = Write("reference/a.bin", payload);
        var victim = Write("other/b.bin", payload);

        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(new[] { _root }, minBytes: 0);
        var summary = finder.DeleteDuplicatesDetailed(
            groups,
            new DeleteOptions(UseRecycleBin: false),
            new DuplicateKeeperPolicy(reference));

        Assert.Equal(1, summary.ItemsDeleted);
        Assert.True(File.Exists(keeper));
        Assert.False(File.Exists(victim));
    }
}
