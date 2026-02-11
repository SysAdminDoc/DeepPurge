namespace DeepPurge.Core.Safety;

/// <summary>
/// Centralized safety validation for all destructive operations.
/// Every delete/modify operation MUST pass through SafetyGuard before execution.
/// </summary>
public static class SafetyGuard
{
    // ═══════════════════════════════════════════════════════
    //  PROTECTED PATHS — NEVER delete anything under these
    // ═══════════════════════════════════════════════════════

    private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows",
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Windows\WinSxS",
        @"C:\Windows\Boot",
        @"C:\Windows\Fonts",
        @"C:\Windows\Globalization",
        @"C:\Windows\IME",
        @"C:\Windows\rescache",
        @"C:\Windows\Resources",
        @"C:\Windows\servicing",
        @"C:\Windows\SystemResources",
        @"C:\Program Files\Windows Defender",
        @"C:\Program Files\Windows Security",
        @"C:\Program Files\Common Files\microsoft shared",
        @"C:\Program Files (x86)\Common Files",
        @"C:\ProgramData\Microsoft\Windows",
        @"C:\ProgramData\Microsoft\Windows Defender",
        @"C:\Recovery",
        @"C:\$Recycle.Bin",
    };

    private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows\System32\config\SYSTEM",
        @"C:\Windows\System32\config\SOFTWARE",
        @"C:\Windows\System32\config\SAM",
        @"C:\Windows\System32\config\SECURITY",
        @"C:\Windows\System32\config\DEFAULT",
        @"C:\bootmgr",
        @"C:\BOOTNXT",
    };

    private static readonly HashSet<string> ProtectedRegistryRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        @"HKLM\SYSTEM\CurrentControlSet\Control",
        @"HKLM\SYSTEM\CurrentControlSet\Enum",
        @"HKLM\SYSTEM\CurrentControlSet\Hardware Profiles",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
        @"HKLM\SOFTWARE\Microsoft\Cryptography",
        @"HKLM\SOFTWARE\Policies",
        @"HKCU\SOFTWARE\Policies",
        @"HKLM\BCD00000000",
        @"HKLM\SAM",
        @"HKLM\SECURITY",
    };

    private static readonly HashSet<string> ProtectedServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows core
        "wuauserv", "BITS", "Winmgmt", "EventLog", "PlugPlay", "RpcSs", "RpcEptMapper",
        "DcomLaunch", "LSM", "SamSs", "LanmanServer", "LanmanWorkstation", "Dhcp",
        "Dnscache", "CryptSvc", "TrustedInstaller", "msiserver", "Spooler",
        "WinDefend", "MpsSvc", "SecurityHealthService", "wscsvc", "WdiServiceHost",
        "WdiSystemHost", "Schedule", "Themes", "AudioEndpointBuilder", "Audiosrv",
        "UserManager", "StateRepository", "StorSvc", "SystemEventsBroker",
        "TimeBrokerSvc", "TokenBroker", "CoreMessagingRegistrar",
        // Networking
        "Netman", "NlaSvc", "netprofm", "Wcmsvc", "WlanSvc", "iphlpsvc",
        "BFE", "mpssvc", "nsi",
        // Drivers / hardware
        "NTDS", "W32Time", "FontCache", "gpsvc",
        // Power / shell
        "Power", "ProfSvc", "ShellHWDetection", "SysMain", "TabletInputService",
    };

    private static readonly HashSet<string> ProtectedTaskPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        @"\Microsoft\Windows\",
        @"\Microsoft\Office\",
        @"\Microsoft\Edge\",
        @"\Microsoft\VisualStudio\",
    };

    // ═══════════════════════════════════════════════════════
    //  VALIDATION METHODS
    // ═══════════════════════════════════════════════════════

    /// <summary>Returns true if the file/folder path is safe to delete</summary>
    public static bool IsPathSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = Path.GetFullPath(path).TrimEnd('\\');

        // Never delete protected files
        if (ProtectedFiles.Contains(normalized)) return false;

        // Never delete anything directly IN a protected directory (the directory itself)
        if (ProtectedDirectories.Contains(normalized)) return false;

        // Never delete if path IS a drive root
        if (normalized.Length <= 3) return false; // "C:\" or "C:"

        // Never delete the Users folder itself or any user profile root
        if (normalized.Equals(@"C:\Users", StringComparison.OrdinalIgnoreCase)) return false;

        // Don't delete if a parent is a strict protected directory
        foreach (var protectedDir in ProtectedDirectories)
        {
            // If the path IS the protected dir, block
            if (normalized.Equals(protectedDir, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Never delete Windows system files (exe, dll, sys in System32)
        if (normalized.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"C:\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(normalized).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".sys" or ".drv" or ".ocx" or ".cpl" or ".msc")
                return false;
        }

        return true;
    }

    /// <summary>Returns true if the registry path is safe to delete</summary>
    public static bool IsRegistryPathSafeToDelete(string regPath)
    {
        if (string.IsNullOrWhiteSpace(regPath)) return false;

        foreach (var protectedRoot in ProtectedRegistryRoots)
        {
            if (regPath.StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>Returns true if the service is safe to modify/delete</summary>
    public static bool IsServiceSafeToModify(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return false;
        return !ProtectedServiceNames.Contains(serviceName);
    }

    /// <summary>Returns true if the scheduled task path is safe to delete</summary>
    public static bool IsTaskSafeToDelete(string taskPath)
    {
        if (string.IsNullOrWhiteSpace(taskPath)) return false;
        return !ProtectedTaskPaths.Any(p =>
            taskPath.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns true if the autorun entry is safe to delete (not a Windows component)</summary>
    public static bool IsAutorunSafeToDelete(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var lower = command.ToLowerInvariant();

        // Protect Windows components
        if (lower.Contains(@"c:\windows\system32") ||
            lower.Contains(@"c:\windows\syswow64") ||
            lower.Contains("securityhealthsystray") ||
            lower.Contains("windowsdefender") ||
            lower.Contains("ctfmon.exe") ||
            lower.Contains("onedrive") ||
            lower.Contains("msedge"))
            return false;

        return true;
    }

    /// <summary>Validate a junk file path — extra conservative for temp cleaning</summary>
    public static bool IsJunkPathSafeToDelete(string path)
    {
        if (!IsPathSafeToDelete(path)) return false;

        // Extra checks for junk cleaning
        var normalized = Path.GetFullPath(path);

        // Never touch files in active user's profile root
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (normalized.Equals(userProfile, StringComparison.OrdinalIgnoreCase)) return false;

        // Only delete from known-safe parent locations for junk
        var safeJunkParents = new[]
        {
            Path.GetTempPath(),
            @"C:\Windows\Temp",
            @"C:\Windows\Prefetch",
            @"C:\Windows\Logs",
            @"C:\Windows\SoftwareDistribution\Download",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"C:\Windows\Minidump",
            @"C:\ProgramData\Microsoft\Windows\WER",
        };

        return safeJunkParents.Any(parent =>
            !string.IsNullOrEmpty(parent) &&
            normalized.StartsWith(parent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Get a human-readable safety assessment</summary>
    public static (bool Safe, string Reason) AssessOperation(string operationType, string target)
    {
        switch (operationType)
        {
            case "DeleteFile":
            case "DeleteFolder":
                if (!IsPathSafeToDelete(target))
                    return (false, $"Protected system path: {target}");
                break;
            case "DeleteRegistry":
                if (!IsRegistryPathSafeToDelete(target))
                    return (false, $"Protected registry key: {target}");
                break;
            case "DeleteService":
                if (!IsServiceSafeToModify(target))
                    return (false, $"Protected Windows service: {target}");
                break;
            case "DeleteTask":
                if (!IsTaskSafeToDelete(target))
                    return (false, $"Protected Windows task: {target}");
                break;
        }
        return (true, "OK");
    }
}
