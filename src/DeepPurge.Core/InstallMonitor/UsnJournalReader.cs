using System.Runtime.InteropServices;
using DeepPurge.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace DeepPurge.Core.InstallMonitor;

public record UsnChange(string Path, UsnChangeReason Reason, DateTime TimestampUtc);

public enum UsnChangeReason { Created, Modified, Renamed, Deleted }

public class UsnJournalReader
{
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_READ_USN_JOURNAL  = 0x000900bb;
    private const uint FSCTL_CREATE_USN_JOURNAL = 0x000900e7;

    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    private const uint USN_REASON_FILE_CREATE   = 0x00000100;
    private const uint USN_REASON_FILE_DELETE   = 0x00000200;
    private const uint USN_REASON_RENAME_NEW    = 0x00002000;
    private const uint USN_REASON_DATA_EXTEND   = 0x00000002;
    private const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    private const uint USN_REASON_DATA_TRUNCATION = 0x00000004;

    public static bool IsSupported(string volumeRoot)
    {
        try
        {
            using var h = OpenVolume(volumeRoot);
            return QueryJournal(h, out _);
        }
        catch { return false; }
    }

    public static long GetCurrentUsn(string volumeRoot)
    {
        using var h = OpenVolume(volumeRoot);
        if (!QueryJournal(h, out var data)) return -1;
        return data.NextUsn;
    }

    public static void EnsureJournalSize(string volumeRoot, long desiredBytes = 64 * 1024 * 1024)
    {
        try
        {
            using var h = OpenVolume(volumeRoot);
            var create = new CREATE_USN_JOURNAL_DATA
            {
                MaximumSize = (ulong)desiredBytes,
                AllocationDelta = (ulong)(desiredBytes / 8),
            };
            int bytesReturned;
            DeviceIoControl(h, FSCTL_CREATE_USN_JOURNAL, ref create,
                Marshal.SizeOf<CREATE_USN_JOURNAL_DATA>(), IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"EnsureJournalSize: {ex.Message}");
        }
    }

    public static List<UsnChange> ReadChangesSince(string volumeRoot, long startUsn)
    {
        var changes = new List<UsnChange>();
        try
        {
            using var h = OpenVolume(volumeRoot);
            if (!QueryJournal(h, out var journalData)) return changes;

            var readData = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = startUsn,
                ReasonMask = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE |
                             USN_REASON_RENAME_NEW | USN_REASON_DATA_EXTEND |
                             USN_REASON_DATA_OVERWRITE | USN_REASON_DATA_TRUNCATION,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalID = journalData.UsnJournalID,
            };

            const int bufferSize = 64 * 1024;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                while (true)
                {
                    int bytesReturned;
                    bool ok = DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, ref readData,
                        Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(), buffer, bufferSize,
                        out bytesReturned, IntPtr.Zero);
                    if (!ok || bytesReturned <= sizeof(long)) break;

                    long nextUsn = Marshal.ReadInt64(buffer);
                    int offset = sizeof(long);
                    while (offset < bytesReturned)
                    {
                        var recordLen = Marshal.ReadInt32(buffer + offset);
                        if (recordLen <= 0) break;

                        var record = ParseRecord(buffer + offset, volumeRoot);
                        if (record != null) changes.Add(record);

                        offset += recordLen;
                    }
                    readData.StartUsn = nextUsn;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex)
        {
            Log.Warn($"ReadChangesSince: {ex.Message}");
        }
        return changes;
    }

    private static UsnChange? ParseRecord(IntPtr ptr, string volumeRoot)
    {
        int fileNameOffset = Marshal.ReadInt16(ptr + 58);
        int fileNameLength = Marshal.ReadInt16(ptr + 56);
        uint reason = (uint)Marshal.ReadInt32(ptr + 40);

        if (fileNameLength <= 0) return null;

        var fileName = Marshal.PtrToStringUni(ptr + fileNameOffset, fileNameLength / 2) ?? "";
        var changeReason = ClassifyReason(reason);
        if (changeReason == null) return null;

        var timestampTicks = Marshal.ReadInt64(ptr + 32);
        var timestamp = timestampTicks > 0 ? DateTime.FromFileTimeUtc(timestampTicks) : DateTime.UtcNow;

        return new UsnChange(Path.Combine(volumeRoot, fileName), changeReason.Value, timestamp);
    }

    private static UsnChangeReason? ClassifyReason(uint reason)
    {
        if ((reason & USN_REASON_FILE_CREATE) != 0) return UsnChangeReason.Created;
        if ((reason & USN_REASON_FILE_DELETE) != 0) return UsnChangeReason.Deleted;
        if ((reason & USN_REASON_RENAME_NEW) != 0) return UsnChangeReason.Renamed;
        if ((reason & (USN_REASON_DATA_EXTEND | USN_REASON_DATA_OVERWRITE | USN_REASON_DATA_TRUNCATION)) != 0) return UsnChangeReason.Modified;
        return null;
    }

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        var drive = Path.GetPathRoot(volumeRoot)?.TrimEnd('\\') ?? "C:";
        var path = $@"\\.\{drive}";
        var h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return h;
    }

    private static bool QueryJournal(SafeFileHandle h, out USN_JOURNAL_DATA_V0 data)
    {
        data = default;
        int bytesReturned;
        return DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0,
            out data, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(), out bytesReturned, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref READ_USN_JOURNAL_DATA_V0 lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref CREATE_USN_JOURNAL_DATA lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        out USN_JOURNAL_DATA_V0 lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATE_USN_JOURNAL_DATA
    {
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }
}
