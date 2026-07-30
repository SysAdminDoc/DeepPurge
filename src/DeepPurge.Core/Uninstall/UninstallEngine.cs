using System.Diagnostics;
using System.Text;
using DeepPurge.Core.Execution;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Models;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Uninstall;

/// <summary>
/// Orchestrates the full uninstall flow: pre-op backup → vendor uninstaller →
/// registry leftover scan → file leftover scan → selective deletion.
/// </summary>
public class UninstallEngine
{
    public event Action<string>? StatusChanged;
    public event Action<int>? ProgressChanged;

    private readonly RegistryLeftoverScanner _registryScanner = new();
    private readonly FileLeftoverScanner _fileScanner = new();
    private readonly BackupManager _backupManager = new();

    private static readonly HashSet<int> UninstallerSuccessCodes = new() { 0, 1641, 3010 };
    public static TimeSpan UninstallerTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public async Task<UninstallResult> UninstallAsync(InstalledProgram program, ScanMode scanMode,
        bool createRestorePoint = true, bool runBuiltInUninstaller = true, bool silent = false,
        bool dryRun = false, CancellationToken ct = default)
    {
        var result = new UninstallResult();
        var sw = Stopwatch.StartNew();

        try
        {
            if (dryRun)
            {
                result.Success = true;
                result.UninstallerSkipped = true;
                result.Output = DescribeUninstallPreview(program, silent);
                StatusChanged?.Invoke(result.Output);
                return result;
            }

            StatusChanged?.Invoke("Creating safety backups...");
            ProgressChanged?.Invoke(5);

            if (createRestorePoint)
            {
                try { RestorePointManager.CreateRestorePoint($"Before uninstalling {program.DisplayName}"); }
                catch (Exception ex) { StatusChanged?.Invoke($"Restore point warning: {ex.Message}"); }
            }

            _backupManager.BackupRegistryKey(program.RegistryPath);
            ProgressChanged?.Invoke(15);

            var nativePackageUninstaller = TryBuildNativePackageUninstaller(
                program,
                silent,
                out var nativeCommand,
                out var nativeError);

            if (runBuiltInUninstaller && nativePackageUninstaller != null)
            {
                StatusChanged?.Invoke($"Running {program.PackageManager} uninstall for {program.DisplayName}...");
                ProgressChanged?.Invoke(20);

                var (exitCode, output, error) = await RunProcessAsync(nativePackageUninstaller, ct)
                    .ConfigureAwait(false);
                result.ExitCode = exitCode;
                result.Output = output;
                result.ErrorOutput = error;
                result.Success = UninstallerSuccessCodes.Contains(exitCode);

                DeepPurge.Core.Diagnostics.Log.Info(
                    $"Package uninstall {program.PackageManager}/{program.PackageId} exit={exitCode} command={nativeCommand}");
            }
            else if (runBuiltInUninstaller && program.HasUninstaller)
            {
                StatusChanged?.Invoke($"Running {program.DisplayName} uninstaller...");
                ProgressChanged?.Invoke(20);

                // Prefer the curated silent-switch DB over the raw scanner
                // heuristic — it handles more installer families correctly.
                var uninstallCmd = silent
                    ? SilentSwitchDatabase.ResolveSilentCommand(program)
                    : program.UninstallString;

                if (string.IsNullOrWhiteSpace(uninstallCmd))
                    uninstallCmd = program.UninstallString;

                if (string.IsNullOrWhiteSpace(uninstallCmd))
                {
                    // Nothing to run — the registry had HasUninstaller=true but
                    // no actual command. Report rather than silently launching
                    // an empty process.
                    StatusChanged?.Invoke($"No uninstall command registered for {program.DisplayName}");
                    result.ExitCode = -1;
                    result.ErrorOutput = "No uninstall command registered.";
                    result.UninstallerSkipped = true;
                    result.Success = false;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(nativeError))
                        StatusChanged?.Invoke($"Package-manager uninstall skipped: {nativeError}");

                    var (exitCode, output, error) = await RunUninstallerAsync(uninstallCmd, silent, ct);
                    result.ExitCode = exitCode;
                    result.Output = output;
                    result.ErrorOutput = error;
                    result.Success = UninstallerSuccessCodes.Contains(exitCode);
                }
            }
            else
            {
                result.UninstallerSkipped = true;
                if (string.IsNullOrWhiteSpace(nativeError))
                {
                    result.Success = true;
                }
                else
                {
                    result.ExitCode = -1;
                    result.ErrorOutput = nativeError;
                    result.Success = false;
                    StatusChanged?.Invoke(nativeError);
                }
            }
            ProgressChanged?.Invoke(50);

            StatusChanged?.Invoke("Scanning for leftover registry entries...");
            var registryLeftovers = await Task.Run(() => _registryScanner.ScanForLeftovers(program, scanMode), ct);
            ProgressChanged?.Invoke(70);

            StatusChanged?.Invoke("Scanning for leftover files and folders...");
            var fileLeftovers = await Task.Run(() => _fileScanner.ScanForLeftovers(program, scanMode), ct);
            ProgressChanged?.Invoke(90);

            result.LeftoverScan = new ScanResult
            {
                Program = program,
                RegistryLeftovers = registryLeftovers,
                FileLeftovers = fileLeftovers,
                ScanTime = DateTime.Now,
                Mode = scanMode,
                ScanDuration = sw.Elapsed,
            };

            StatusChanged?.Invoke(
                $"Scan complete: {registryLeftovers.Count} registry + {fileLeftovers.Count} file leftovers found");
            ProgressChanged?.Invoke(100);
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Operation cancelled");
            result.Success = false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error: {ex.Message}");
            result.Success = false;
            result.ErrorOutput = ex.ToString();
        }

