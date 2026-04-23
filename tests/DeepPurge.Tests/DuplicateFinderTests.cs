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
}
