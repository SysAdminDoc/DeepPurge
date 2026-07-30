using System.Reflection;
using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

public class SecureDeleteTests
{
    [Fact]
    public void SecureDelete_does_not_expose_free_space_wipe_api()
    {
        var method = typeof(SecureDelete).GetMethod(
            "WipeFreeSpaceAsync",
            BindingFlags.Public | BindingFlags.Static);

        Assert.Null(method);
    }

    [Fact]
    public void Wipe_Deletes_The_Pinned_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-secure-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 128 * 1024).ToArray());

        Assert.True(SecureDelete.Wipe(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WipeDirectory_Deletes_A_Normal_Tree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dp-secure-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "child"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "child", "b.txt"), "b");

        Assert.True(SecureDelete.WipeDirectory(root));
        Assert.False(Directory.Exists(root));
    }
}
