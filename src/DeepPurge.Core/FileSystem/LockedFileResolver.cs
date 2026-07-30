using System.Runtime.InteropServices;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.FileSystem;

public record LockingProcess(int ProcessId, string ProcessName, string Description);

public static class LockedFileResolver
{
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle,
        uint nFiles, string[] rgsFileNames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint pSessionHandle,
        out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    public static List<LockingProcess> GetLockingProcesses(string filePath)
    {
        var result = new List<LockingProcess>();
        uint sessionHandle;
        var key = Guid.NewGuid().ToString("N");

        int err = RmStartSession(out sessionHandle, 0, key);
        if (err != 0) return result;

        try
        {
            err = RmRegisterResources(sessionHandle, 1, new[] { filePath }, 0, null, 0, null);
            if (err != 0) return result;

            uint needed = 0, count = 0, rebootReasons = 0;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                err = RmGetList(sessionHandle, out needed, ref count, null, ref rebootReasons);
                if (err != 234 || needed == 0 || needed > 1024) break; // not ERROR_MORE_DATA

                var processes = new RM_PROCESS_INFO[needed];
                count = needed;
                err = RmGetList(sessionHandle, out needed, ref count, processes, ref rebootReasons);

                if (err == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        result.Add(new LockingProcess(
                            processes[i].Process.dwProcessId,
                            processes[i].strAppName ?? "",
                            $"PID {processes[i].Process.dwProcessId}"));
                    }
                    break;
                }

                if (err != 234) break; // not a sizing race — stop retrying
            }
        }
        catch (Exception ex) { Log.Warn($"Restart Manager query failed for '{filePath}': {ex.Message}"); }
        finally
        {
            RmEndSession(sessionHandle);
        }

        return result;
    }
}
