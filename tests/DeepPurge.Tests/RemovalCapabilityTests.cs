using DeepPurge.Core.Models;
using DeepPurge.Core.Uninstall;

namespace DeepPurge.Tests;

public sealed class RemovalCapabilityTests
{
    [Theory]
    [InlineData(RemovalCapability.NativeUninstaller, "Native uninstaller")]
    [InlineData(RemovalCapability.PackageManager, "Package manager")]
    [InlineData(RemovalCapability.PortableFolder, "Portable folder")]
    [InlineData(RemovalCapability.GameLauncher, "Game launcher")]
    [InlineData(RemovalCapability.Unsupported, "Unsupported")]
    public void Capability_names_are_readable(RemovalCapability capability, string expected)
    {
        var program = new InstalledProgram { RemovalCapability = capability };

        Assert.Equal(expected, program.CapabilityDisplay);
    }

    [Fact]
    public void Native_uninstaller_exposes_command_and_trust_facts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DeepPurge_Uninstaller_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "uninstall.exe");
        File.WriteAllBytes(executable, new byte[] { 0x4D, 0x5A });
        try
        {
            var program = new InstalledProgram
            {
                DisplayName = "Example",
                RegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Example",
                UninstallString = $"\"{executable}\" /quiet",
                Source = RegistrySource.HKCU_Uninstall,
            };

            RemovalCapabilityInspector.Populate(program);

            Assert.Equal(RemovalCapability.NativeUninstaller, program.RemovalCapability);
            Assert.Equal(executable, program.UninstallerExecutablePath);
            Assert.Equal("/quiet", program.UninstallerArguments);
            Assert.True(program.RemovalSupported);
            Assert.Contains("risk=High", program.ActionTrustDisplay);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Package_only_row_has_a_source_identity_and_native_action()
    {
        var program = new InstalledProgram
        {
            DisplayName = "Git",
            PackageManager = "scoop",
            PackageId = "git",
        };

        RemovalCapabilityInspector.Populate(program);

        Assert.Equal(RemovalCapability.PackageManager, program.RemovalCapability);
        Assert.Equal("scoop:git", program.RemovalSourceIdentity);
        Assert.Contains("uninstall", program.UninstallerArguments, StringComparison.OrdinalIgnoreCase);
        Assert.True(program.RemovalSupported);
    }

    [Fact]
    public void Portable_folder_is_recoverable_and_game_launcher_is_review_only()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DeepPurge_Portable_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var portable = new InstalledProgram
            {
                DisplayName = "Portable",
                InstallLocation = root,
                PackageManager = "portable",
                Source = RegistrySource.Portable,
            };
            RemovalCapabilityInspector.Populate(portable);

            var game = new InstalledProgram
            {
                DisplayName = "Game",
                InstallLocation = root,
                PackageManager = "steam",
                Source = RegistrySource.Portable,
            };
            RemovalCapabilityInspector.Populate(game);

            Assert.Equal(RemovalCapability.PortableFolder, portable.RemovalCapability);
            Assert.True(portable.RemovalSupported);
            Assert.Equal(RemovalCapability.GameLauncher, game.RemovalCapability);
            Assert.False(game.RemovalSupported);
            Assert.Contains("source-native", game.RemovalStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unsupported_game_uninstall_does_not_report_success()
    {
        var program = new InstalledProgram
        {
            DisplayName = "Game",
            InstallLocation = Path.Combine(Path.GetTempPath(), "not-a-game"),
            PackageManager = "steam",
            Source = RegistrySource.Portable,
        };

        var result = await new UninstallEngine().UninstallAsync(
            program,
            ScanMode.Moderate,
            createRestorePoint: false,
            dryRun: true,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(UninstallOutcome.Unsupported, result.Outcome);
        Assert.Equal(RemovalCapability.GameLauncher, result.Capability);
        Assert.Contains("source-native", result.ErrorOutput, StringComparison.OrdinalIgnoreCase);
    }
}
