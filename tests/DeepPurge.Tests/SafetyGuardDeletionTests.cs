using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

public class SafetyGuardDeletionTests : IDisposable
{
    private readonly string _testRoot;

    public SafetyGuardDeletionTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"DeepPurge_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true); }
        catch { }
    }

    [Fact]
    public void SafeDeleteFile_Deletes_Normal_File()
    {
        var file = Path.Combine(_testRoot, "test.txt");
        File.WriteAllText(file, "data");
        Assert.True(SafetyGuard.SafeDeleteFile(file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void SafeDeleteFile_Returns_False_For_Nonexistent()
    {
        var file = Path.Combine(_testRoot, "nonexistent.txt");
        Assert.True(SafetyGuard.SafeDeleteFile(file));
    }

    [Fact]
    public void SafeDeleteFile_Returns_False_For_Null()
    {
        Assert.False(SafetyGuard.SafeDeleteFile(null!));
    }

    [Fact]
    public void SafeDeleteDirectory_Deletes_Normal_Tree()
    {
        var dir = Path.Combine(_testRoot, "subdir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(dir, "child"));
        File.WriteAllText(Path.Combine(dir, "child", "c.txt"), "c");

        Assert.True(SafetyGuard.SafeDeleteDirectory(dir));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void SafeDeleteDirectory_Returns_False_For_Nonexistent()
    {
        var dir = Path.Combine(_testRoot, "nonexistent");
        Assert.False(SafetyGuard.SafeDeleteDirectory(dir));
    }

    [Fact]
    public void SafeDeleteDirectory_Returns_False_For_Null()
    {
        Assert.False(SafetyGuard.SafeDeleteDirectory(null!));
    }

    [Fact]
    public void SafeDeleteDirectory_Returns_False_For_Empty_String()
    {
        Assert.False(SafetyGuard.SafeDeleteDirectory(""));
    }

    [Fact]
    public void SafeDeleteDirectory_Rejects_Protected_Path()
    {
        Assert.False(SafetyGuard.SafeDeleteDirectory(@"C:\Windows"));
        Assert.True(Directory.Exists(@"C:\Windows"));
    }

    [Fact]
    public void SafeEnumerateFiles_Returns_Files_In_Flat_Dir()
    {
        File.WriteAllText(Path.Combine(_testRoot, "f1.txt"), "1");
        File.WriteAllText(Path.Combine(_testRoot, "f2.log"), "2");

        var files = SafetyGuard.SafeEnumerateFiles(_testRoot).ToList();
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void SafeEnumerateFiles_Recurses_Into_Subdirectories()
    {
        var child = Path.Combine(_testRoot, "sub");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(_testRoot, "top.txt"), "t");
        File.WriteAllText(Path.Combine(child, "deep.txt"), "d");

        var files = SafetyGuard.SafeEnumerateFiles(_testRoot).ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.EndsWith("top.txt"));
        Assert.Contains(files, f => f.EndsWith("deep.txt"));
    }

    [Fact]
    public void SafeEnumerateFiles_Returns_Empty_For_Nonexistent()
    {
        var files = SafetyGuard.SafeEnumerateFiles(Path.Combine(_testRoot, "nope")).ToList();
        Assert.Empty(files);
    }

    [Fact]
    public void SafeEnumerateDirectories_Returns_Deepest_First()
    {
        var a = Path.Combine(_testRoot, "a");
        var ab = Path.Combine(a, "b");
        Directory.CreateDirectory(ab);

        var dirs = SafetyGuard.SafeEnumerateDirectories(_testRoot).ToList();
        Assert.Equal(2, dirs.Count);
        Assert.Equal(ab, dirs[0]);
        Assert.Equal(a, dirs[1]);
    }

    [Fact]
    public void SafeEnumerateDirectories_Returns_Empty_For_Nonexistent()
    {
        var dirs = SafetyGuard.SafeEnumerateDirectories(Path.Combine(_testRoot, "nope")).ToList();
        Assert.Empty(dirs);
    }

    [Fact]
    public void SafeDeleteDirectory_Handles_Empty_Directory()
    {
        var dir = Path.Combine(_testRoot, "empty");
        Directory.CreateDirectory(dir);
        Assert.True(SafetyGuard.SafeDeleteDirectory(dir));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void SafeEnumerateFiles_Applies_Pattern_Filter()
    {
        File.WriteAllText(Path.Combine(_testRoot, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_testRoot, "b.log"), "b");

        var txtOnly = SafetyGuard.SafeEnumerateFiles(_testRoot, "*.txt").ToList();
        Assert.Single(txtOnly);
        Assert.Contains(txtOnly, f => f.EndsWith("a.txt"));
    }
}
