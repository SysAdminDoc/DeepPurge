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

    [Fact]
    public async Task Runner_ignores_current_directory_shadow_for_system_helper()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "DeepPurgeHelperShadow",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var decoy = Path.Combine(root, "cmd.exe");
        await File.WriteAllTextAsync(
            decoy,
            "This is deliberately not an executable.",
            TestContext.Current.CancellationToken);

        try
        {
            var result = await ExternalProcessRunner.RunAsync(
                new ExternalProcessCommand("cmd.exe")
                {
                    Arguments = new[] { "/d", "/c", "echo", "protected-helper" },
                    WorkingDirectory = root,
                    Timeout = TimeSpan.FromSeconds(5),
                },
                ct: TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.StartError ?? result.CombinedOutput);
            Assert.Equal(
                WindowsExecutableResolver.ResolveSystemHelper("cmd.exe"),
                result.Command.FileName);
            Assert.NotEqual(decoy, result.Command.FileName);
            Assert.Contains("protected-helper", result.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Runner_rejects_unknown_unqualified_executable()
    {
        var result = await ExternalProcessRunner.RunAsync(
            new ExternalProcessCommand("user-controlled-tool.exe"),
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Started);
        Assert.Equal(ExternalProcessStatus.StartFailed, result.Status);
        Assert.Contains("Unqualified executable", result.StartError);
    }

    [Fact]
    public void Original_user_command_line_preserves_argument_boundaries()
    {
        var command = new ExternalProcessCommand(@"C:\Program Files\Tool\tool.exe")
        {
            Arguments = new[]
            {
                "plain",
                "two words",
                "quote\"inside",
                @"C:\path with space\",
            },
        };

        var rendered = OriginalUserProcessLauncher.BuildCommandLine(command);

        Assert.Equal(
            "\"C:\\Program Files\\Tool\\tool.exe\" plain \"two words\" \"quote\\\"inside\" \"C:\\path with space\\\\\"",
            rendered);
    }

    [Fact]
    public async Task Original_user_context_never_inherits_an_elevated_token()
    {
        var command = new ExternalProcessCommand(
            WindowsExecutableResolver.ResolveSystemHelper("powershell.exe"))
        {
            Arguments = new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
            },
            Timeout = TimeSpan.FromSeconds(10),
            ExecutionContext = ExternalProcessExecutionContext.OriginalInteractiveUser,
        };

        var result = await ExternalProcessRunner.RunAsync(
            command,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.StartError ?? result.CombinedOutput);
        Assert.Equal("False", result.Output.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task Original_user_context_maps_timeout()
    {
        var command = new ExternalProcessCommand(
            WindowsExecutableResolver.ResolveSystemHelper("powershell.exe"))
        {
            Arguments = new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Start-Sleep -Seconds 5",
            },
            Timeout = TimeSpan.FromMilliseconds(200),
            ExecutionContext = ExternalProcessExecutionContext.OriginalInteractiveUser,
        };

        var result = await ExternalProcessRunner.RunAsync(
            command,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Started, result.StartError);
        Assert.True(result.TimedOut);
        Assert.Equal(ExternalProcessStatus.TimedOut, result.Status);
    }
}
