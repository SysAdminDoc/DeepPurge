using DeepPurge.Core.Execution;
using DeepPurge.Core.Uninstall;
using Xunit;

namespace DeepPurge.Tests;

public class UninstallerCommandTests
{
    [Fact]
    public void Msi_uninstaller_uses_protected_system_binary()
    {
        var startInfo = UninstallEngine.BuildUninstallerStartInfo(
            "MsiExec.exe /X {00000000-0000-0000-0000-000000000000} /qn",
            silent: true);

        Assert.Equal(
            WindowsExecutableResolver.ResolveSystemHelper("msiexec.exe"),
            startInfo.FileName);
        Assert.Contains("/X", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.True(startInfo.RedirectStandardOutput);
    }

    [Fact]
    public void Quoted_absolute_uninstaller_keeps_executable_and_arguments_separate()
    {
        var startInfo = UninstallEngine.BuildUninstallerStartInfo(
            "\"C:\\Program Files\\Vendor\\uninstall.exe\" /S /tenant=two",
            silent: false);

        Assert.Equal(
            @"C:\Program Files\Vendor\uninstall.exe",
            startInfo.FileName);
        Assert.Equal("/S /tenant=two", startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void Unquoted_absolute_path_with_spaces_resolves_existing_executable_prefix()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "DeepPurge Uninstaller",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "remove.exe");
        File.WriteAllBytes(executable, new byte[] { 0x4D, 0x5A });

        try
        {
            var startInfo = UninstallEngine.BuildUninstallerStartInfo(
                $"{executable} /quiet /norestart",
                silent: true);

            Assert.Equal(executable, startInfo.FileName);
            Assert.Equal("/quiet /norestart", startInfo.Arguments);
            Assert.NotEqual(
                WindowsExecutableResolver.ResolveSystemHelper("cmd.exe"),
                startInfo.FileName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("uninstall.exe /quiet")]
    [InlineData("tools\\uninstall.exe /quiet")]
    [InlineData("uninstall.exe")]
    [InlineData("echo remove && calc.exe")]
    public void Relative_or_shell_uninstaller_is_rejected(string command)
    {
        Assert.Throws<InvalidOperationException>(
            () => UninstallEngine.BuildUninstallerStartInfo(command));
    }
}
