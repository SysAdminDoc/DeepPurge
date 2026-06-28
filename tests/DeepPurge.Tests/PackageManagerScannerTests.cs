using System.Reflection;
using DeepPurge.Core.Models;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Registry;
using Xunit;

namespace DeepPurge.Tests;

public class PackageManagerScannerTests
{
    [Fact]
    public void ParseChocolateyLimitOutput_reads_name_version_pairs()
    {
        var parse = typeof(PackageManagerScanner).GetMethod(
            "ParseChocolateyLimitOutput",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var entries = (List<ChocolateyEntry>)parse.Invoke(null, new object[]
        {
            "Chocolatey v2.4.3\r\ngit|2.45.1\r\n7zip|24.08\r\nmalformed\r\n"
        })!;

        Assert.Equal(2, entries.Count);
        Assert.Equal("git", entries[0].Name);
        Assert.Equal("2.45.1", entries[0].Version);
        Assert.Equal("7zip", entries[1].Name);
    }

    [Fact]
    public void OemBloatScoring_flags_support_utilities_but_not_drivers()
    {
        var score = typeof(InstalledProgramScanner).GetMethod(
            "ScoreOemBloat",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var programs = new List<InstalledProgram>
        {
            new() { DisplayName = "Dell SupportAssist", Publisher = "Dell Inc." },
            new() { DisplayName = "Dell Touchpad Driver", Publisher = "Dell Inc." },
        };

        score.Invoke(null, new object[] { programs });

        Assert.True(programs[0].IsOemBloatCandidate);
        Assert.Contains("OEM", programs[0].FlagsDisplay);
        Assert.False(programs[1].IsOemBloatCandidate);
        Assert.Equal("", programs[1].OemBloatReason);
    }

    [Theory]
    [InlineData("Microsoft.PowerToys")]
    [InlineData("VideoLAN.VLC")]
    [InlineData("Git.Git")]
    [InlineData("7zip.7zip")]
    [InlineData("Some-Publisher.App_2")]
    public void Winget_upgrade_builder_accepts_normal_ids(string packageId)
    {
        var psi = PackageManagerCommandBuilder.CreateWingetUpgradeStartInfo(packageId);

        Assert.Equal("winget.exe", psi.FileName);
        Assert.True(psi.UseShellExecute);
        Assert.DoesNotContain("cmd", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[]
        {
            "upgrade",
            "--id",
            packageId,
            "--exact",
            "--accept-source-agreements",
            "--accept-package-agreements",
        }, psi.ArgumentList);
        Assert.Equal("", psi.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Microsoft.PowerToys")]
    [InlineData("Microsoft.PowerToys ")]
    [InlineData("Publisher App")]
    [InlineData("Publisher.App&calc")]
    [InlineData("Publisher.App|calc")]
    [InlineData("Publisher.App\"")]
    [InlineData("Publisher.App%PATH%")]
    [InlineData("Publisher.App\r\ncalc")]
    [InlineData("-starts-with-dash")]
    public void Winget_upgrade_builder_rejects_shell_metacharacters(string packageId)
    {
        Assert.False(PackageManagerCommandBuilder.IsSafeWingetPackageId(packageId));
        Assert.Throws<ArgumentException>(() =>
            PackageManagerCommandBuilder.CreateWingetUpgradeStartInfo(packageId));
    }
}
