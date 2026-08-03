using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace DeepPurge.Core.InstallMonitor;

internal sealed record UsnPathNode(
    UsnFileId FileReferenceNumber,
    UsnFileId ParentFileReferenceNumber,
    string Name);

internal sealed record ParsedUsnRecord(
    UsnFileId FileReferenceNumber,
    UsnFileId ParentFileReferenceNumber,
    long Usn,
    long TimestampFileTime,
    uint Reason,
    uint FileAttributes,
    string Name,
    ushort MajorVersion);

/// <summary>
/// Reads NTFS change-journal records as diagnostic evidence. A journal record
/// contains a leaf name and parent file reference, not an absolute path, so
/// every path is reconstructed from an MFT index. Unresolved records remain
/// explicitly unresolved and are never converted into a root-relative path.
/// </summary>
public static class UsnJournalReader
{
    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const uint FsctlCreateUsnJournal = 0x000900e7;
    private const uint FsctlEnumUsnData = 0x000900b3;

    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint FileShareDelete = 4;
    private const uint OpenExisting = 3;

    private const uint UsnReasonDataOverwrite = 0x00000001;
    private const uint UsnReasonDataExtend = 0x00000002;
    private const uint UsnReasonDataTruncation = 0x00000004;
    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOld = 0x00001000;
    private const uint UsnReasonRenameNew = 0x00002000;

    private const int ErrorHandleEof = 38;
    private const int JournalBufferSize = 64 * 1024;
    private const int MftBufferSize = 1024 * 1024;

    public static bool IsSupported(string volumeRoot)
    {
        try
        {
            if (!VolumeFileSystem.IsNtfs(volumeRoot)) return false;
            using var handle = OpenVolume(volumeRoot);
            return QueryJournal(handle, out _);
        }
        catch
        {
            return false;
        }
    }

    public static long GetCurrentUsn(string volumeRoot)
    {
        if (!VolumeFileSystem.IsNtfs(volumeRoot)) return -1;
        using var handle = OpenVolume(volumeRoot);
        return QueryJournal(handle, out var data) ? data.NextUsn : -1;
    }

    /// <summary>
    /// Explicit maintenance API retained for callers that intentionally manage
    /// the journal. Install tracing no longer creates or resizes a journal.
    /// </summary>
    public static void EnsureJournalSize(
        string volumeRoot,
        long desiredBytes = 64 * 1024 * 1024)
    {
        if (!VolumeFileSystem.IsNtfs(volumeRoot)) return;

        try
        {
            using var handle = OpenVolume(volumeRoot);
            var create = new CreateUsnJournalData
            {
                MaximumSize = (ulong)desiredBytes,
                AllocationDelta = (ulong)(desiredBytes / 8),
            };
            DeviceIoControl(
                handle,
                FsctlCreateUsnJournal,
                ref create,
                Marshal.SizeOf<CreateUsnJournalData>(),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"EnsureJournalSize: {ex.Message}");
        }
    }

