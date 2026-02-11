using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeepPurge.Core.Safety;

public class BackupManager
{
    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepPurge", "Backups");

    public BackupManager()
    {
        Directory.CreateDirectory(BackupRoot);
    }

    public string BackupRegistryKey(string registryPath)
    {
        if (string.IsNullOrEmpty(registryPath)) return "";

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeName = registryPath.Replace("\\", "_").Replace("/", "_");
        if (safeName.Length > 100) safeName = safeName[..100];
        var backupFile = Path.Combine(BackupRoot, $"reg_{timestamp}_{safeName}.reg");

        try
        {
            var fullRegPath = registryPath
                .Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\")
                .Replace("HKCU\\", "HKEY_CURRENT_USER\\")
                .Replace("HKCR\\", "HKEY_CLASSES_ROOT\\");

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
        if (!File.Exists(backupFile)) return false;
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
            process?.WaitForExit(30000);
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    public List<string> GetBackupFiles()
    {
        try
        {
            return Directory.GetFiles(BackupRoot, "*.reg")
                .OrderByDescending(f => File.GetCreationTime(f))
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
                if (File.GetCreationTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }

    public string BackupDirectory => BackupRoot;
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
        try
        {
            var rpInfo = new RESTOREPOINTINFO
            {
                dwEventType = BEGIN_SYSTEM_CHANGE,
                dwRestorePtType = APPLICATION_UNINSTALL,
                llSequenceNumber = 0,
                szDescription = description.Length > 255 ? description[..255] : description,
            };

            var success = SRSetRestorePointW(ref rpInfo, out var status);
            if (success && status.nStatus == 0)
                return true;
        }
        catch { }

        // Fallback: PowerShell
        try
        {
            var escapedDesc = description.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Checkpoint-Computer -Description '{escapedDesc}' -RestorePointType 'APPLICATION_UNINSTALL'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
