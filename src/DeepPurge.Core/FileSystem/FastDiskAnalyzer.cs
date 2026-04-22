using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeepPurge.Core.FileSystem;

/// <summary>
/// Borrowed directly from WizTree's approach (<see href="https://diskanalyzer.com"/>):
/// the single biggest reason WizTree scans a 1TB SSD in seconds is that it reads
/// the NTFS <c>$MFT</c> sequentially via <c>FSCTL_ENUM_USN_DATA</c> instead of
/// walking the filesystem with <c>FindFirstFile / FindNextFile</c>. One sequential
/// read of the MFT emits every record on the volume, so we avoid the millions of
/// random seeks the directory-walk approach costs.
///
/// Implementation notes:
/// - <c>FSCTL_ENUM_USN_DATA</c> returns <c>USN_RECORD_V2</c> entries which carry the
///   file's FRN, parent FRN, name, and attributes — but <b>not</b> the size.
/// - To get sizes we do a single <c>FSCTL_GET_NTFS_FILE_RECORD</c> pass per record.
///   That is the expensive part, but still far faster than FindFirstFile because
///   we're already holding the volume handle and the MFT is warm.
/// - Paths are reconstructed by linking each record's <c>ParentFileReferenceNumber</c>
///   up the FRN chain to the volume root (FRN 0x5).
/// - Requires Administrator + NTFS. For ReFS, FAT32, or permission failures we
///   fall back to a parallel <c>FindFirstFileExW(FIND_FIRST_EX_LARGE_FETCH)</c>
///   traversal — still significantly faster than the legacy <see cref="Directory.EnumerateFiles"/>
///   because the large-fetch flag tells NTFS to stream directory entries in
///   bigger batches and <see cref="FindExInfoBasic"/> skips the 8.3 short-name lookup.
/// </summary>
public static class FastDiskAnalyzer
{
    private const int MinLargeFileBytes = 50 * 1024 * 1024; // 50 MB — same threshold as the old analyzer.

    // ───── public API ────────────────────────────────────────────

    public static List<DiskFolderInfo> AnalyzeDrive(
        string drivePath,
        CancellationToken ct = default)
    {
        // Happy path: NTFS + admin → one MFT sweep gives every file on the volume.
        if (TryMftScan(drivePath, out var mftFolders, ct))
            return AggregateTopLevel(drivePath, mftFolders);

        // Fallback: fast directory walk (still uses the WizTree-style
        // FIND_FIRST_EX_LARGE_FETCH hint).
        return AnalyzeFolderFast(drivePath, ct);
    }

    public static List<LargeFileInfo> FindLargeFiles(
        string drivePath,
        long minSizeBytes = MinLargeFileBytes,
        int maxResults = 200,
        CancellationToken ct = default)
    {
        // MFT scan yields every file with a size already, so the "large files"
        // view is free — no second traversal needed.
        if (TryMftFileList(drivePath, minSizeBytes, out var hits, ct))
        {
            return hits
                .OrderByDescending(f => f.SizeBytes)
                .Take(maxResults)
                .ToList();
        }

        return FindLargeFilesFallback(drivePath, minSizeBytes, maxResults, ct);
    }

    // ───── MFT scan (WizTree technique) ──────────────────────────

    private static bool TryMftScan(
        string drivePath,
        out Dictionary<ulong, MftNode> nodes,
        CancellationToken ct)
    {
        nodes = new();
        try
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(drivePath));
            if (string.IsNullOrEmpty(volumeRoot)) return false;
            if (!IsNtfs(volumeRoot)) return false;

            using var handle = OpenVolume(volumeRoot);
            if (handle.IsInvalid) return false;

