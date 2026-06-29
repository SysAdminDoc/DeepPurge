using DeepPurge.Core.Execution;
using Xunit;

namespace DeepPurge.Tests;

public class ExternalProcessRunnerTests
{
    [Fact]
    public void Command_uses_argument_list_so_metacharacters_stay_literal()
    {
        var command = new ExternalProcessCommand("tool.exe")
        {
            Arguments = new[] { "value&calc", "two words", "quote\"inside" },
        };

        var psi = command.ToStartInfo();

        Assert.Equal("", psi.Arguments);
        Assert.Equal(new[] { "value&calc", "two words", "quote\"inside" }, psi.ArgumentList);
        Assert.Contains("\"two words\"", command.ToRedactedCommandLine());
        Assert.Contains("value&calc", command.ToRedactedCommandLine());
    }

    [Fact]
    public void Command_redacts_selected_arguments_and_absolute_paths()
    {
        var secretPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "sensitive",
            "backup.reg");
        var command = new ExternalProcessCommand("reg.exe")
        {
            Arguments = new[] { "export", @"HKCU\Software\Test", secretPath, "/y" },
            RedactedArgumentIndexes = new HashSet<int> { 1 },
            RedactAbsolutePaths = true,
        };

        var rendered = command.ToRedactedCommandLine();

        Assert.DoesNotContain(@"HKCU\Software\Test", rendered);
        Assert.DoesNotContain(secretPath, rendered);
        Assert.Contains("<redacted>", rendered);
    }

    [Fact]
    public async Task Runner_maps_nonzero_exit_code()
    {
        var result = await ExternalProcessRunner.RunAsync(new ExternalProcessCommand("cmd.exe")
        {
            Arguments = new[] { "/d", "/c", "exit", "7" },
            Timeout = TimeSpan.FromSeconds(5),
        }, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Started);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(ExternalProcessStatus.FailedExitCode, result.Status);
    }

    [Fact]
    public async Task Runner_maps_timeout_and_kills_process_tree()
    {
        var result = await ExternalProcessRunner.RunAsync(new ExternalProcessCommand("powershell.exe")
        {
            Arguments = new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 5" },
            Timeout = TimeSpan.FromMilliseconds(150),
        }, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.Equal(ExternalProcessStatus.TimedOut, result.Status);
    }
}
