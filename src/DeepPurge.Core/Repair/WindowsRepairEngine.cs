using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Execution;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Repair;

public enum RepairOperation
{
    SfcScan,
    DismScanHealth,
    DismRestoreHealth,
    DismComponentCleanup,
    DismResetBase,
    ChkDsk,
    RebuildFontCache,
    RebuildIconCache,
    WingetRepair,
    MsiRepair,
}

public class RepairResult
{
    public RepairOperation Operation { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs Microsoft-supplied Windows repair tools with live output capture.
/// </summary>
public class WindowsRepairEngine
{
    public async Task<RepairResult> RunAsync(
        RepairOperation op,
        IProgress<string>? log = null,
        string? argExtra = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new RepairResult { Operation = op };

        if (op is RepairOperation.RebuildFontCache or RepairOperation.RebuildIconCache)
        {
            var buffer = new StringBuilder();
            IProgress<string> combined = new Progress<string>(line =>
            {
                buffer.AppendLine(line);
                log?.Report(line);
            });
            result.ExitCode = op == RepairOperation.RebuildFontCache
                ? await RebuildFontCacheAsync(combined, ct).ConfigureAwait(false)
                : await RebuildIconCacheAsync(combined, ct).ConfigureAwait(false);
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            result.Output = buffer.ToString();
            return result;
        }

        var command = ResolveCommand(op, argExtra);
        var run = await ExternalProcessRunner.RunAsync(command, log, ct).ConfigureAwait(false);
        result.ExitCode = run.Status switch
        {
            ExternalProcessStatus.Ok or ExternalProcessStatus.FailedExitCode => run.ExitCode,
            ExternalProcessStatus.Canceled => -1,
            ExternalProcessStatus.StartFailed => -2,
            ExternalProcessStatus.TimedOut => -4,
            _ => -3,
        };

        if (run.Status == ExternalProcessStatus.Canceled) log?.Report("[cancelled]");
        if (run.Status == ExternalProcessStatus.TimedOut) log?.Report("[timeout]");
        if (run.Status == ExternalProcessStatus.StartFailed)
        {
            log?.Report($"[error] Could not launch '{command.FileName}': {run.StartError}");
            Log.Warn($"WindowsRepairEngine: {run.StartError}");
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        result.Output = run.CombinedOutput;
        return result;
    }

    private static ExternalProcessCommand ResolveCommand(RepairOperation op, string? extra) => op switch
    {
        RepairOperation.SfcScan => LongRepairCommand("sfc.exe", "/scannow"),
        RepairOperation.DismScanHealth => LongRepairCommand("DISM.exe", "/Online", "/Cleanup-Image", "/ScanHealth"),
        RepairOperation.DismRestoreHealth => LongRepairCommand("DISM.exe", "/Online", "/Cleanup-Image", "/RestoreHealth"),
        RepairOperation.DismComponentCleanup => LongRepairCommand("DISM.exe", "/Online", "/Cleanup-Image", "/StartComponentCleanup"),
        RepairOperation.DismResetBase => LongRepairCommand("DISM.exe", "/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"),
        RepairOperation.ChkDsk => LongRepairCommand("chkdsk.exe", Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:", "/scan"),
        RepairOperation.WingetRepair => PackageManagerExecutableResolver.CreateCommand(
            "winget",
            new[] { "repair", SanitizeToken(extra), "--silent" },
            TimeSpan.FromHours(2)),
        RepairOperation.MsiRepair => LongRepairCommand("msiexec.exe", "/fa", SanitizeProductCode(extra), "/qn"),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    private static ExternalProcessCommand LongRepairCommand(string fileName, params string[] args)
        => new(WindowsExecutableResolver.ResolveSystemHelper(fileName))
        {
            Arguments = args,
            Timeout = TimeSpan.FromHours(2),
            OutputLimitChars = 512 * 1024,
            ErrorLimitChars = 256 * 1024,
        };

    private static async Task<int> RebuildFontCacheAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Stopping FontCache services...");
        await RunShort(log, "net.exe", "stop FontCache", ct).ConfigureAwait(false);
        await RunShort(log, "net.exe", "stop FontCache3.0.0.0", ct).ConfigureAwait(false);

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cachePaths = new[]
        {
            Path.Combine(windir, "ServiceProfiles", "LocalService", "AppData", "Local"),
            Path.Combine(windir, "System32"),
        };

        var removed = 0;
        foreach (var dir in cachePaths)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.StartsWith("FontCache", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("FNTCACHE.DAT", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!HandleBoundFileOperations.DeleteFileWithinScope(
                                file,
                                dir,
                                out var reason))
                            throw new IOException(reason);
                        removed++;
                        log.Report($"deleted {name}");
                    }
                    catch (Exception ex) { log.Report($"skip {name}: {ex.Message}"); }
                }
            }
        }

        log.Report($"Removed {removed} cache file(s). Restarting FontCache...");
        await RunShort(log, "net.exe", "start FontCache", ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RebuildIconCacheAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Stopping Explorer...");
        await RunShort(log, "taskkill.exe", "/f /im explorer.exe", ct).ConfigureAwait(false);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var explorerDir = Path.Combine(local, "Microsoft", "Windows", "Explorer");

        var victims = new List<string>();
        try
        {
            if (Directory.Exists(explorerDir))
            {
                victims.AddRange(Directory.EnumerateFiles(explorerDir, "iconcache_*.db"));
                victims.AddRange(Directory.EnumerateFiles(explorerDir, "thumbcache_*.db"));
            }

            var legacyIcon = Path.Combine(local, "IconCache.db");
            if (File.Exists(legacyIcon)) victims.Add(legacyIcon);
        }
        catch (Exception ex) { log.Report($"enumerate: {ex.Message}"); }

        var removed = 0;
        foreach (var victim in victims)
        {
            try
            {
                if (!HandleBoundFileOperations.DeleteFileWithinScope(
                        victim,
                        local,
                        out var reason))
                    throw new IOException(reason);
                removed++;
                log.Report($"deleted {Path.GetFileName(victim)}");
            }
            catch (Exception ex) { log.Report($"skip {Path.GetFileName(victim)}: {ex.Message}"); }
        }

        log.Report($"Removed {removed} cache file(s). Restarting Explorer...");
        try
        {
            Process.Start(new ProcessStartInfo(
                WindowsExecutableResolver.ResolveSystemHelper("explorer.exe"))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            log.Report($"restart explorer: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static async Task RunShort(IProgress<string> log, string exe, string args, CancellationToken ct)
    {
        try
        {
            var result = await ExternalProcessRunner.RunAsync(new ExternalProcessCommand(exe)
            {
                Arguments = SplitShortArgs(args),
                Timeout = TimeSpan.FromSeconds(60),
            }, ct: ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
                log.Report(result.CombinedOutput);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.Report($"{exe}: {ex.Message}"); }
    }

    private static IReadOnlyList<string> SplitShortArgs(string args)
        => args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SanitizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "\"\"";
        var clean = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return clean.Length == 0 ? "\"\"" : clean;
    }

    private static string SanitizeProductCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "\"\"";
        var match = Regex.Match(
            raw,
            @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        return match.Success ? match.Value : "\"\"";
    }
}
