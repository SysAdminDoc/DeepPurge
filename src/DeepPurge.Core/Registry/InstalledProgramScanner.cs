using global::Microsoft.Win32;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
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
            catch (Exception ex) { Log.Warn($"Failed to scan HKLM uninstall path '{path}': {ex.Message}"); }
        }

        // Scan HKCU — use real user's hive when running under SMAA elevation
        try
        {
            using var hkcuKey = UserIdentity.IsSmaaElevated
                ? UserIdentity.OpenRealUserHive(HkcuUninstallPath)
                : global::Microsoft.Win32.Registry.CurrentUser.OpenSubKey(HkcuUninstallPath);
            if (hkcuKey != null)
            {
                ScanRegistryKey(hkcuKey, HkcuUninstallPath, RegistrySource.HKCU_Uninstall,
                    programs, seen, includeSystemComponents, includeUpdates, "HKCU");
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to scan HKCU uninstall registry: {ex.Message}"); }

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
                catch (Exception ex) { Log.Warn($"Failed to scan user SID uninstall key '{sid}': {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to enumerate HKU user SIDs: {ex.Message}"); }

        FlagSuspectedBundleware(programs);
        ScoreOemBloat(programs);
        return programs.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly HashSet<string> TrustedPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft Corporation", "Microsoft Windows",
        "Intel", "Intel Corporation", "Intel(R) Corporation",
        "NVIDIA", "NVIDIA Corporation",
        "AMD", "Advanced Micro Devices", "Advanced Micro Devices, Inc.",
        "Realtek", "Realtek Semiconductor",
        "Qualcomm", "Broadcom", "Texas Instruments",
    };

    private static readonly string[] OemPublishers =
    {
        "Acer", "Alienware", "ASUS", "ASUSTeK", "Dell", "Dell Inc.",
        "Dynabook", "Fujitsu", "Gigabyte", "HP", "HP Inc.", "Hewlett-Packard",
        "Huawei", "Lenovo", "LG", "MSI", "Micro-Star", "Samsung", "Toshiba",
    };

    private static readonly string[] OemBloatTerms =
    {
        "app explorer", "assistant", "care center", "companion", "customer connect",
        "digital delivery", "documentation", "jumpstart", "marketplace", "myasus",
        "optimizer", "promo", "registration", "supportassist", "support assistant",
        "support center", "trial", "vantage", "welcome",
    };

    private static readonly string[] OemEssentialTerms =
    {
        "audio", "bios", "bluetooth", "chipset", "control center", "driver",
        "firmware", "graphics", "hotkey", "lan", "management engine", "power",
        "runtime", "sdk", "service", "system interface", "thermal", "touchpad",
        "wi-fi", "wireless",
    };

    private static void FlagSuspectedBundleware(List<InstalledProgram> programs)
    {
        var byDate = programs
            .Where(p => !string.IsNullOrEmpty(p.InstallDate) && p.InstallDate.Length == 8)
            .GroupBy(p => p.InstallDate)
            .Where(g => g.Count() >= 2);

        foreach (var group in byDate)
        {
            var list = group.ToList();
            var hasNonTrusted = list.Where(p =>
                !string.IsNullOrEmpty(p.Publisher) &&
                !TrustedPublishers.Contains(p.Publisher) &&
                !p.IsSystemComponent).ToList();

            if (hasNonTrusted.Count < 2) continue;

            var publisherGroups = hasNonTrusted.GroupBy(p => p.Publisher, StringComparer.OrdinalIgnoreCase);
            foreach (var pg in publisherGroups.Where(g => g.Count() == 1))
            {
                foreach (var lone in pg)
                    lone.IsSuspectedBundleware = true;
            }
        }
    }

    private static void ScoreOemBloat(List<InstalledProgram> programs)
    {
        foreach (var program in programs)
        {
            if (program.IsSystemComponent)
            {
                program.OemBloatScore = 0;
                program.OemBloatReason = "";
                continue;
            }

            var name = program.DisplayName ?? "";
            var publisher = program.Publisher ?? "";
            var reasons = new List<string>();
            var score = 0;

            if (ContainsAny(publisher, OemPublishers) || ContainsAny(name, OemPublishers))
            {
                score += 35;
                reasons.Add("OEM publisher/name");
            }

            if (ContainsAny(name, OemBloatTerms))
            {
                score += 45;
                reasons.Add("support/trial utility");
            }

            if (program.IsSuspectedBundleware)
            {
                score += 15;
                reasons.Add("same-day bundle signal");
            }

            if (ContainsAny(name, OemEssentialTerms))
            {
                score -= 45;
                reasons.Add("driver/system utility signal");
            }

            program.OemBloatScore = Math.Clamp(score, 0, 100);
            program.OemBloatReason = program.OemBloatScore >= 60
                ? string.Join("; ", reasons)
                : "";
        }
    }

    private static bool ContainsAny(string value, IEnumerable<string> needles)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));
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

                RemovalCapabilityInspector.Populate(program);
                programs.Add(program);
            }
            catch (Exception ex) { Log.Warn($"Failed to read registry subkey '{subKeyName}': {ex.Message}"); }
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

    public static void ComputeActualSizes(IList<InstalledProgram> programs)
    {
        Parallel.ForEach(programs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, prog =>
        {
            long total = 0;
            var paths = new List<string>();

            if (!string.IsNullOrEmpty(prog.InstallLocation) && Directory.Exists(prog.InstallLocation))
                paths.Add(prog.InstallLocation);

            var appDataDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            };

            var nameTerms = new List<string>();
            if (!string.IsNullOrEmpty(prog.DisplayName)) nameTerms.Add(prog.DisplayName);
            if (!string.IsNullOrEmpty(prog.InstallLocation))
            {
                var folderName = Path.GetFileName(prog.InstallLocation.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(folderName) && folderName.Length > 2) nameTerms.Add(folderName);
            }

            foreach (var appDataDir in appDataDirs)
            {
                if (string.IsNullOrEmpty(appDataDir) || !Directory.Exists(appDataDir)) continue;
                try
                {
                    foreach (var sub in Directory.GetDirectories(appDataDir))
                    {
                        var subName = Path.GetFileName(sub);
                        if (nameTerms.Any(t => subName.Equals(t, StringComparison.OrdinalIgnoreCase)))
                            paths.Add(sub);
                    }
                }
                catch (Exception ex) { Log.Warn($"Failed to enumerate appdata directory '{appDataDir}': {ex.Message}"); }
            }

            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    total += new DirectoryInfo(path)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(fi => { try { return fi.Length; } catch { return 0L; } });
                }
                catch (Exception ex) { Log.Warn($"Failed to compute size for '{path}': {ex.Message}"); }
            }

            if (total > 0) prog.ActualSizeBytes = total;
        });
    }
}
