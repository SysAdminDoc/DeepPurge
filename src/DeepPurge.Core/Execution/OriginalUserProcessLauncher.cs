using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DeepPurge.Core.App;
using Microsoft.Win32.SafeHandles;

namespace DeepPurge.Core.Execution;

/// <summary>
/// Launches a process with the medium-integrity token owned by the Explorer
/// shell in this interactive session. This keeps user-installed package
/// managers out of DeepPurge's elevated security context.
/// </summary>
internal static class OriginalUserProcessLauncher
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const short SwHide = 0;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);

    internal static bool RequiresTokenBroker =>
        UserIdentity.IsProcessElevated || UserIdentity.IsSmaaElevated;

    internal static async Task<ExternalProcessResult> RunAsync(
        ExternalProcessCommand command,
        IProgress<string>? outputProgress,
        CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(command.FileName))
            return StartFailure(command, "Original-user launches require an absolute executable path.");

        if (!TryOpenOriginalUserToken(out var token, out var tokenError))
            return StartFailure(command, tokenError);

        using var brokerToken = token;
        using var timeoutCts = command.Timeout > TimeSpan.Zero
            ? new CancellationTokenSource(command.Timeout)
            : new CancellationTokenSource();
        using var linkedCts = command.Timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        IntPtr stdoutRead = IntPtr.Zero;
        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;
        IntPtr stdinHandle = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        Process? process = null;
        StreamReader? stdout = null;
        StreamReader? stderr = null;
        var launchGateHeld = false;

        try
        {
            await LaunchGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            launchGateHeld = true;
            CreateOutputPipe(out stdoutRead, out stdoutWrite);
            CreateOutputPipe(out stderrRead, out stderrWrite);
            stdinHandle = CreateNullInputHandle();

            if (!CreateEnvironmentBlock(out environment, token, inherit: false))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create the original user's environment block.");

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles |
                        (command.CreateNoWindow ? StartfUseShowWindow : 0),
                ShowWindow = SwHide,
                StandardInput = stdinHandle,
                StandardOutput = stdoutWrite,
                StandardError = stderrWrite,
            };
            var commandLine = new StringBuilder(BuildCommandLine(command));
            var creationFlags = CreateUnicodeEnvironment |
                                (command.CreateNoWindow ? CreateNoWindow : 0);
            var workingDirectory = ResolveWorkingDirectory(command.WorkingDirectory);

            if (!CreateProcessWithTokenW(
                    token,
                    0,
                    command.FileName,
                    commandLine,
                    creationFlags,
                    environment,
                    workingDirectory,
                    ref startup,
                    out var processInfo))
            {
                var errorCode = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    errorCode,
                    $"The original-user process broker could not start the command " +
                    $"(Win32 {errorCode}: {new Win32Exception(errorCode).Message}).");
            }

            try
            {
                process = Process.GetProcessById(checked((int)processInfo.ProcessId));
            }
            finally
            {
                CloseNativeHandle(processInfo.Thread);
                CloseNativeHandle(processInfo.Process);
            }

            CloseNativeHandle(stdoutWrite);
            stdoutWrite = IntPtr.Zero;
            CloseNativeHandle(stderrWrite);
            stderrWrite = IntPtr.Zero;
            CloseNativeHandle(stdinHandle);
            stdinHandle = IntPtr.Zero;

            stdout = CreateReader(
                ref stdoutRead,
                command.StandardOutputEncoding ?? Encoding.UTF8);
            stderr = CreateReader(
                ref stderrRead,
                command.StandardErrorEncoding ?? Encoding.UTF8);
            LaunchGate.Release();
            launchGateHeld = false;

            var output = new StringBuilder();
            var error = new StringBuilder();
            var outputLimitHit = false;
            var errorLimitHit = false;
            var outputTask = CaptureAsync(
                stdout,
                output,
                command.OutputLimitChars,
                value => outputLimitHit = value,
                outputProgress,
                linkedCts.Token);
            var errorTask = CaptureAsync(
                stderr,
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
                AppendTruncationMarkers(
                    output,
                    error,
                    outputLimitHit,
                    errorLimitHit);
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

            AppendTruncationMarkers(
                output,
                error,
                outputLimitHit,
                errorLimitHit);
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
            return new(
                command,
                -1,
                "",
                "",
                Started: process is not null,
                TimedOut: false,
                Canceled: true,
                StartError: null);
        }
        catch (Exception ex)
        {
            if (process is not null) TryKill(process);
            return StartFailure(command, ex.Message);
        }
        finally
        {
            stdout?.Dispose();
            stderr?.Dispose();
            process?.Dispose();
            if (launchGateHeld) LaunchGate.Release();
            CloseNativeHandle(stdoutRead);
            CloseNativeHandle(stdoutWrite);
            CloseNativeHandle(stderrRead);
            CloseNativeHandle(stderrWrite);
            CloseNativeHandle(stdinHandle);
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
        }
    }

    internal static string BuildCommandLine(ExternalProcessCommand command)
        => string.Join(
            " ",
            new[] { command.FileName }
                .Concat(command.Arguments)
                .Select(QuoteWindowsArgument));

    internal static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (!argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(c);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static bool TryOpenOriginalUserToken(
        out SafeAccessTokenHandle token,
        out string error)
    {
        token = new SafeAccessTokenHandle(IntPtr.Zero);
        error = "No medium-integrity Explorer token was available for the original desktop user.";
        var expectedSid = UserIdentity.RealUserSid;
        using var currentProcess = Process.GetCurrentProcess();
        var currentSession = currentProcess.SessionId;

        foreach (var explorer in Process.GetProcessesByName("explorer"))
        {
            using (explorer)
            {
                try
                {
                    if (explorer.SessionId != currentSession) continue;
                    if (!OpenProcessToken(
                            explorer.Handle,
                            TokenDuplicate | TokenQuery,
                            out var candidate))
                        continue;

                    using (candidate)
                    using (var identity = new WindowsIdentity(candidate.DangerousGetHandle()))
                    {
                        var sid = identity.User?.Value;
                        if (string.IsNullOrWhiteSpace(sid) ||
                            !sid.Equals(expectedSid, StringComparison.OrdinalIgnoreCase) ||
                            IsTokenElevated(candidate))
                        {
                            continue;
                        }

                        if (!DuplicateTokenEx(
                                candidate,
                                TokenAssignPrimary |
                                TokenDuplicate |
                                TokenQuery |
                                TokenAdjustDefault |
                                TokenAdjustSessionId,
                                IntPtr.Zero,
                                SecurityImpersonation,
                                TokenPrimary,
                                out var duplicated))
                        {
                            duplicated.Dispose();
                            continue;
                        }

                        token = duplicated;
                        return true;
                    }
                }
                catch
                {
                    // Explorer instances can disappear while being inspected.
                }
            }
        }

        return false;
    }

    private static bool IsTokenElevated(SafeAccessTokenHandle token)
    {
        var elevation = 0;
        var size = Marshal.SizeOf<int>();
        return GetTokenInformation(
                   token,
                   TokenElevation,
                   ref elevation,
                   size,
                   out _) &&
               elevation != 0;
    }

    private static void CreateOutputPipe(out IntPtr read, out IntPtr write)
    {
        var security = new SecurityAttributes
        {
            Size = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!CreatePipe(out read, out write, ref security, 0))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not create a process output pipe.");
        if (!SetHandleInformation(read, HandleFlagInherit, 0))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not protect the parent end of a process output pipe.");
    }

    private static IntPtr CreateNullInputHandle()
    {
        var security = new SecurityAttributes
        {
            Size = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        var handle = CreateFileW(
            "NUL",
            GenericRead,
            FileShareRead | FileShareWrite,
            ref security,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not open the null input device.");
        return handle;
    }

    private static StreamReader CreateReader(ref IntPtr handle, Encoding encoding)
    {
        var safeHandle = new SafeFileHandle(handle, ownsHandle: true);
        handle = IntPtr.Zero;
        return new StreamReader(
            new FileStream(
                safeHandle,
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false),
            encoding,
            detectEncodingFromByteOrderMarks: true);
    }

    private static string ResolveWorkingDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var path = Path.GetFullPath(requested);
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException(
                    $"Process working directory does not exist: '{path}'.");
            return path;
        }

        var profile = UserIdentity.RealProfilePath;
        return Directory.Exists(profile) ? profile : Environment.SystemDirectory;
    }

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
        try
        {
            await Task.WhenAll(tasks)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch { }
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
        catch { }
    }

    private static ExternalProcessResult StartFailure(
        ExternalProcessCommand command,
        string error)
        => new(
            command,
            -1,
            "",
            "",
            Started: false,
            TimedOut: false,
            Canceled: false,
            StartError: error);

    private static void CloseNativeHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != InvalidHandleValue)
            CloseHandle(handle);
    }

    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        ref int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle token,
        uint logonFlags,
        string applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        IntPtr handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
