using System.Reflection;
using DeepPurge.Core.FileSystem;
using Xunit;

namespace DeepPurge.Tests;

public class VolumeFileSystemTests
{
    [Fact]
    public void GetForPath_returns_filesystem_for_temp_volume()
    {
        var info = VolumeFileSystem.GetForPath(Path.GetTempPath());

        Assert.False(string.IsNullOrWhiteSpace(info.RootPath));
        Assert.False(string.IsNullOrWhiteSpace(info.FileSystemName));
        Assert.Equal(!info.IsNtfs, info.UsesFallbackEnumeration);
    }

    [Theory]
    [InlineData(@"C:\Windows\Installer\a.msi", true)]
    [InlineData(@"C:\Windows\Installer\a.msp", true)]
    [InlineData(@"C:\Windows\Installer\a.txt", false)]
    public void WindowsInstallerPackage_helper_matches_msi_and_msp(string path, bool expected)
    {
        var helper = typeof(JunkFilesCleaner).GetMethod(
            "IsWindowsInstallerPackage",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var actual = (bool)helper.Invoke(null, new object[] { path })!;

        Assert.Equal(expected, actual);
    }
}
