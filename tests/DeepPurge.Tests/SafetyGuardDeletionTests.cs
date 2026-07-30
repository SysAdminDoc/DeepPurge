using System.Diagnostics;
using System.Text.RegularExpressions;
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
    public void SafeDeleteDirectory_Rejects_Root_Junction_And_Preserves_Target()
    {
        var outside = Path.Combine(_testRoot, "outside-root");
        var junction = Path.Combine(_testRoot, "root-link");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve");
        CreateJunction(junction, outside);

        try
        {
            Assert.False(SafetyGuard.SafeDeleteDirectory(junction));
            Assert.Equal("preserve", File.ReadAllText(sentinel));
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    [Fact]
    public void SafeDeleteDirectory_Aborts_On_Child_Junction_And_Preserves_Outside_Sentinel()
    {
        var candidate = Path.Combine(_testRoot, "candidate");
        var outside = Path.Combine(_testRoot, "outside-child");
        var junction = Path.Combine(candidate, "redirect");
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(candidate, "ordinary.txt"), "delete only after validation");
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve");
        CreateJunction(junction, outside);

        try
        {
            Assert.False(SafetyGuard.SafeDeleteDirectory(candidate));
            Assert.Equal("preserve", File.ReadAllText(sentinel));
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            RemoveJunction(junction);
        }
    }

    [Fact]
    public void Pinned_Delete_Handle_Blocks_Deterministic_Target_Swap()
    {
        var victim = Path.Combine(_testRoot, "victim.txt");
        var outsideSentinel = Path.Combine(_testRoot, "outside-sentinel.txt");
        File.WriteAllText(victim, "victim");
        File.WriteAllText(outsideSentinel, "preserve");

        var scope = FileOperationScope.Exact(victim);
        Assert.True(
            HandleBoundFileOperations.TryOpenForDeletion(
                victim,
                expectedDirectory: false,
                scope,
                out var target,
                out var reason),
            reason);

        using (target)
        {
            var swapFailure = Record.Exception(
                () => File.Move(outsideSentinel, victim, overwrite: true));
            Assert.NotNull(swapFailure);
            Assert.True(File.Exists(victim));
            Assert.Equal("preserve", File.ReadAllText(outsideSentinel));
            Assert.True(target!.Revalidate(out reason), reason);
            Assert.True(target.TryDelete(out reason), reason);
        }

        Assert.False(File.Exists(victim));
        Assert.Equal("preserve", File.ReadAllText(outsideSentinel));
    }

    [Fact]
    public void Handle_Open_Rejects_Target_Outside_Operation_Scope()
    {
        var approved = Path.Combine(_testRoot, "approved");
        var outside = Path.Combine(_testRoot, "outside.txt");
        Directory.CreateDirectory(approved);
        File.WriteAllText(outside, "preserve");

        var opened = HandleBoundFileOperations.TryOpenForDeletion(
            outside,
            expectedDirectory: false,
            FileOperationScope.Tree(approved),
            out var target,
            out _);

        Assert.False(opened);
        Assert.Null(target);
        Assert.Equal("preserve", File.ReadAllText(outside));
    }

    [Fact]
    public void Core_Source_Has_No_Path_Based_Delete_Primitives()
    {
        var sourceRoot = Path.Combine(FindRepoRoot(), "src");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                @"\b(?:File|Directory)\.Delete\s*\("))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToList();

        Assert.Empty(offenders);
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

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            Arguments = $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(10_000), "mklink did not finish.");
        Assert.True(
            process.ExitCode == 0,
            $"mklink failed: {process.StandardError.ReadToEnd()}");
    }

    private static void RemoveJunction(string junctionPath)
    {
        try
        {
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath, recursive: false);
        }
        catch { }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeepPurge.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DeepPurge repository root.");
    }
}