        return result;
    }

    public async Task<ScanResult> ForcedScanAsync(string programName, string? installFolder, ScanMode scanMode,
        CancellationToken ct = default)
    {
        var fakeProgram = new InstalledProgram
        {
            DisplayName = programName,
            InstallLocation = installFolder ?? "",
        };

        StatusChanged?.Invoke($"Forced scan for: {programName}");

        var registryLeftovers = await Task.Run(() => _registryScanner.ScanForLeftovers(fakeProgram, scanMode), ct);
        var fileLeftovers = await Task.Run(() => _fileScanner.ScanForLeftovers(fakeProgram, scanMode), ct);

        return new ScanResult
        {
            Program = fakeProgram,
            RegistryLeftovers = registryLeftovers,
            FileLeftovers = fileLeftovers,
            ScanTime = DateTime.Now,
            Mode = scanMode,
        };
    }

    /// <summary>
    /// Backwards-compatible overload with the simple tuple return.
    /// New callers should prefer the richer
    /// <see cref="DeleteLeftoversAsync(IEnumerable{LeftoverItem}, IEnumerable{LeftoverItem}, DeleteOptions, IProgress{DeleteProgress}?, CancellationToken)"/>.
    /// </summary>
    public async Task<(int regDeleted, int fileDeleted)> DeleteLeftoversAsync(
        IEnumerable<LeftoverItem> registryItems, IEnumerable<LeftoverItem> fileItems,
        bool moveFilesToRecycleBin = true, CancellationToken ct = default)
    {
        var options = new DeleteOptions(
            DryRun: false,
            SecureDelete: false,
            UseRecycleBin: moveFilesToRecycleBin);
        var (_, reg, file) = await DeleteLeftoversAsync(registryItems, fileItems, options, progress: null, ct)
            .ConfigureAwait(false);
        return (reg, file);
    }

    /// <summary>
    /// Full-featured leftover deletion with dry-run, secure-delete, and
    /// progress reporting. Returns a summary plus the (reg, file) counts
    /// for compatibility with existing VM code.
    /// </summary>
    public async Task<(DeleteSummary summary, int regDeleted, int fileDeleted)> DeleteLeftoversAsync(
        IEnumerable<LeftoverItem> registryItems,
        IEnumerable<LeftoverItem> fileItems,
        DeleteOptions options,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        var regList = registryItems.Where(IsDeletable).ToList();
        var fileList = fileItems.Where(IsDeletable).ToList();
        var total = regList.Count + fileList.Count;

        return await Task.Run(() =>
        {
            int regDeleted = 0, fileDeleted = 0, skipped = 0;
            long freed = 0;
            int index = 0;

            foreach (var item in regList)
            {
                ct.ThrowIfCancellationRequested();
                index++;

                if (options.DryRun)
                {
                    regDeleted++;
                    progress?.Report(new DeleteProgress(index, total, freed, item.Path, Skipped: false));
                    continue;
                }

                try
                {
                    var result = DeleteRegistryItem(item);
                    if (result.Deleted)
                    {
                        regDeleted++;
                    }
                    else
                    {
                        skipped++;
                        StatusChanged?.Invoke($"Skipped registry: {item.Path} - {result.Status}");
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    StatusChanged?.Invoke($"Failed to delete registry: {item.Path} - {ex.Message}");
                }

                progress?.Report(new DeleteProgress(index, total, freed, item.Path, Skipped: false));
            }

            foreach (var item in fileList)
            {
                ct.ThrowIfCancellationRequested();
                index++;

                if (!SafetyGuard.IsPathSafeToDelete(item.Path))
                {
                    StatusChanged?.Invoke($"Blocked by SafetyGuard (file): {item.Path}");
                    skipped++;
                    progress?.Report(new DeleteProgress(index, total, freed, item.Path, Skipped: true));
                    continue;
                }

                if (options.DryRun)
                {
                    fileDeleted++;
                    freed += item.SizeBytes;
                    progress?.Report(new DeleteProgress(index, total, freed, item.Path, Skipped: false));
                    continue;
                }

                try
                {
                    var isDir = item.Type == LeftoverType.Folder;
                    bool removed;
                    string failureReason = "";
                    if (options.SecureDelete)
                    {
                        removed = isDir
                            ? SecureDelete.WipeDirectory(item.Path)
                            : SecureDelete.Wipe(item.Path);
                    }
                    else if (options.UseRecycleBin)
                    {
                        removed = SafetyGuard.SafeMoveToRecycleBin(
                            item.Path,
                            isDir,
                            out failureReason);
                    }
                    else
                    {
                        removed = DeleteFileItem(item);
                    }

                    if (!removed)
                    {
                        skipped++;
                        StatusChanged?.Invoke(
                            $"Failed to delete: {item.Path}" +
                            (string.IsNullOrWhiteSpace(failureReason)
                                ? ""
                                : $" - {failureReason}"));
                        progress?.Report(
                            new DeleteProgress(index, total, freed, item.Path, Skipped: true));
                        continue;
                    }

                    fileDeleted++;
                    freed += item.SizeBytes;
                }
                catch (Exception ex)
                {
                    skipped++;
                    StatusChanged?.Invoke($"Failed to delete: {item.Path} - {ex.Message}");
                }

                progress?.Report(new DeleteProgress(index, total, freed, item.Path, Skipped: false));
            }

            var verb = options.DryRun ? "Would delete" : "Deleted";
            StatusChanged?.Invoke($"{verb} {regDeleted} registry entries and {fileDeleted} files/folders");

            return (
                new DeleteSummary(regDeleted + fileDeleted, skipped, freed, options.DryRun),
                regDeleted,
                fileDeleted);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sequentially uninstalls a batch of programs — the BCUninstaller bulk
    /// queue pattern, simplified. Emits a <paramref name="progress"/> event
    /// after each item and returns one UninstallResult per input.
    /// Uses the silent-switch database so unattended bulk runs don't block
    /// on GUI prompts.
    /// </summary>
    public async Task<List<UninstallResult>> UninstallBatchAsync(
        IReadOnlyList<InstalledProgram> programs,
        ScanMode scanMode,
        bool createRestorePoint = true,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<UninstallResult>(programs.Count);
        bool firstRestorePoint = createRestorePoint;

        for (int i = 0; i < programs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var program = programs[i];

            if (program.IsProtected || App.ProtectedPrograms.IsProtected(program.DisplayName))
            {
                StatusChanged?.Invoke($"[{i + 1}/{programs.Count}] Skipping protected: {program.DisplayName}");
                continue;
            }

            StatusChanged?.Invoke($"[{i + 1}/{programs.Count}] Uninstalling {program.DisplayName}...");

            // Create a single restore point at the start of the batch rather
            // than one per program — otherwise Windows throttles SRSetRestorePoint.
            var thisRunRestorePoint = firstRestorePoint;
            firstRestorePoint = false;

            // Always run silent in a batch; otherwise the user has to click
            // through every installer.
            var result = await UninstallAsync(
                program,
                scanMode,
                createRestorePoint: thisRunRestorePoint,
                runBuiltInUninstaller: true,
                silent: true,
                ct: ct).ConfigureAwait(false);

            results.Add(result);
            progress?.Report(new DeleteProgress(
                i + 1,
                programs.Count,
                0,
                program.DisplayName,
                Skipped: !result.Success));
        }

        return results;
    }

    private static bool IsDeletable(LeftoverItem item)
        => item.IsSelected && item.Confidence != LeftoverConfidence.Risky;

    // ═══════════════════════════════════════════════════════
    //  Uninstaller process orchestration
    // ═══════════════════════════════════════════════════════

    private async Task<(int exitCode, string output, string error)> RunUninstallerAsync(
        string uninstallCommand, bool silent, CancellationToken ct)
    {
        var psi = BuildUninstallerStartInfo(uninstallCommand, silent);
        return await RunProcessAsync(psi, ct, forceRedirect: false).ConfigureAwait(false);
    }

    private async Task<(int exitCode, string output, string error)> RunProcessAsync(
        ExternalProcessCommand command, CancellationToken ct)
    {
        var result = await ExternalProcessRunner.RunAsync(command with
        {
            Timeout = UninstallerTimeout,
            OutputLimitChars = 256 * 1024,
            ErrorLimitChars = 128 * 1024,
        }, ct: ct).ConfigureAwait(false);

        if (result.TimedOut) return (-1, result.Output, "Uninstaller timed out");
        if (result.Canceled) return (-1, result.Output, "Uninstaller was cancelled");
        if (!result.Started) return (-1, result.Output, $"Failed to launch uninstaller: {result.StartError}");
        return (result.ExitCode, result.Output, result.Error);
    }

    private async Task<(int exitCode, string output, string error)> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct, bool forceRedirect)
    {
        if (forceRedirect)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        // Only subscribe to stdout/stderr when we actually redirected.
        // Interactive uninstallers left unredirected (silent=false) don't
        // surface through these streams; subscribing would be a no-op, but
        // BeginOutputReadLine throws InvalidOperationException against a
        // non-redirected stream.
        if (psi.RedirectStandardOutput)
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        if (psi.RedirectStandardError)
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        try
        {
            process.Start();
            if (psi.RedirectStandardOutput) process.BeginOutputReadLine();
            if (psi.RedirectStandardError)  process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return (-1, output.ToString(), $"Failed to launch uninstaller: {ex.Message}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(UninstallerTimeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* process may have already exited */ }
            return (-1, output.ToString(), "Uninstaller timed out or was cancelled");
        }

        return (process.ExitCode, output.ToString(), error.ToString());
    }

    private static ExternalProcessCommand? TryBuildNativePackageUninstaller(
        InstalledProgram program,
        bool silent,
        out string command,
        out string? error)
    {
        command = "";
        error = null;

        if (string.IsNullOrWhiteSpace(program.PackageManager) ||
            string.IsNullOrWhiteSpace(program.PackageId))
            return null;

        if (!PackageManagerCommandBuilder.IsSupportedNativeUninstallManager(program.PackageManager))
            return null;

        try
        {
            command = PackageManagerCommandBuilder.DescribeNativeUninstallCommand(
                program.PackageManager,
                program.PackageId,
                silent);
            return PackageManagerCommandBuilder.CreateNativeUninstallCommand(
                program.PackageManager,
                program.PackageId,
                silent);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Package-manager uninstall blocked for {program.DisplayName}: {ex.Message}";
            return null;
        }
    }

    private static string DescribeUninstallPreview(InstalledProgram program, bool silent)
    {
        var native = TryBuildNativePackageUninstaller(program, silent, out var command, out var nativeError);
        if (native != null) return $"Dry-run: would run {command}";
        if (!string.IsNullOrWhiteSpace(nativeError)) return $"Dry-run: {nativeError}";

        if (program.HasUninstaller)
        {
            var uninstallCmd = silent
                ? SilentSwitchDatabase.ResolveSilentCommand(program)
                : program.UninstallString;

            if (string.IsNullOrWhiteSpace(uninstallCmd))
                uninstallCmd = program.UninstallString;

            return string.IsNullOrWhiteSpace(uninstallCmd)
                ? "Dry-run: no uninstall command registered."
                : $"Dry-run: would run registered uninstaller: {uninstallCmd}";
        }

        return "Dry-run: no uninstall command registered.";
    }

    /// <summary>
    /// Turn an arbitrary uninstall string into a concrete ProcessStartInfo.
    ///
    /// Silent-mode behaviour:
    ///   silent=true  → hide the console window, redirect stdout/stderr so
    ///                  we can capture exit output cleanly for bulk runs
    ///   silent=false → leave the window visible and DO NOT redirect stdout,
    ///                  because GUI uninstallers (NSIS, InnoSetup) that don't
    ///                  use stdout will hang indefinitely when we pipe them.
    ///
    /// Handles:
    ///   - Quoted paths: `"C:\foo\unins.exe" /S`
    ///   - MsiExec special-case (/I→/X rewrite happens in GetSilentUninstallCommand)
    ///   - Unquoted paths with spaces: routed through `cmd /c` for shell parsing
    /// </summary>
    internal static ProcessStartInfo BuildUninstallerStartInfo(string uninstallCommand, bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(uninstallCommand))
            throw new ArgumentException("Uninstall command is empty.", nameof(uninstallCommand));

        var trimmed = uninstallCommand.Trim();

        ProcessStartInfo Tune(ProcessStartInfo psi)
        {
            psi.UseShellExecute        = false;
            psi.CreateNoWindow         = silent;
            psi.RedirectStandardOutput = silent;
            psi.RedirectStandardError  = silent;
            return psi;
        }

        // MSI special case — always route through msiexec.exe so we can trust the path.
        if (trimmed.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            var args = System.Text.RegularExpressions.Regex.Replace(
                trimmed, @"(?i)msiexec(\.exe)?\s*", "", System.Text.RegularExpressions.RegexOptions.None).Trim();
            return Tune(new ProcessStartInfo { FileName = "msiexec.exe", Arguments = args });
        }

        // Quoted path: `"C:\foo\unins.exe" /S`
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 0)
            {
                var exe = trimmed[1..end];
                var args = trimmed[(end + 1)..].Trim();
                return Tune(new ProcessStartInfo { FileName = exe, Arguments = args });
            }
        }

        // Unquoted, no spaces — run directly.
        if (!trimmed.Contains(' '))
            return Tune(new ProcessStartInfo { FileName = trimmed });

        // Unquoted with spaces — let cmd.exe parse. /c closes cmd after exit.
        return Tune(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c \"{trimmed}\"" });
    }

    // ═══════════════════════════════════════════════════════
    //  Deletion primitives
    // ═══════════════════════════════════════════════════════

    private static RegistryDeletionResult DeleteRegistryItem(LeftoverItem item)
        => item.Type == LeftoverType.RegistryValue
            ? RegistryDeletion.DeleteValue(item.Path, "uninstall-leftover")
            : RegistryDeletion.DeleteKeyTree(item.Path, "uninstall-leftover");

    private static bool DeleteFileItem(LeftoverItem item)
    {
        if (item.Type == LeftoverType.Folder)
            return Directory.Exists(item.Path) &&
                   Safety.SafetyGuard.SafeDeleteDirectory(item.Path);
        return File.Exists(item.Path) &&
               Safety.SafetyGuard.SafeDeleteFile(item.Path);
    }
}
