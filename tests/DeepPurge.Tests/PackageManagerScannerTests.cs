using System.Reflection;
using DeepPurge.Core.Models;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Uninstall;
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
    public void Package_source_parsers_tolerate_bad_output()
    {
        var parseJson = typeof(PackageManagerScanner).GetMethod(
            "ParseWingetJson",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var parseTable = typeof(PackageManagerScanner).GetMethod(
            "ParseWingetTable",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var jsonEntries = (List<WingetEntry>)parseJson.Invoke(null, new object[] { "{not-json" })!;
        var tableEntries = (List<WingetEntry>)parseTable.Invoke(null, new object[] { "not a winget table" })!;

        Assert.Empty(jsonEntries);
        Assert.Empty(tableEntries);
    }

    [Fact]
    public void Scoop_health_reports_missing_root_as_warning()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "DeepPurgeMissingScoop", Guid.NewGuid().ToString("N"));

        var health = PackageManagerScanner.InspectScoopRoot(missingRoot);

        Assert.Equal("scoop", health.Source);
        Assert.Equal(DeepPurge.Core.Diagnostics.SelfTestStatus.Warn, health.Status);
        Assert.Contains("root", health.LastScannerStatus);
    }

    [Fact]
    public void Scoop_health_counts_app_directories_without_scoop_cli()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeepPurgeScoopHealth", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "git"));
            Directory.CreateDirectory(Path.Combine(root, "scoop"));

            var health = PackageManagerScanner.InspectScoopRoot(root, version: "v0.5.0");

            Assert.Equal(DeepPurge.Core.Diagnostics.SelfTestStatus.Ok, health.Status);
            Assert.Equal(1, health.PackageCount);
            Assert.Equal(root, health.Root);
            Assert.Equal("v0.5.0", health.Version);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
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

    [Fact]
    public void Native_uninstall_builder_uses_winget_id_exact()
    {
        var psi = PackageManagerCommandBuilder.CreateNativeUninstallStartInfo(
            "winget",
            "Microsoft.PowerToys",
            silent: true);

        Assert.Equal("winget.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.DoesNotContain("cmd", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[]
        {
            "uninstall",
            "--id",
            "Microsoft.PowerToys",
            "--exact",
            "--disable-interactivity",
            "--accept-source-agreements",
            "--silent",
        }, psi.ArgumentList);
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public void Native_uninstall_builder_uses_scoop_command_wrapper()
    {
        var psi = PackageManagerCommandBuilder.CreateNativeUninstallStartInfo(
            "scoop",
            "git",
            silent: false);

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[] { "/d", "/c", "scoop", "uninstall", "git" }, psi.ArgumentList);
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public void Native_uninstall_builder_uses_chocolatey_noninteractive_flags()
    {
        var psi = PackageManagerCommandBuilder.CreateNativeUninstallStartInfo(
            "chocolatey",
            "7zip",
            silent: false);

        Assert.Equal("choco.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[]
        {
            "uninstall",
            "7zip",
            "--yes",
            "--no-progress",
            "--no-color",
            "--limit-output",
        }, psi.ArgumentList);
        Assert.Equal("", psi.Arguments);
    }

    [Fact]
    public void Native_uninstall_command_description_quotes_only_when_needed()
    {
        var command = PackageManagerCommandBuilder.DescribeNativeUninstallCommand(
            "winget",
            "Microsoft.PowerToys",
            silent: false);

        Assert.Equal(
            "winget.exe uninstall --id Microsoft.PowerToys --exact --disable-interactivity --accept-source-agreements",
            command);
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
        Assert.Throws<ArgumentException>(() =>
            PackageManagerCommandBuilder.CreateNativeUninstallStartInfo("winget", packageId));
        Assert.Throws<ArgumentException>(() =>
            PackageManagerCommandBuilder.CreateNativeUninstallStartInfo("scoop", packageId));
        Assert.Throws<ArgumentException>(() =>
            PackageManagerCommandBuilder.CreateNativeUninstallStartInfo("chocolatey", packageId));
    }

    [Fact]
    public void Native_uninstall_builder_rejects_unknown_source()
    {
        Assert.False(PackageManagerCommandBuilder.IsSupportedNativeUninstallManager("steam"));
        Assert.Throws<NotSupportedException>(() =>
            PackageManagerCommandBuilder.CreateNativeUninstallStartInfo("steam", "12345"));
    }

    [Fact]
    public async Task UninstallEngine_dry_run_previews_package_only_native_uninstall()
    {
        var program = new InstalledProgram
        {
            DisplayName = "git",
            PackageManager = "scoop",
            PackageId = "git",
            UninstallString = "",
        };

        var result = await new UninstallEngine().UninstallAsync(
            program,
            ScanMode.Moderate,
            createRestorePoint: false,
            dryRun: true,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.UninstallerSkipped);
        Assert.Contains("cmd.exe /d /c scoop uninstall git", result.Output);
    }
}