            var usnEnum = new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue,
            };
            var enumBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_ENUM_DATA_V0>());
            var output = Marshal.AllocHGlobal(UsnBufferSize);

            try
            {
                Marshal.StructureToPtr(usnEnum, enumBuffer, fDeleteOld: false);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!DeviceIoControl(
                            handle,
                            FSCTL_ENUM_USN_DATA,
                            enumBuffer,
                            (uint)Marshal.SizeOf<MFT_ENUM_DATA_V0>(),
                            output,
                            UsnBufferSize,
                            out var bytesReturned,
                            IntPtr.Zero))
                    {
                        var err = Marshal.GetLastPInvokeError();
                        // 38 = ERROR_HANDLE_EOF — we're done.
                        if (err == 38) break;
                        return false;
                    }

                    if (bytesReturned < sizeof(long)) break;

                    // First 8 bytes = next FRN to resume from.
                    var nextFrn = Marshal.ReadInt64(output);
                    var offset = sizeof(long);

                    while (offset < bytesReturned)
                    {
                        var record = Marshal.PtrToStructure<USN_RECORD_V2>(output + offset);
                        if (record.RecordLength == 0) break;

                        var nameBytes = new byte[record.FileNameLength];
                        Marshal.Copy(output + offset + record.FileNameOffset, nameBytes, 0, record.FileNameLength);
                        var name = System.Text.Encoding.Unicode.GetString(nameBytes);

                        nodes[(ulong)record.FileReferenceNumber] = new MftNode(
                            (ulong)record.FileReferenceNumber,
                            (ulong)record.ParentFileReferenceNumber,
                            name,
                            (record.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0);

                        offset += record.RecordLength;
                    }

                    // Resume the USN walk from the next unseen FRN.
                    Marshal.WriteInt64(enumBuffer, nextFrn);
                }

                // USN records don't carry size — a single pass of GET_NTFS_FILE_RECORD
                // fills it in for every file node.
                PopulateSizes(handle, nodes, ct);
            }
            finally
            {
                Marshal.FreeHGlobal(enumBuffer);
                Marshal.FreeHGlobal(output);
            }

            return nodes.Count > 0;
        }
        catch
        {
            nodes = new();
            return false;
        }
    }

    private static bool TryMftFileList(
        string drivePath,
        long minSize,
        out List<LargeFileInfo> files,
        CancellationToken ct)
    {
        files = new();
        if (!TryMftScan(drivePath, out var nodes, ct)) return false;

        // Reconstruct paths on-demand; cheap because the dictionary is already hot.
        foreach (var node in nodes.Values)
        {
            if (node.IsDirectory) continue;
            if (node.SizeBytes < minSize) continue;

            var fullPath = ResolvePath(nodes, node);
            if (string.IsNullOrEmpty(fullPath)) continue;

            files.Add(new LargeFileInfo
            {
                Path = fullPath,
                Name = node.Name,
                Extension = Path.GetExtension(node.Name).ToLowerInvariant(),
                SizeBytes = node.SizeBytes,
                LastModified = node.LastModified,
            });
        }
        return true;
    }

    private static void PopulateSizes(SafeFileHandle volume, Dictionary<ulong, MftNode> nodes, CancellationToken ct)
    {
        // FSCTL_GET_NTFS_FILE_RECORD wants an input/output buffer plus a FRN.
        // We parse the $DATA attribute's allocated length for file size.
        var inputBuf = Marshal.AllocHGlobal(sizeof(long));
        var recordBuf = Marshal.AllocHGlobal(FileRecordBufferSize);
        try
        {
            foreach (var node in nodes.Values)
            {
                if (node.IsDirectory) continue;
                ct.ThrowIfCancellationRequested();

                Marshal.WriteInt64(inputBuf, (long)node.Frn);
                if (!DeviceIoControl(
                        volume,
                        FSCTL_GET_NTFS_FILE_RECORD,
                        inputBuf,
                        sizeof(long),
                        recordBuf,
                        FileRecordBufferSize,
                        out var bytesReturned,
                        IntPtr.Zero))
                    continue;

                if (bytesReturned < NTFS_FILE_RECORD_OUTPUT_BUFFER_HEADER_SIZE) continue;

                // Skip the output-buffer header; the raw record follows.
                var recordStart = recordBuf + NTFS_FILE_RECORD_OUTPUT_BUFFER_HEADER_SIZE;
                if (ParseFileRecord(recordStart, (int)bytesReturned - NTFS_FILE_RECORD_OUTPUT_BUFFER_HEADER_SIZE,
                        out var size, out var modified))
                {
                    node.SizeBytes = size;
                    node.LastModified = modified;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputBuf);
            Marshal.FreeHGlobal(recordBuf);
        }
    }

    /// <summary>
    /// Extremely minimal MFT file-record parser: we only need the $STANDARD_INFORMATION
    /// timestamp and the $DATA attribute's data size. The full NTFS attribute spec is
    /// much deeper, but for aggregate folder sizing this subset is sufficient.
    /// Returns false for anything we can't confidently parse — the caller just
    /// leaves the size at zero in that case.
    /// </summary>
    private static bool ParseFileRecord(IntPtr record, int recordBytes, out long size, out DateTime modified)
    {
        size = 0;
        modified = default;

        if (recordBytes < 48) return false;

        // Magic "FILE" signature at offset 0.
        if (Marshal.ReadInt32(record) != 0x454C4946) return false;

        // Offset to first attribute is at offset 0x14.
        int attrOffset = Marshal.ReadInt16(record, 0x14);
        if (attrOffset <= 0 || attrOffset >= recordBytes) return false;

        int pos = attrOffset;
        while (pos < recordBytes - 8)
        {
            int attrType = Marshal.ReadInt32(record, pos);
            if (attrType == unchecked((int)0xFFFFFFFF)) break; // end marker
            int attrLen = Marshal.ReadInt32(record, pos + 4);
            if (attrLen <= 0 || pos + attrLen > recordBytes) break;

            byte nonResident = Marshal.ReadByte(record, pos + 8);

            switch (attrType)
            {
                case 0x10: // $STANDARD_INFORMATION
                {
                    // ModifiedTime is at content offset 0x08.
                    int contentOff = Marshal.ReadInt16(record, pos + 0x14);
                    if (pos + contentOff + 16 <= recordBytes)
                    {
                        long ft = Marshal.ReadInt64(record, pos + contentOff + 0x08);
                        if (ft > 0)
                        {
                            try { modified = DateTime.FromFileTimeUtc(ft).ToLocalTime(); }
                            catch { /* bogus stamp, ignore */ }
                        }
                    }
                    break;
                }
                case 0x80: // $DATA — unnamed default stream is the file body
                {
                    int nameLen = Marshal.ReadByte(record, pos + 9);
                    if (nameLen != 0) break; // alternate data stream, skip

                    if (nonResident == 0)
                    {
                        // Resident data: content length at offset 0x10.
                        int contentLen = Marshal.ReadInt32(record, pos + 0x10);
                        if (contentLen > size) size = contentLen;
                    }
                    else
                    {
                        // Non-resident data: real size at offset 0x30.
                        long real = Marshal.ReadInt64(record, pos + 0x30);
                        if (real > size) size = real;
                    }
                    break;
                }
            }

            pos += attrLen;
        }
        return true;
    }

    private static List<DiskFolderInfo> AggregateTopLevel(string drivePath, Dictionary<ulong, MftNode> nodes)
    {
        // Map FRN → rolling size so each directory adds its file sizes to its
        // immediate ancestors. One linear pass instead of recursion.
        // Keep the tuple as a named ValueTuple<long,int> to avoid inference
        // dropping the field names when we pass it through dictionary lookups.
        var rollup = new Dictionary<ulong, ValueTuple<long, int>>();
        foreach (var node in nodes.Values)
        {
            if (node.IsDirectory) continue;
            if (node.SizeBytes <= 0) continue;

            var cursor = node.ParentFrn;
            while (cursor != 0 && nodes.ContainsKey(cursor))
            {
                var prev = rollup.TryGetValue(cursor, out var e) ? e : (0L, 0);
                rollup[cursor] = (prev.Item1 + node.SizeBytes, prev.Item2 + 1);

                cursor = nodes[cursor].ParentFrn;
                if (cursor == nodes[cursor].Frn) break; // root self-reference
            }
        }

        // Find top-level folders (parent == root FRN 5 on NTFS) and surface them.
        var results = new List<DiskFolderInfo>();
        long totalSize = 0;

        foreach (var node in nodes.Values)
        {
            if (!node.IsDirectory) continue;
            if (node.ParentFrn != NtfsRootFrn) continue;

            var stats = rollup.TryGetValue(node.Frn, out var e) ? e : (0L, 0);
            if (stats.Item1 <= 0) continue;

            results.Add(new DiskFolderInfo
            {
                Path = Path.Combine(drivePath, node.Name),
                Name = node.Name,
                SizeBytes = stats.Item1,
                FileCount = stats.Item2,
            });
            totalSize += stats.Item1;
        }

        if (totalSize > 0)
        {
            foreach (var r in results)
                r.Percentage = r.SizeBytes * 100.0 / totalSize;
        }

        return results.OrderByDescending(r => r.SizeBytes).ToList();
    }

    /// <summary>
    /// Walk the FRN chain up to the volume root to reconstruct an absolute path.
    /// </summary>
    private static string ResolvePath(Dictionary<ulong, MftNode> nodes, MftNode leaf)
    {
        var parts = new Stack<string>();
        parts.Push(leaf.Name);

        var cursor = leaf.ParentFrn;
        int guard = 0;
        while (cursor != NtfsRootFrn && nodes.TryGetValue(cursor, out var parent) && guard++ < 256)
        {
            parts.Push(parent.Name);
            if (parent.ParentFrn == parent.Frn) break;
            cursor = parent.ParentFrn;
        }

        return string.Join('\\', parts);
    }

    // ───── FindFirstFileExW fallback (still fast) ───────────────

    public static List<DiskFolderInfo> AnalyzeFolderFast(string rootPath, CancellationToken ct)
    {
        if (!Directory.Exists(rootPath)) return new();

        var results = new ConcurrentBag<DiskFolderInfo>();
        string[] topDirs;
        try { topDirs = Directory.GetDirectories(rootPath); }
        catch { return new(); }

        // Parallel per-top-folder — the dominant cost on large volumes is
        // per-folder stat(), so spreading across threads gives a near-linear
        // speedup up to the disk's IOPS ceiling.
        Parallel.ForEach(topDirs, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
            CancellationToken = ct,
        }, dir =>
        {
            var (bytes, fileCount, folderCount) = CountWithLargeFetch(dir, ct);
            results.Add(new DiskFolderInfo
            {
                Path = dir,
                Name = Path.GetFileName(dir),
                SizeBytes = bytes,
                FileCount = fileCount,
                FolderCount = folderCount,
            });
        });

        long total = results.Sum(r => r.SizeBytes);
        if (total > 0)
            foreach (var r in results) r.Percentage = r.SizeBytes * 100.0 / total;

        return results.OrderByDescending(r => r.SizeBytes).ToList();
    }

    private static List<LargeFileInfo> FindLargeFilesFallback(
        string rootPath, long minSize, int maxResults, CancellationToken ct)
    {
        var hits = new ConcurrentBag<LargeFileInfo>();
        if (!Directory.Exists(rootPath)) return new();

        string[] topDirs;
        try { topDirs = Directory.GetDirectories(rootPath); }
        catch { return new(); }

        Parallel.ForEach(topDirs, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
            CancellationToken = ct,
        }, dir =>
        {
            ScanForLargeFilesWithLargeFetch(dir, minSize, hits, maxResults, ct);
        });

        return hits
            .OrderByDescending(f => f.SizeBytes)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Iterative directory walk using <see cref="FindFirstFileExW"/> with
    /// <c>FIND_FIRST_EX_LARGE_FETCH</c> and <c>FindExInfoBasic</c>. Roughly 2-3x
    /// faster than <see cref="Directory.EnumerateFiles"/> on large trees because
    /// NTFS streams more entries per syscall and we skip the 8.3 short-name
    /// lookup we don't use.
    /// </summary>
    private static (long bytes, int fileCount, int folderCount) CountWithLargeFetch(string root, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0, folders = 0;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();

            var searchPath = Path.Combine(current, "*");
            var findHandle = FindFirstFileExW(
                searchPath,
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                out var findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                FIND_FIRST_EX_LARGE_FETCH);

            if (findHandle == INVALID_HANDLE_VALUE) continue;

            try
            {
                do
                {
                    var name = findData.cFileName;
                    if (name == "." || name == "..") continue;

                    if ((findData.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                        continue; // skip junctions to avoid loops / mount-point double-counting

                    if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        folders++;
                        stack.Push(Path.Combine(current, name));
                    }
                    else
                    {
                        files++;
                        bytes += ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;
                    }
                }
                while (FindNextFileW(findHandle, out findData));
            }
            finally
            {
                FindClose(findHandle);
            }
        }

        return (bytes, files, folders);
    }

    private static void ScanForLargeFilesWithLargeFetch(
        string root,
        long minSize,
        ConcurrentBag<LargeFileInfo> hits,
        int maxResults,
        CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0 && hits.Count < maxResults * 2)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();

            var dirName = Path.GetFileName(current);
            if (dirName.StartsWith('.') || dirName.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                continue;

            var searchPath = Path.Combine(current, "*");
            var findHandle = FindFirstFileExW(
                searchPath,
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                out var findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                FIND_FIRST_EX_LARGE_FETCH);

            if (findHandle == INVALID_HANDLE_VALUE) continue;

            try
            {
                do
                {
                    var name = findData.cFileName;
                    if (name == "." || name == "..") continue;

                    if ((findData.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                        continue;

                    if ((findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        stack.Push(Path.Combine(current, name));
                        continue;
                    }

                    long size = ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;
                    if (size < minSize) continue;

                    hits.Add(new LargeFileInfo
                    {
                        Path = Path.Combine(current, name),
                        Name = name,
                        Extension = Path.GetExtension(name).ToLowerInvariant(),
                        SizeBytes = size,
                        LastModified = DateTime.FromFileTime(
                            ((long)findData.ftLastWriteTime.dwHighDateTime << 32) |
                            (uint)findData.ftLastWriteTime.dwLowDateTime),
                    });
                }
                while (FindNextFileW(findHandle, out findData));
            }
            finally
            {
                FindClose(findHandle);
            }
        }
    }

    // ───── volume helpers ───────────────────────────────────────

    private static bool IsNtfs(string volumeRoot)
    {
        try
        {
            var drive = new DriveInfo(volumeRoot);
            return drive.IsReady && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static SafeFileHandle OpenVolume(string volumeRoot)
    {
        // "\\.\C:" — the volume handle lets us issue FSCTL ioctls against the raw filesystem.
        var volumePath = @"\\.\" + volumeRoot.TrimEnd('\\', '/');
        return CreateFileW(
            volumePath,
            FILE_READ_DATA | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
    }

    // ───── internal types ───────────────────────────────────────

    private sealed class MftNode
    {
        public ulong Frn { get; }
        public ulong ParentFrn { get; }
        public string Name { get; }
        public bool IsDirectory { get; }
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }

        public MftNode(ulong frn, ulong parent, string name, bool dir)
        {
            Frn = frn; ParentFrn = parent; Name = name; IsDirectory = dir;
        }
    }

    // ───── P/Invoke surface ─────────────────────────────────────

    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    private const uint FSCTL_GET_NTFS_FILE_RECORD = 0x00090068;
    private const int NTFS_FILE_RECORD_OUTPUT_BUFFER_HEADER_SIZE = 16; // LARGE_INTEGER FRN + ULONG FileRecordLength
    private const int UsnBufferSize = 1024 * 1024;
    private const int FileRecordBufferSize = 4096;
    private const ulong NtfsRootFrn = 0x5;

    private const uint FILE_READ_DATA = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    private const int FIND_FIRST_EX_LARGE_FETCH = 0x2;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0
    {
        public long StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct USN_RECORD_V2
    {
        public int RecordLength;
        public short MajorVersion;
        public short MinorVersion;
        public long FileReferenceNumber;
        public long ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public short FileNameLength;
        public short FileNameOffset;
        // FileName follows variably; we read it via Marshal.Copy in the caller.
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    private enum FINDEX_INFO_LEVELS { FindExInfoStandard = 0, FindExInfoBasic = 1 }
    private enum FINDEX_SEARCH_OPS { FindExSearchNameMatch = 0 }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(
        string lpFileName,
        FINDEX_INFO_LEVELS fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData,
        FINDEX_SEARCH_OPS fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);
}
