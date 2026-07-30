using System.Text;

namespace DeepPurge.Core.Execution;

public enum ExternalProcessStatus
{
    Ok,
    FailedExitCode,
    TimedOut,
    Canceled,
    StartFailed,
}

public enum ExternalProcessExecutionContext
{
    CurrentProcess,
    OriginalInteractiveUser,
}

public sealed record ExternalProcessCommand(string FileName)
{
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int OutputLimitChars { get; init; } = 64 * 1024;
    public int ErrorLimitChars { get; init; } = 64 * 1024;
    public bool CreateNoWindow { get; init; } = true;
    public Encoding? StandardOutputEncoding { get; init; }
    public Encoding? StandardErrorEncoding { get; init; }
    public ISet<int> RedactedArgumentIndexes { get; init; } = new HashSet<int>();
    public bool RedactAbsolutePaths { get; init; }
    public ExternalProcessExecutionContext ExecutionContext { get; init; } =
        ExternalProcessExecutionContext.CurrentProcess;

    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = FileName,
            UseShellExecute = false,
            CreateNoWindow = CreateNoWindow,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
            psi.WorkingDirectory = WorkingDirectory;
        if (StandardOutputEncoding is not null)
            psi.StandardOutputEncoding = StandardOutputEncoding;
        if (StandardErrorEncoding is not null)
            psi.StandardErrorEncoding = StandardErrorEncoding;

        foreach (var arg in Arguments)
            psi.ArgumentList.Add(arg);

        return psi;
    }

    public string ToRedactedCommandLine()
        => string.Join(" ", new[] { FileName }.Concat(RedactedArguments()).Select(QuoteForDisplay));

    private IEnumerable<string> RedactedArguments()
    {
        for (var i = 0; i < Arguments.Count; i++)
        {
            var arg = Arguments[i];
            yield return ShouldRedact(i, arg) ? "<redacted>" : arg;
        }
    }

    private bool ShouldRedact(int index, string arg)
    {
        if (RedactedArgumentIndexes.Contains(index)) return true;
        if (!RedactAbsolutePaths) return false;
        try { return Path.IsPathFullyQualified(arg); }
        catch { return false; }
    }

    private static string QuoteForDisplay(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }
}

public sealed record ExternalProcessResult(
    ExternalProcessCommand Command,
    int ExitCode,
    string Output,
    string Error,
    bool Started,
    bool TimedOut,
    bool Canceled,
    string? StartError)
{
    public bool Success => Status == ExternalProcessStatus.Ok;

    public ExternalProcessStatus Status
    {
        get
        {
            if (!Started) return ExternalProcessStatus.StartFailed;
            if (TimedOut) return ExternalProcessStatus.TimedOut;
            if (Canceled) return ExternalProcessStatus.Canceled;
            return ExitCode == 0 ? ExternalProcessStatus.Ok : ExternalProcessStatus.FailedExitCode;
        }
    }

    public string RedactedCommandLine => Command.ToRedactedCommandLine();

    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { Output, Error }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}

public static class ExternalProcessRunner
{
    public static async Task<ExternalProcessResult> RunAsync(
        ExternalProcessCommand command,
        IProgress<string>? outputProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            command = command with
            {
                FileName = WindowsExecutableResolver.ResolveForLaunch(command.FileName),
            };
        }
        catch (Exception ex)
        {
            return new(
                command,
                -1,
                "",
                "",
                Started: false,
                TimedOut: false,
                Canceled: false,
                StartError: ex.Message);
        }

        if (command.ExecutionContext == ExternalProcessExecutionContext.OriginalInteractiveUser &&
            OriginalUserProcessLauncher.RequiresTokenBroker)
        {
            return await OriginalUserProcessLauncher.RunAsync(
                    command,
                    outputProgress,
                    ct)
                .ConfigureAwait(false);
        }

        using var timeoutCts = command.Timeout > TimeSpan.Zero
            ? new CancellationTokenSource(command.Timeout)
            : new CancellationTokenSource();
        using var linkedCts = command.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        Process? process = null;
        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputLimitHit = false;
        var errorLimitHit = false;

        try
        {
            process = new Process
            {
                StartInfo = command.ToStartInfo(),
                EnableRaisingEvents = true,
            };

            if (!process.Start())
                return new(command, -1, "", "process did not start", Started: false, TimedOut: false, Canceled: false, StartError: "process did not start");

            var outputTask = CaptureAsync(
                process.StandardOutput,
                output,
                command.OutputLimitChars,
                value => outputLimitHit = value,
                outputProgress,
                linkedCts.Token);
            var errorTask = CaptureAsync(
                process.StandardError,
                error,
                command.ErrorLimitChars,
                value => errorLimitHit = value,
                outputProgress,
                linkedCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                var timedOut = command.Timeout > TimeSpan.Zero &&
                               timeoutCts.IsCancellationRequested &&
                               !ct.IsCancellationRequested;
                await DrainBestEffort(outputTask, errorTask).ConfigureAwait(false);
                AppendTruncationMarkers(output, error, outputLimitHit, errorLimitHit);
                return new(
                    command,
                    -1,
                    output.ToString(),
                    error.ToString(),
                    Started: true,
                    TimedOut: timedOut,
                    Canceled: !timedOut,
                    StartError: null);
            }

            AppendTruncationMarkers(output, error, outputLimitHit, errorLimitHit);
            return new(
                command,
                process.ExitCode,
                output.ToString(),
                error.ToString(),
                Started: true,
                TimedOut: false,
                Canceled: false,
                StartError: null);
        }
        catch (OperationCanceledException)
        {
            if (process is not null) TryKill(process);
            return new(command, -1, output.ToString(), error.ToString(), Started: process is not null, TimedOut: false, Canceled: true, StartError: null);
        }
        catch (Exception ex)
        {
            return new(command, -1, output.ToString(), error.ToString(), Started: false, TimedOut: false, Canceled: false, StartError: ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    public static ExternalProcessResult Run(
        ExternalProcessCommand command,
        IProgress<string>? outputProgress = null,
        CancellationToken ct = default)
        => RunAsync(command, outputProgress, ct).GetAwaiter().GetResult();

    private static async Task CaptureAsync(
        StreamReader reader,
        StringBuilder target,
        int limitChars,
        Action<bool> setLimitHit,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            progress?.Report(line);
            AppendCapped(target, line + Environment.NewLine, limitChars, setLimitHit);
        }
    }

    private static void AppendCapped(
        StringBuilder target,
        string value,
        int limitChars,
        Action<bool> setLimitHit)
    {
        if (limitChars <= 0)
        {
            setLimitHit(true);
            return;
        }

        var remaining = limitChars - target.Length;
        if (remaining <= 0)
        {
            setLimitHit(true);
            return;
        }

        if (value.Length <= remaining)
        {
            target.Append(value);
            return;
        }

        target.Append(value.AsSpan(0, remaining));
        setLimitHit(true);
    }

    private static async Task DrainBestEffort(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* process was cancelled or killed; partial output is enough */ }
    }

    private static void AppendTruncationMarkers(
        StringBuilder output,
        StringBuilder error,
        bool outputLimitHit,
        bool errorLimitHit)
    {
        if (outputLimitHit) output.AppendLine("[stdout truncated]");
        if (errorLimitHit) error.AppendLine("[stderr truncated]");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* process may have exited between checks */ }
    }
}
