namespace DeepPurge.Core.Safety;

/// <summary>
/// Centralized safety validation for all destructive operations.
/// Every delete/modify operation MUST pass through SafetyGuard before execution.
/// </summary>
public static class SafetyGuard
{
    // ═══════════════════════════════════════════════════════
    //  DYNAMIC PATH ROOTS — never hardcode drive letters
    // ═══════════════════════════════════════════════════════

    private static readonly string WinDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Sys32 =
        Environment.SystemDirectory;
    private static readonly string SysWow64 =
        Path.Combine(WinDir, "SysWOW64");
    private static readonly string ProgramFiles =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string ProgramData =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string SystemDrive =
        Path.GetPathRoot(WinDir) ?? @"C:\";
    internal static readonly string UsersDir =
        Path.Combine(SystemDrive.TrimEnd('\\'), "Users");

    // ═══════════════════════════════════════════════════════
    //  PROTECTED PATHS — NEVER delete anything under these
    // ═══════════════════════════════════════════════════════

    private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        WinDir,
        Sys32,
        SysWow64,
        Path.Combine(WinDir, "WinSxS"),
        Path.Combine(WinDir, "Boot"),
        Path.Combine(WinDir, "Fonts"),
        Path.Combine(WinDir, "Globalization"),
        Path.Combine(WinDir, "IME"),
        Path.Combine(WinDir, "rescache"),
        Path.Combine(WinDir, "Resources"),
        Path.Combine(WinDir, "servicing"),
        Path.Combine(WinDir, "SystemResources"),
        Path.Combine(ProgramFiles, "Windows Defender"),
        Path.Combine(ProgramFiles, "Windows Security"),
        Path.Combine(ProgramFiles, "Common Files", "microsoft shared"),
        Path.Combine(ProgramFilesX86, "Common Files"),
        Path.Combine(ProgramData, "Microsoft", "Windows"),
        Path.Combine(ProgramData, "Microsoft", "Windows Defender"),
        Path.Combine(SystemDrive.TrimEnd('\\'), "Recovery"),
        Path.Combine(SystemDrive.TrimEnd('\\'), "$Recycle.Bin"),
    };

    private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine(Sys32, "config", "SYSTEM"),
        Path.Combine(Sys32, "config", "SOFTWARE"),
        Path.Combine(Sys32, "config", "SAM"),
        Path.Combine(Sys32, "config", "SECURITY"),
        Path.Combine(Sys32, "config", "DEFAULT"),
        Path.Combine(SystemDrive.TrimEnd('\\'), "bootmgr"),
        Path.Combine(SystemDrive.TrimEnd('\\'), "BOOTNXT"),
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

    /// <summary>
    /// Firewall rule display-name prefixes that must never be deleted.
    /// These are Windows Defender / Core Networking / system rules.
    /// </summary>
    private static readonly string[] ProtectedFirewallPrefixes =
    {
        "Core Networking",
        "Windows Defender",
        "Remote Desktop",
        "File and Printer Sharing",
        "Network Discovery",
        "@FirewallAPI.dll",
        "@%SystemRoot%",
    };

    // ═══════════════════════════════════════════════════════
    //  VALIDATION METHODS
    // ═══════════════════════════════════════════════════════

    /// <summary>Returns true if the file/folder path is safe to delete</summary>
    public static bool IsPathSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (path.Contains("..")) return false;

        var normalized = Path.GetFullPath(path).TrimEnd('\\');

        // User-defined exclusion list (global whitelist of protected paths)
        var excluded = App.AppSettings.Current.ExcludedPaths;
        if (excluded.Count > 0 && excluded.Any(ex =>
            normalized.StartsWith(Path.GetFullPath(ex).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return false;

        // Never delete protected files
        if (ProtectedFiles.Contains(normalized)) return false;

        // Never delete anything directly IN a protected directory (the directory itself)
        if (ProtectedDirectories.Contains(normalized)) return false;

        // Never delete if path IS a drive root
        if (normalized.Length <= 3) return false; // "C:\" or "C:"

        // Never delete the Users folder itself or any user profile root
        if (normalized.Equals(UsersDir, StringComparison.OrdinalIgnoreCase)) return false;

        // Don't delete if a parent is a strict protected directory
        foreach (var protectedDir in ProtectedDirectories)
        {
            if (normalized.Equals(protectedDir, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Never delete Windows system files (exe, dll, sys in System32)
        if (normalized.StartsWith(Sys32 + @"\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(SysWow64 + @"\", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Returns true if the firewall rule is safe to delete (not a core Windows rule)</summary>
    public static bool IsFirewallRuleSafeToDelete(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        return !ProtectedFirewallPrefixes.Any(prefix =>
            displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns true if the PATH entry is safe to remove</summary>
    public static bool IsPathEntrySafeToRemove(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var expanded = Environment.ExpandEnvironmentVariables(directory);
        var lower = expanded.ToLowerInvariant();
        // Never remove Windows system paths, .NET runtime paths, or PowerShell paths
        if (lower.Contains(@"\windows\system32") ||
            lower.Contains(@"\windows\syswow64") ||
            lower.Contains(@"\dotnet") ||
            lower.Contains(@"\powershell") ||
            lower.Contains(@"\windowsapps"))
            return false;
        return true;
    }

    /// <summary>Returns true if the autorun entry is safe to delete (not a Windows component)</summary>
    public static bool IsAutorunSafeToDelete(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        if (command.Contains(Sys32, StringComparison.OrdinalIgnoreCase) ||
            command.Contains(SysWow64, StringComparison.OrdinalIgnoreCase) ||
            command.Contains("securityhealthsystray", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("windowsdefender", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("ctfmon.exe", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("onedrive", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("msedge", StringComparison.OrdinalIgnoreCase))
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
            Path.Combine(WinDir, "Temp"),
            Path.Combine(WinDir, "Prefetch"),
            Path.Combine(WinDir, "Logs"),
            Path.Combine(WinDir, "SoftwareDistribution", "Download"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(WinDir, "Minidump"),
            Path.Combine(ProgramData, "Microsoft", "Windows", "WER"),
        };

        return safeJunkParents.Any(parent =>
            !string.IsNullOrEmpty(parent) &&
            normalized.StartsWith(parent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if a registry key is a symbolic link (REG_LINK).
    /// Reads the key class via RegQueryInfoKeyW — symlinks have a non-empty class.
    /// Normal keys have an empty/null class. Callers must NOT write/delete
    /// symlinked keys — an attacker can redirect writes to critical system keys.
    /// </summary>
    public static bool IsRegistrySymlink(Microsoft.Win32.RegistryKey key)
    {
        try
        {
            var field = typeof(Microsoft.Win32.RegistryKey).GetField("_hkey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field == null) return false;
            var handle = field.GetValue(key) as Microsoft.Win32.SafeHandles.SafeRegistryHandle;
            if (handle == null || handle.IsInvalid) return false;

            var classBuffer = new StringBuilder(260);
            int classLen = classBuffer.Capacity;
            int result = RegQueryInfoKeyW(handle.DangerousGetHandle(),
                classBuffer, ref classLen, IntPtr.Zero,
                out _, out _, out _, out _, out _, out _, out _, IntPtr.Zero);

            if (result != 0) return false;
            return classLen > 0 && classBuffer.Length > 0;
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(
        IntPtr hKey, StringBuilder? lpClass, ref int lpcchClass, IntPtr lpReserved,
        out int lpcSubKeys, out int lpcbMaxSubKeyLen, out int lpcbMaxClassLen,
        out int lpcValues, out int lpcbMaxValueNameLen, out int lpcbMaxValueLen,
        out int lpcbSecurityDescriptor, IntPtr lpftLastWriteTime);

    /// <summary>Returns true if the path is a reparse point (symlink, junction, mount point). Callers must NOT recurse into reparse points during deletion.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Enumerates files recursively while skipping child reparse points (junctions, symlinks).
    /// Prevents a junction under a safe directory from redirecting enumeration into unrelated data.
    /// </summary>
    public static IEnumerable<string> SafeEnumerateFiles(string root, string pattern = "*")
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> topFiles;
        try { topFiles = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var f in topFiles) yield return f;

        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var dir in subDirs)
        {
            if (IsReparsePoint(dir)) continue;
            foreach (var f in SafeEnumerateFiles(dir, pattern)) yield return f;
        }
    }

    /// <summary>
    /// Enumerates subdirectories recursively while skipping child reparse points.
    /// Results are deepest-first (longest path first) for safe bottom-up deletion.
    /// </summary>
    public static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch { yield break; }

        foreach (var dir in subDirs)
        {
            if (IsReparsePoint(dir)) continue;
            foreach (var child in SafeEnumerateDirectories(dir)) yield return child;
            yield return dir;
        }
    }

    /// <summary>
    /// Recursively deletes a directory tree, skipping child reparse points.
    /// Safer alternative to Directory.Delete(path, recursive: true).
    /// </summary>
    public static bool SafeDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        if (!IsPathSafeToDelete(path)) return false;

        try
        {
            foreach (var file in SafeEnumerateFiles(path))
                SafeDeleteFile(file);

            foreach (var dir in SafeEnumerateDirectories(path))
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: false); }
                catch (Exception ex) { Diagnostics.Log.Warn($"SafeDelete dir '{dir}': {ex.Message}"); }
            }

            try { Directory.Delete(path, recursive: false); }
            catch { /* root may not be empty if some files were locked */ }
            return !Directory.Exists(path);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"SafeDeleteDirectory '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes a single file. If deletion fails with a sharing violation,
    /// queries the Restart Manager for locking processes and queues the file
    /// for delete-on-reboot as a fallback.
    /// </summary>
    public static bool SafeDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        {
            var lockers = FileSystem.LockedFileResolver.GetLockingProcesses(path);
            if (lockers.Count > 0)
                Diagnostics.Log.Warn($"Locked by: {string.Join(", ", lockers.Select(l => $"{l.ProcessName} (PID {l.ProcessId})"))} — '{path}'");

            if (FileSystem.LockedFileResolver.QueueDeleteOnReboot(path))
            {
                Diagnostics.Log.Info($"Queued for delete-on-reboot: '{path}'");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"SafeDeleteFile '{path}': {ex.Message}");
            return false;
        }
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
            case "DeleteFirewallRule":
                if (!IsFirewallRuleSafeToDelete(target))
                    return (false, $"Protected Windows firewall rule: {target}");
                break;
            case "RemovePathEntry":
                if (!IsPathEntrySafeToRemove(target))
                    return (false, $"Protected system PATH entry: {target}");
                break;
        }
        return (true, "OK");
    }
}
