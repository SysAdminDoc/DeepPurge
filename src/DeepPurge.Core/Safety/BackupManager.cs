using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeepPurge.Core.Safety;

public class BackupManager
{
    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepPurge", "Backups");

    // Allow the subset of chars Windows registry keys actually permit in paths.
    // Anything else is rejected rather than sanitized — if we saw a weird key,
    // something upstream is wrong and we shouldn't be blindly issuing reg.exe.
    private static readonly Regex SafeRegistryPath =
        new(@"^(HKLM|HKCU|HKCR|HKU|HKEY_[A-Z_]+)(\\[^""<>|*?\r\n]*)?$", RegexOptions.Compiled);

    public BackupManager()
    {
        try { Directory.CreateDirectory(BackupRoot); } catch { /* fallbacks to "" on failure */ }
    }

    public string BackupDirectory => BackupRoot;

    public string BackupRegistryKey(string registryPath)
    {
        if (string.IsNullOrEmpty(registryPath)) return "";

        var fullRegPath = NormalizeRegistryPath(registryPath);
        if (!SafeRegistryPath.IsMatch(fullRegPath)) return "";

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeName = SanitizeForFileName(registryPath);
        if (safeName.Length > 100) safeName = safeName[..100];
        var backupFile = Path.Combine(BackupRoot, $"reg_{timestamp}_{safeName}.reg");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{fullRegPath}\" \"{backupFile}\" /y",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(30000);
            return File.Exists(backupFile) ? backupFile : "";
        }
        catch { return ""; }
    }

    public bool RestoreRegistryBackup(string backupFile)
    {
        if (string.IsNullOrEmpty(backupFile) || !File.Exists(backupFile)) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"import \"{backupFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return false;
            process.WaitForExit(30000);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    public List<string> GetBackupFiles()
    {
        try
        {
            return Directory.GetFiles(BackupRoot, "*.reg")
                .OrderByDescending(File.GetCreationTime)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    public void CleanOldBackups(int keepDays = 30)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.GetFiles(BackupRoot, "*.reg"))
            {
                try { if (File.GetCreationTime(file) < cutoff) File.Delete(file); }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    private static string NormalizeRegistryPath(string path) => path
        .Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
        .Replace("HKCU\\", "HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)
        .Replace("HKCR\\", "HKEY_CLASSES_ROOT\\", StringComparison.OrdinalIgnoreCase)
        .Replace("HKU\\",  "HKEY_USERS\\",        StringComparison.OrdinalIgnoreCase);

    private static string SanitizeForFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c =>
            invalid.Contains(c) || c is '\\' or '/' or ':' ? '_' : c).ToArray());
        return cleaned.Trim('_');
    }
}

public static class RestorePointManager
{
    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    private static extern bool SRSetRestorePointW(
        ref RESTOREPOINTINFO pRestorePtSpec,
        out STATEMGRSTATUS pSMgrStatus);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFO
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    private const int BEGIN_SYSTEM_CHANGE = 100;
    private const int APPLICATION_UNINSTALL = 10;

    public static bool CreateRestorePoint(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) description = "DeepPurge Checkpoint";
        if (description.Length > 255) description = description[..255];

        try
        {
            var rpInfo = new RESTOREPOINTINFO
            {
                dwEventType = BEGIN_SYSTEM_CHANGE,
                dwRestorePtType = APPLICATION_UNINSTALL,
                llSequenceNumber = 0,
                szDescription = description,
            };

            if (SRSetRestorePointW(ref rpInfo, out var status) && status.nStatus == 0)
                return true;
        }
        catch { /* fall through to PowerShell */ }

        try
        {
            // Escape single quotes for PowerShell single-quoted literal.
            var escaped = description.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"" +
                    $"Checkpoint-Computer -Description '{escaped}' " +
                    "-RestorePointType 'APPLICATION_UNINSTALL'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(60000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
