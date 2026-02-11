using System.Diagnostics;
using global::Microsoft.Win32;
using DeepPurge.Core.Models;
using DeepPurge.Core.Registry;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Uninstall;

public class UninstallEngine
{
    public event Action<string>? StatusChanged;
    public event Action<int>? ProgressChanged;

    private readonly RegistryLeftoverScanner _registryScanner;
    private readonly FileLeftoverScanner _fileScanner;
    private readonly BackupManager _backupManager;

    public UninstallEngine()
    {
        _registryScanner = new RegistryLeftoverScanner();
        _fileScanner = new FileLeftoverScanner();
        _backupManager = new BackupManager();
    }

    /// <summary>
    /// Runs the full 4-step uninstall pipeline
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(InstalledProgram program, ScanMode scanMode,
        bool createRestorePoint = true, bool runBuiltInUninstaller = true, bool silent = false,
        CancellationToken ct = default)
    {
        var result = new UninstallResult();
        var sw = Stopwatch.StartNew();

        try
        {
            // Step 1: Create safety backups
            StatusChanged?.Invoke("Creating safety backups...");
            ProgressChanged?.Invoke(5);

            if (createRestorePoint)
            {
                try { RestorePointManager.CreateRestorePoint($"Before uninstalling {program.DisplayName}"); }
                catch (Exception ex) { StatusChanged?.Invoke($"Restore point warning: {ex.Message}"); }
            }

            _backupManager.BackupRegistryKey(program.RegistryPath);
            ProgressChanged?.Invoke(15);

            // Step 2: Run built-in uninstaller
            if (runBuiltInUninstaller && program.HasUninstaller)
            {
                StatusChanged?.Invoke($"Running {program.DisplayName} uninstaller...");
                ProgressChanged?.Invoke(20);

                var uninstallCmd = silent
                    ? InstalledProgramScanner.GetSilentUninstallCommand(program)
                    : program.UninstallString;

                if (string.IsNullOrEmpty(uninstallCmd))
                    uninstallCmd = program.UninstallString;

                var (exitCode, output, error) = await RunUninstallerAsync(uninstallCmd, ct);
                result.ExitCode = exitCode;
                result.Output = output;
                result.ErrorOutput = error;
                result.Success = exitCode == 0 || exitCode == 1641 || exitCode == 3010; // 1641/3010 = reboot required
            }
            else
            {
                result.UninstallerSkipped = true;
                result.Success = true;
            }
            ProgressChanged?.Invoke(50);

            // Step 3: Scan for leftovers
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
                ScanDuration = sw.Elapsed
            };

            StatusChanged?.Invoke($"Scan complete: {registryLeftovers.Count} registry + {fileLeftovers.Count} file leftovers found");
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

    /// <summary>
    /// Performs a leftover-only scan without running the built-in uninstaller (Forced Uninstall)
    /// </summary>
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
    /// Deletes selected leftover items with backup
    /// </summary>
    public async Task<(int regDeleted, int fileDeleted)> DeleteLeftoversAsync(
        IEnumerable<LeftoverItem> registryItems, IEnumerable<LeftoverItem> fileItems,
        bool moveFilesToRecycleBin = true, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            int regDeleted = 0, fileDeleted = 0;

            // Delete registry leftovers (backup first)
            foreach (var item in registryItems.Where(i => i.IsSelected && i.Confidence != LeftoverConfidence.Risky))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    _backupManager.BackupRegistryKey(item.Path);
                    DeleteRegistryItem(item);
                    regDeleted++;
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Failed to delete registry: {item.Path} - {ex.Message}");
                }
            }

            // Delete file leftovers
            foreach (var item in fileItems.Where(i => i.IsSelected && i.Confidence != LeftoverConfidence.Risky))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (moveFilesToRecycleBin)
                        MoveToRecycleBin(item.Path, item.Type == LeftoverType.Folder);
                    else
                        DeleteFileItem(item);
                    fileDeleted++;
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Failed to delete: {item.Path} - {ex.Message}");
                }
            }

            StatusChanged?.Invoke($"Deleted {regDeleted} registry entries and {fileDeleted} files/folders");
            return (regDeleted, fileDeleted);
        }, ct);
    }

    private async Task<(int exitCode, string output, string error)> RunUninstallerAsync(
        string uninstallCommand, CancellationToken ct)
    {
        var (exe, args) = ParseCommand(uninstallCommand);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };

        // MSI commands need special handling
        if (exe.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "msiexec.exe";
            psi.Arguments = uninstallCommand.Replace("msiexec.exe", "").Replace("MsiExec.exe", "").Trim();
        }

        using var process = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait up to 10 minutes for the uninstaller
        var timeout = TimeSpan.FromMinutes(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return (-1, output.ToString(), "Uninstaller timed out or was cancelled");
        }

        return (process.ExitCode, output.ToString(), error.ToString());
    }

    private static (string exe, string args) ParseCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
                return (command[1..end], command[(end + 1)..].Trim());
        }

        var spaceIdx = command.IndexOf(' ');
        if (spaceIdx > 0 && !command[..spaceIdx].Contains('\\'))
            return (command, "");

        if (spaceIdx > 0)
            return (command[..spaceIdx], command[(spaceIdx + 1)..].Trim());

        return (command, "");
    }

    private void DeleteRegistryItem(LeftoverItem item)
    {
        var path = item.Path;
        // Parse hive and subpath
        RegistryKey? hive = null;
        string subPath;

        if (path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
        {
            hive = global::Microsoft.Win32.Registry.LocalMachine;
            subPath = path[5..];
        }
        else if (path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
        {
            hive = global::Microsoft.Win32.Registry.CurrentUser;
            subPath = path[5..];
        }
        else if (path.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase))
        {
            hive = global::Microsoft.Win32.Registry.ClassesRoot;
            subPath = path[5..];
        }
        else return;

        if (item.Type == LeftoverType.RegistryValue)
        {
            // Delete a specific value
            var lastBackslash = subPath.LastIndexOf('\\');
            if (lastBackslash < 0) return;
            var keyPath = subPath[..lastBackslash];
            var valueName = subPath[(lastBackslash + 1)..];
            using var key = hive.OpenSubKey(keyPath, true);
            key?.DeleteValue(valueName, false);
        }
        else
        {
            // Delete entire key tree
            hive.DeleteSubKeyTree(subPath, false);
        }
    }

    private void DeleteFileItem(LeftoverItem item)
    {
        if (item.Type == LeftoverType.Folder && Directory.Exists(item.Path))
            Directory.Delete(item.Path, true);
        else if (File.Exists(item.Path))
            File.Delete(item.Path);
    }

    private void MoveToRecycleBin(string path, bool isDirectory)
    {
        try
        {
            var fileOp = new NativeMethods.SHFILEOPSTRUCT
            {
                wFunc = NativeMethods.FO_DELETE,
                pFrom = path + '\0' + '\0',
                fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION |
                         NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_SILENT
            };
            int result = NativeMethods.SHFileOperation(ref fileOp);
            if (result == 0) return;
        }
        catch { }

        // Fallback to permanent delete
        try
        {
            if (isDirectory && Directory.Exists(path))
                Directory.Delete(path, true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Delete failed: {path} - {ex.Message}");
        }
    }
}

internal static class NativeMethods
{
    public const int FO_DELETE = 0x0003;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_SILENT = 0x0004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string pFrom;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);
}
