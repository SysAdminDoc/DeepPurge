using global::Microsoft.Win32;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Registry;

public static class InstalledProgramScanner
{
    private static readonly (string Path, RegistrySource Source)[] UninstallPaths =
    {
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistrySource.HKLM_Uninstall),
        (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistrySource.HKLM_WOW64_Uninstall),
    };

    private const string HkcuUninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> GetAllInstalledPrograms(bool includeSystemComponents = false, bool includeUpdates = false)
    {
        var programs = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan HKLM paths
        foreach (var (path, source) in UninstallPaths)
        {
            try
            {
                using var baseKey = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (baseKey == null) continue;
                ScanRegistryKey(baseKey, path, source, programs, seen, includeSystemComponents, includeUpdates, "HKLM");
            }
            catch { /* Access denied or other registry errors */ }
        }

        // Scan HKCU
        try
        {
            using var hkcuKey = global::Microsoft.Win32.Registry.CurrentUser.OpenSubKey(HkcuUninstallPath);
            if (hkcuKey != null)
            {
                ScanRegistryKey(hkcuKey, HkcuUninstallPath, RegistrySource.HKCU_Uninstall,
                    programs, seen, includeSystemComponents, includeUpdates, "HKCU");
            }
        }
        catch { }

        // Scan all user SIDs for per-user installs
        try
        {
            using var usersKey = global::Microsoft.Win32.Registry.Users;
            foreach (var sid in usersKey.GetSubKeyNames())
            {
                try
                {
                    var userPath = $@"{sid}\{HkcuUninstallPath}";
                    using var userKey = usersKey.OpenSubKey(userPath);
                    if (userKey != null)
                    {
                        ScanRegistryKey(userKey, userPath, RegistrySource.HKCU_Uninstall,
                            programs, seen, includeSystemComponents, includeUpdates, $"HKU\\{sid}");
                    }
                }
                catch { }
            }
        }
        catch { }

        return programs.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ScanRegistryKey(RegistryKey baseKey, string basePath, RegistrySource source,
        List<InstalledProgram> programs, HashSet<string> seen,
        bool includeSystemComponents, bool includeUpdates, string hivePrefix)
    {
        foreach (var subKeyName in baseKey.GetSubKeyNames())
        {
            try
            {
                using var subKey = baseKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var displayName = subKey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;

                // Dedup by displayName + version
                var version = subKey.GetValue("DisplayVersion") as string ?? "";
                var dedupKey = $"{displayName}|{version}".ToLowerInvariant();
                if (!seen.Add(dedupKey)) continue;

                var isSystemComponent = (int)(subKey.GetValue("SystemComponent") ?? 0) == 1;
                if (isSystemComponent && !includeSystemComponents) continue;

                var parentKey = subKey.GetValue("ParentKeyName") as string ?? "";
                if (!string.IsNullOrEmpty(parentKey) && !includeUpdates) continue;

                var program = new InstalledProgram
                {
                    RegistryKeyName = subKeyName,
                    RegistryPath = $@"{hivePrefix}\{basePath}\{subKeyName}",
                    DisplayName = displayName,
                    DisplayVersion = version,
                    Publisher = subKey.GetValue("Publisher") as string ?? "",
                    InstallLocation = NormalizePath(subKey.GetValue("InstallLocation") as string ?? ""),
                    InstallDate = subKey.GetValue("InstallDate") as string ?? "",
                    UninstallString = subKey.GetValue("UninstallString") as string ?? "",
                    QuietUninstallString = subKey.GetValue("QuietUninstallString") as string ?? "",
                    DisplayIconPath = subKey.GetValue("DisplayIcon") as string ?? "",
                    EstimatedSizeKB = Convert.ToInt64(subKey.GetValue("EstimatedSize") ?? 0L),
                    IsSystemComponent = isSystemComponent,
                    IsWindowsInstaller = (int)(subKey.GetValue("WindowsInstaller") ?? 0) == 1,
                    ParentKeyName = parentKey,
                    Source = source,
                };

                programs.Add(program);
            }
            catch { /* Individual key read failure - skip */ }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.TrimEnd('\\', '/');
    }

    /// <summary>
    /// Detects the installer type (NSIS, InnoSetup, MSI, etc.) from the uninstall string
    /// </summary>
    public static string DetectInstallerType(InstalledProgram program)
    {
        var uninstall = program.UninstallString.ToLowerInvariant();

        if (program.IsWindowsInstaller || uninstall.Contains("msiexec"))
            return "MSI";
        if (uninstall.Contains("unins000") || uninstall.Contains("unins001"))
            return "InnoSetup";
        if (uninstall.Contains("uninst") && uninstall.Contains("nsis"))
            return "NSIS";
        if (uninstall.Contains("au_.exe") || uninstall.Contains("\\au_"))
            return "NSIS";
        if (uninstall.Contains("installshield"))
            return "InstallShield";
        if (uninstall.Contains("wix"))
            return "WiX";

        return "Unknown";
    }

    /// <summary>
    /// Attempts to determine a silent uninstall command
    /// </summary>
    public static string GetSilentUninstallCommand(InstalledProgram program)
    {
        if (!string.IsNullOrEmpty(program.QuietUninstallString))
            return program.QuietUninstallString;

        var uninstall = program.UninstallString;
        var type = DetectInstallerType(program);

        return type switch
        {
            "MSI" => uninstall.Contains("/I") 
                ? uninstall.Replace("/I", "/X") + " /qn /norestart"
                : uninstall + " /qn /norestart",
            "InnoSetup" => uninstall + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            "NSIS" => uninstall + " /S",
            "InstallShield" => uninstall + " -s",
            _ => ""
        };
    }
}