    public static List<UsnChange> ReadChangesSince(
        string volumeRoot,
        long startUsn)
    {
        var changes = new List<UsnChange>();
        if (startUsn < 0 || !VolumeFileSystem.IsNtfs(volumeRoot))
            return changes;

        try
        {
            using var handle = OpenVolume(volumeRoot);
            if (!QueryJournal(handle, out var journalData))
                return changes;

            var nodes = BuildMftIndex(handle);
            var readData = new ReadUsnJournalDataV0
            {
                StartUsn = startUsn,
                ReasonMask = UsnReasonFileCreate |
                             UsnReasonFileDelete |
                             UsnReasonRenameOld |
                             UsnReasonRenameNew |
                             UsnReasonDataExtend |
                             UsnReasonDataOverwrite |
                             UsnReasonDataTruncation,
                ReturnOnlyOnClose = 1,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = journalData.UsnJournalId,
            };

            var buffer = Marshal.AllocHGlobal(JournalBufferSize);
            try
            {
                while (true)
                {
                    if (!DeviceIoControl(
                            handle,
                            FsctlReadUsnJournal,
                            ref readData,
                            Marshal.SizeOf<ReadUsnJournalDataV0>(),
                            buffer,
                            JournalBufferSize,
                            out var bytesReturned,
                            IntPtr.Zero) ||
                        bytesReturned <= sizeof(long))
                        break;

                    var managed = new byte[bytesReturned];
                    Marshal.Copy(buffer, managed, 0, managed.Length);
                    var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(managed);
                    var offset = sizeof(long);
                    while (offset + 8 <= managed.Length)
                    {
                        var recordLength = BinaryPrimitives.ReadInt32LittleEndian(
                            managed.AsSpan(offset, sizeof(int)));
                        if (recordLength <= 0 ||
                            offset + recordLength > managed.Length)
                            break;

                        var parsed = TryParseRecord(
                            managed.AsSpan(offset, recordLength));
                        if (parsed != null &&
                            TryClassifyReason(parsed.Reason, out var changeKind))
                        {
                            var path = ResolvePath(
                                volumeRoot,
                                parsed.ParentFileReferenceNumber,
                                parsed.Name,
                                nodes,
                                out var resolved);
                            changes.Add(new UsnChange(
                                path,
                                changeKind,
                                ToUtc(parsed.TimestampFileTime),
                                parsed.FileReferenceNumber,
                                parsed.ParentFileReferenceNumber,
                                resolved,
                                parsed.Reason,
                                parsed.MajorVersion));
                        }

                        offset += recordLength;
                    }

                    if (nextUsn <= readData.StartUsn) break;
                    readData.StartUsn = nextUsn;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ReadChangesSince: {ex.Message}");
        }

        return changes;
    }

    internal static ParsedUsnRecord? TryParseRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 8) return null;

        var recordLength = BinaryPrimitives.ReadInt32LittleEndian(record);
        if (recordLength < 8 || recordLength > record.Length) return null;

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int fileIdOffset;
        int parentIdOffset;
        int usnOffset;
        int timestampOffset;
        int reasonOffset;
        int attributesOffset;
        int nameLengthOffset;
        int nameOffsetOffset;
        int idBytes;

        switch (majorVersion)
        {
            case 2:
                fileIdOffset = 8;
                parentIdOffset = 16;
                usnOffset = 24;
                timestampOffset = 32;
                reasonOffset = 40;
                attributesOffset = 52;
                nameLengthOffset = 56;
                nameOffsetOffset = 58;
                idBytes = 8;
                break;
            case 3:
                fileIdOffset = 8;
                parentIdOffset = 24;
                usnOffset = 40;
                timestampOffset = 48;
                reasonOffset = 56;
                attributesOffset = 68;
                nameLengthOffset = 72;
                nameOffsetOffset = 74;
                idBytes = 16;
                break;
            default:
                return null;
        }

        if (recordLength < nameOffsetOffset + sizeof(ushort))
            return null;

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            record[nameLengthOffset..]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(
            record[nameOffsetOffset..]);
        if ((nameLength & 1) != 0 ||
            nameOffset < nameOffsetOffset + sizeof(ushort) ||
            nameOffset + nameLength > recordLength)
            return null;

        var name = Encoding.Unicode.GetString(
            record.Slice(nameOffset, nameLength));
        if (!IsValidLeafName(name)) return null;

        return new ParsedUsnRecord(
            ReadFileId(record, fileIdOffset, idBytes),
            ReadFileId(record, parentIdOffset, idBytes),
            BinaryPrimitives.ReadInt64LittleEndian(record[usnOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(record[timestampOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[reasonOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[attributesOffset..]),
            name,
            majorVersion);
    }

    internal static string ResolvePath(
        string volumeRoot,
        UsnFileId parentFileReference,
        string leafName,
        IReadOnlyDictionary<UsnFileId, UsnPathNode> nodes,
        out bool resolved)
    {
        resolved = false;
        if (!IsValidLeafName(leafName))
            return $"<unresolved:{parentFileReference}>\\<invalid-name>";

        var parts = new Stack<string>();
        parts.Push(leafName);
        var cursor = parentFileReference;
        var seen = new HashSet<UsnFileId>();

        for (var depth = 0; depth < 256; depth++)
        {
            if (IsNtfsRoot(cursor))
            {
                resolved = true;
                break;
            }

            if (!seen.Add(cursor) ||
                !nodes.TryGetValue(cursor, out var parent))
                break;

            if (IsNtfsRoot(parent.FileReferenceNumber) ||
                parent.ParentFileReferenceNumber == parent.FileReferenceNumber)
            {
                resolved = true;
                if (IsValidPathComponent(parent.Name) &&
                    parent.Name is not "." and not "\\")
                    parts.Push(parent.Name);
                break;
            }

            if (!IsValidPathComponent(parent.Name))
                break;

            parts.Push(parent.Name);
            cursor = parent.ParentFileReferenceNumber;
        }

        if (!resolved)
            return $"<unresolved:{parentFileReference}>\\{leafName}";

        var root = Path.GetPathRoot(volumeRoot) ?? volumeRoot;
        return Path.Combine(new[] { root }.Concat(parts));
    }

    private static Dictionary<UsnFileId, UsnPathNode> BuildMftIndex(
        SafeFileHandle handle)
    {
        var nodes = new Dictionary<UsnFileId, UsnPathNode>();
        var enumData = new MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue,
        };
        var input = Marshal.AllocHGlobal(Marshal.SizeOf<MftEnumDataV0>());
        var output = Marshal.AllocHGlobal(MftBufferSize);
        try
        {
            Marshal.StructureToPtr(enumData, input, fDeleteOld: false);
            while (true)
            {
                if (!DeviceIoControl(
                        handle,
                        FsctlEnumUsnData,
                        input,
                        (uint)Marshal.SizeOf<MftEnumDataV0>(),
                        output,
                        MftBufferSize,
                        out var bytesReturned,
                        IntPtr.Zero))
                {
                    if (Marshal.GetLastWin32Error() == ErrorHandleEof) break;
                    return new Dictionary<UsnFileId, UsnPathNode>();
                }

                if (bytesReturned <= sizeof(long)) break;
                var managed = new byte[bytesReturned];
                Marshal.Copy(output, managed, 0, managed.Length);
                var nextFileReference =
                    BinaryPrimitives.ReadInt64LittleEndian(managed);
                var offset = sizeof(long);
                while (offset + 8 <= managed.Length)
                {
                    var recordLength = BinaryPrimitives.ReadInt32LittleEndian(
                        managed.AsSpan(offset, sizeof(int)));
                    if (recordLength <= 0 ||
                        offset + recordLength > managed.Length)
                        break;

                    var parsed = TryParseRecord(
                        managed.AsSpan(offset, recordLength));
                    if (parsed != null)
                    {
                        nodes[parsed.FileReferenceNumber] = new UsnPathNode(
                            parsed.FileReferenceNumber,
                            parsed.ParentFileReferenceNumber,
                            parsed.Name);
                    }
                    offset += recordLength;
                }

                Marshal.WriteInt64(input, nextFileReference);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }

        return nodes;
    }

    private static UsnFileId ReadFileId(
        ReadOnlySpan<byte> record,
        int offset,
        int byteCount)
    {
        var low = BinaryPrimitives.ReadUInt64LittleEndian(record[offset..]);
        var high = byteCount == 16
            ? BinaryPrimitives.ReadUInt64LittleEndian(record[(offset + 8)..])
            : 0;
        return new UsnFileId(low, high);
    }

    private static bool TryClassifyReason(
        uint reason,
        out InstallObjectChangeKind changeKind)
    {
        if ((reason & UsnReasonFileCreate) != 0)
            changeKind = InstallObjectChangeKind.Created;
        else if ((reason & UsnReasonFileDelete) != 0)
            changeKind = InstallObjectChangeKind.Deleted;
        else if ((reason & (UsnReasonRenameOld | UsnReasonRenameNew)) != 0)
            changeKind = InstallObjectChangeKind.Renamed;
        else if ((reason & (UsnReasonDataExtend |
                            UsnReasonDataOverwrite |
                            UsnReasonDataTruncation)) != 0)
            changeKind = InstallObjectChangeKind.Modified;
        else
            changeKind = InstallObjectChangeKind.Unknown;
        return changeKind != InstallObjectChangeKind.Unknown;
    }

    private static DateTime ToUtc(long fileTime)
    {
        if (fileTime <= 0) return DateTime.UtcNow;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static bool IsValidLeafName(string name)
        => IsValidPathComponent(name) && name is not "." and not "..";

    private static bool IsValidPathComponent(string name)
        => !string.IsNullOrWhiteSpace(name) &&
           !Path.IsPathRooted(name) &&
           name.IndexOfAny(['\\', '/', '\0']) < 0;

    private static bool IsNtfsRoot(UsnFileId id)
        => id.HighPart == 0 &&
           (id.LowPart & 0x0000FFFFFFFFFFFFUL) == 5;

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        var drive = Path.GetPathRoot(volumeRoot)?.TrimEnd('\\') ?? "C:";
        var path = $@"\\.\{drive}";
        var handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error());
        return handle;
    }

    private static bool QueryJournal(
        SafeFileHandle handle,
        out UsnJournalDataV0 data)
    {
        data = default;
        return DeviceIoControl(
            handle,
            FsctlQueryUsnJournal,
            IntPtr.Zero,
            0,
            out data,
            Marshal.SizeOf<UsnJournalDataV0>(),
            out _,
            IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        ref ReadUsnJournalDataV0 input,
        int inputSize,
        IntPtr output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        ref CreateUsnJournalData input,
        int inputSize,
        IntPtr output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr input,
        int inputSize,
        out UsnJournalDataV0 output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr input,
        uint inputSize,
        IntPtr output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateUsnJournalData
    {
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumDataV0
    {
        public long StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }
}
