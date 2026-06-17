using System.Security.Principal;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.App;

public static class UserIdentity
{
    private static readonly Lazy<string> _realUserSid = new(ResolveRealUserSid);
    private static readonly Lazy<string> _realLocalAppData = new(ResolveRealLocalAppData);
    private static readonly Lazy<bool> _isSmaaElevated = new(DetectSmaaElevation);

    public static string RealUserSid => _realUserSid.Value;
    public static string RealLocalAppData => _realLocalAppData.Value;
    public static bool IsSmaaElevated => _isSmaaElevated.Value;

    private static bool DetectSmaaElevation()
    {
        try
        {
            var currentIdentity = WindowsIdentity.GetCurrent();
            var currentSid = currentIdentity.User?.Value;
            if (string.IsNullOrEmpty(currentSid)) return false;

            var consoleSid = GetConsoleSessionUserSid();
            if (string.IsNullOrEmpty(consoleSid)) return false;

            return !string.Equals(currentSid, consoleSid, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ResolveRealUserSid()
    {
        try
        {
            var consoleSid = GetConsoleSessionUserSid();
            if (!string.IsNullOrEmpty(consoleSid)) return consoleSid;
        }
        catch (Exception ex) { Log.Warn($"Console SID resolution failed: {ex.Message}"); }

        try { return WindowsIdentity.GetCurrent().User?.Value ?? ""; }
        catch { return ""; }
    }

    private static string ResolveRealLocalAppData()
    {
        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!IsSmaaElevated) return fallback;

        try
        {
            var sid = RealUserSid;
            if (string.IsNullOrEmpty(sid)) return fallback;

            using var profileKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            var profilePath = profileKey?.GetValue("ProfileImagePath") as string;
            if (!string.IsNullOrEmpty(profilePath) && Directory.Exists(profilePath))
            {
                var localAppData = Path.Combine(profilePath, "AppData", "Local");
                if (Directory.Exists(localAppData)) return localAppData;
            }
        }
        catch (Exception ex) { Log.Warn($"SMAA LocalAppData resolution failed: {ex.Message}"); }

        return fallback;
    }

    private static string? GetConsoleSessionUserSid()
    {
        try
        {
            var explorerProcesses = System.Diagnostics.Process.GetProcessesByName("explorer");
            if (explorerProcesses.Length == 0) return null;

            foreach (var explorer in explorerProcesses)
            {
                try
                {
                    var handle = explorer.Handle;
                    if (!OpenProcessToken(handle, TOKEN_QUERY, out var tokenHandle)) continue;
                    try
                    {
                        using var identity = new WindowsIdentity(tokenHandle);
                        return identity.User?.Value;
                    }
                    finally { CloseHandle(tokenHandle); }
                }
                catch { continue; }
                finally { explorer.Dispose(); }
            }
        }
        catch { }
        return null;
    }

    public static Microsoft.Win32.RegistryKey? OpenRealUserHive(string subKey)
    {
        var sid = RealUserSid;
        if (string.IsNullOrEmpty(sid)) return null;
        try
        {
            return Microsoft.Win32.Registry.Users.OpenSubKey($@"{sid}\{subKey}");
        }
        catch { return null; }
    }

    private const uint TOKEN_QUERY = 0x0008;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
