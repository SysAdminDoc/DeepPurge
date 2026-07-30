using System.ComponentModel;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DeepPurge.Core.Safety;

internal enum FileOperationScopeKind
{
    Exact,
    Tree,
    Sibling,
}

internal readonly record struct FileOperationScope(string RootPath, FileOperationScopeKind Kind)
{
    public static FileOperationScope Exact(string path)
        => new(SafetyGuard.NormalizePath(path), FileOperationScopeKind.Exact);

    public static FileOperationScope Tree(string path)
        => new(SafetyGuard.NormalizePath(path), FileOperationScopeKind.Tree);

    public static FileOperationScope Sibling(string path)
        => new(SafetyGuard.NormalizePath(path), FileOperationScopeKind.Sibling);

    public bool Allows(string path)
    {
        var normalized = SafetyGuard.NormalizePath(path);
        return Kind switch
        {
            FileOperationScopeKind.Exact =>
                normalized.Equals(RootPath, StringComparison.OrdinalIgnoreCase),
            FileOperationScopeKind.Tree =>
                SafetyGuard.IsSamePathOrDescendant(normalized, RootPath),
            FileOperationScopeKind.Sibling =>
                string.Equals(
                    Path.GetDirectoryName(normalized),
                    Path.GetDirectoryName(RootPath),
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

internal readonly record struct FileObjectIdentity(uint VolumeSerialNumber, ulong FileIndex);

/// <summary>
/// An opened filesystem object whose reparse status, final path, type, and
/// object identity have been validated. The handle deliberately withholds
/// delete sharing so the object cannot be renamed or replaced between
/// validation and disposition.
/// </summary>
internal sealed class ValidatedFileTarget : IDisposable
{
    private bool _disposed;
    private bool _markedForDeletion;

    internal ValidatedFileTarget(
        SafeFileHandle handle,
        string finalPath,
        FileObjectIdentity identity,
        FileAttributes attributes,
        long sizeBytes,
        FileOperationScope scope,
        bool requireGlobalSafety)
    {
        Handle = handle;
        CurrentPath = finalPath;
        Identity = identity;
        Attributes = attributes;
        SizeBytes = sizeBytes;
        Scope = scope;
        RequireGlobalSafety = requireGlobalSafety;
    }

    internal SafeFileHandle Handle { get; }
    internal string CurrentPath { get; private set; }
    internal FileObjectIdentity Identity { get; }
    internal FileAttributes Attributes { get; private set; }
    internal long SizeBytes { get; }
    internal FileOperationScope Scope { get; }
    internal bool RequireGlobalSafety { get; }
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    internal bool Revalidate(out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!HandleBoundFileOperations.TryOpenValidated(
                CurrentPath,
                IsDirectory,
                Scope,
                HandleBoundFileOperations.ReadAttributes,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                out var current,
                out reason,
                out _,
                RequireGlobalSafety))
            return false;

        using (current)
        {
            if (current!.Identity != Identity ||
                !current.CurrentPath.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "The path no longer resolves to the validated filesystem object.";
                return false;
            }
        }

        reason = "";
        return true;
    }

    internal bool TryOverwriteWithRandomData(out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsDirectory)
        {
            reason = "Directories cannot be overwritten.";
            return false;
        }

        if (!Revalidate(out reason) ||
            !HandleBoundFileOperations.TryClearReadOnly(this, out reason))
            return false;

        try
        {
            const int bufferSize = 64 * 1024;
            var buffer = new byte[bufferSize];
            long offset = 0;
            while (offset < SizeBytes)
            {
                RandomNumberGenerator.Fill(buffer);
                var count = (int)Math.Min(buffer.Length, SizeBytes - offset);
                RandomAccess.Write(Handle, buffer.AsSpan(0, count), offset);
                offset += count;
            }

            RandomAccess.FlushToDisk(Handle);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Random overwrite failed: {ex.Message}";
            return false;
        }
    }

    internal bool TryRenameToOpaqueSibling(out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Revalidate(out reason)) return false;

        var parent = Path.GetDirectoryName(CurrentPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            reason = "The target has no valid parent directory.";
            return false;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.Combine(
                parent,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + ".tmp");
            if (!Scope.Allows(candidate)) continue;

            if (!HandleBoundFileOperations.TryRename(
                    Handle,
                    candidate,
                    out reason,
                    out var win32Error))
            {
                if (win32Error is 80 or 183) continue;
                return false;
            }

            if (!HandleBoundFileOperations.TryReadMetadata(
                    Handle,
                    out var identity,
                    out var attributes,
                    out _,
                    out reason))
                return false;
            if (identity != Identity)
            {
                reason = "The filesystem object identity changed after rename.";
                return false;
            }

            if (!HandleBoundFileOperations.TryGetFinalPath(
                    Handle,
                    out var finalPath,
                    out reason))
                return false;
            if (!Scope.Allows(finalPath))
            {
                reason = $"The renamed target escaped its sibling scope: {finalPath}";
                return false;
            }

            CurrentPath = finalPath;
            Attributes = attributes;
            reason = "";
            return true;
        }

        reason = "Could not allocate a collision-free opaque filename.";
        return false;
    }

    internal bool TryDelete(out string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_markedForDeletion)
        {
            reason = "";
            return true;
        }

        if (!Revalidate(out reason)) return false;

        var disposition = new HandleBoundFileOperations.FileDispositionInfo
        {
            DeleteFile = 1,
        };
        if (!HandleBoundFileOperations.SetFileInformationByHandle(
                Handle,
                HandleBoundFileOperations.FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<HandleBoundFileOperations.FileDispositionInfo>()))
        {
            reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        _markedForDeletion = true;
        reason = "";
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Handle.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Windows filesystem deletion primitives that operate on validated object
/// handles rather than re-resolving untrusted path strings at delete time.
/// </summary>
internal static class HandleBoundFileOperations
{
    internal const uint DeleteAccess = 0x00010000;
    internal const uint ReadAttributes = 0x00000080;
    private const uint WriteAttributes = 0x00000100;
    private const uint GenericWrite = 0x40000000;

    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReadOnly = 0x00000001;
    private const uint FileAttributeNormal = 0x00000080;
    private const int FileBasicInfoClass = 0;
    internal const int FileDispositionInfoClass = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    internal static bool DeleteFile(string path, out string reason)
    {
        if (!SafetyGuard.IsPathSafeToDelete(path))
        {
            reason = "The path is protected or invalid.";
            return false;
        }

        FileOperationScope scope;
        try { scope = FileOperationScope.Exact(path); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryOpenValidated(
                path,
                expectedDirectory: false,
                scope,
                DeleteAccess | ReadAttributes | WriteAttributes,
                FileShare.Read | FileShare.Write,
                out var target,
                out reason,
                out var error))
            return error is ErrorFileNotFound or ErrorPathNotFound;

        var recordedPath = target!.CurrentPath;
        var size = target.SizeBytes;
        bool deleted;
        using (target)
            deleted = target.TryDelete(out reason);

        if (deleted)
            Diagnostics.DeletionManifest.Record(recordedPath, "file", size, "delete");
        return deleted;
    }

    internal static bool DeleteDirectoryTree(string path, out string reason)
        => DeleteDirectoryTree(path, secure: false, out reason);

    /// <summary>
    /// Deletes an app-owned or explicitly approved file without applying the
    /// global user-data blocklist. The handle still has to resolve within the
    /// caller-provided root and pass every no-follow identity check.
    /// </summary>
    internal static bool DeleteFileWithinScope(
        string path,
        string allowedRoot,
        out string reason)
    {
        FileOperationScope scope;
        try { scope = FileOperationScope.Tree(allowedRoot); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryOpenValidated(
                path,
                expectedDirectory: false,
                scope,
                DeleteAccess | ReadAttributes | WriteAttributes,
                FileShare.Read | FileShare.Write,
                out var target,
                out reason,
                out var error,
                requireGlobalSafety: false))
            return error is ErrorFileNotFound or ErrorPathNotFound;

        using (target)
            return target!.TryDelete(out reason);
    }

    /// <summary>
    /// Recursively deletes an app-owned or explicitly approved tree while
    /// keeping every opened object inside the caller-provided root.
    /// </summary>
    internal static bool DeleteDirectoryTreeWithinScope(
        string path,
        string allowedRoot,
        out string reason)
    {
        FileOperationScope scope;
        try { scope = FileOperationScope.Tree(allowedRoot); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryOpenValidated(
                path,
                expectedDirectory: true,
                scope,
                DeleteAccess | ReadAttributes | WriteAttributes,
                FileShare.Read | FileShare.Write,
                out var root,
                out reason,
                out _,
                requireGlobalSafety: false))
            return false;

        using (root)
            return DeleteTree(
                root!,
                scope,
                secure: false,
                recordManifest: false,
                requireGlobalSafety: false,
                out reason);
    }

    internal static bool SecureDeleteFile(string path, out string reason)
    {
        if (!SafetyGuard.IsPathSafeToDelete(path))
        {
            reason = "The path is protected or invalid.";
            return false;
        }

        FileOperationScope scope;
        try { scope = FileOperationScope.Sibling(path); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryOpenValidated(
                path,
                expectedDirectory: false,
                scope,
                GenericWrite | DeleteAccess | ReadAttributes | WriteAttributes,
                FileShare.Read,
                out var target,
                out reason,
                out _))
            return false;

        var recordedPath = target!.CurrentPath;
        var size = target.SizeBytes;
        bool deleted;
        using (target)
        {
            if (!target.TryOverwriteWithRandomData(out reason))
                return false;

            if (!target.TryRenameToOpaqueSibling(out var renameReason))
                Diagnostics.Log.Warn(
                    $"Secure delete could not obscure the filename '{recordedPath}': {renameReason}");

            deleted = target.TryDelete(out reason);
        }

        if (deleted)
            Diagnostics.DeletionManifest.Record(recordedPath, "file", size, "secure-delete");
        return deleted;
    }

    internal static bool SecureDeleteDirectoryTree(string path, out string reason)
        => DeleteDirectoryTree(path, secure: true, out reason);

    private static bool DeleteDirectoryTree(string path, bool secure, out string reason)
    {
        if (!SafetyGuard.IsPathSafeToDelete(path))
        {
            reason = "The path is protected or invalid.";
            return false;
        }

        FileOperationScope scope;
        try { scope = FileOperationScope.Tree(path); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryOpenValidated(
                path,
                expectedDirectory: true,
                scope,
                DeleteAccess | ReadAttributes | WriteAttributes,
                FileShare.Read | FileShare.Write,
                out var root,
                out reason,
                out _))
            return false;

        var recordedPath = root!.CurrentPath;
        bool deleted;
        using (root)
            deleted = DeleteTree(
                root,
                scope,
                secure,
                recordManifest: true,
                requireGlobalSafety: true,
                out reason);

        if (deleted)
        {
            Diagnostics.DeletionManifest.Record(
                recordedPath,
                "directory",
                0,
                secure ? "secure-delete-recursive" : "delete-recursive");
        }
        return deleted;
    }

    private static bool DeleteTree(
        ValidatedFileTarget directory,
        FileOperationScope scope,
        bool secure,
        bool recordManifest,
        bool requireGlobalSafety,
        out string reason)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(
                directory.CurrentPath,
                "*",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            reason = $"Could not enumerate '{directory.CurrentPath}': {ex.Message}";
            return false;
        }

        foreach (var entry in entries)
        {
            var access = DeleteAccess | ReadAttributes | WriteAttributes;
            if (secure) access |= GenericWrite;

            if (!TryOpenValidated(
                    entry,
                    expectedDirectory: null,
                    scope,
                    access,
                    secure ? FileShare.Read : FileShare.Read | FileShare.Write,
                    out var child,
                    out reason,
                    out _,
                    requireGlobalSafety))
                return false;

            var recordedPath = child!.CurrentPath;
            var size = child.SizeBytes;
            bool deleted;
            using (child)
            {
                if (child.IsDirectory)
                {
                    deleted = DeleteTree(
                        child,
                        scope,
                        secure,
                        recordManifest,
                        requireGlobalSafety,
                        out reason);
                }
                else
                {
                    if (secure && !child.TryOverwriteWithRandomData(out reason))
                        return false;

                    if (secure && !child.TryRenameToOpaqueSibling(out var renameReason))
                    {
                        Diagnostics.Log.Warn(
                            $"Secure delete could not obscure the filename '{recordedPath}': {renameReason}");
                    }

                    deleted = child.TryDelete(out reason);
                }
            }

            if (!deleted) return false;
            if (recordManifest && !child.IsDirectory)
            {
                Diagnostics.DeletionManifest.Record(
                    recordedPath,
                    "file",
                    size,
                    secure ? "secure-delete" : "delete");
            }
        }

        return directory.TryDelete(out reason);
    }

    internal static bool TryOpenForDeletion(
        string path,
        bool expectedDirectory,
        FileOperationScope scope,
        out ValidatedFileTarget? target,
        out string reason)
        => TryOpenValidated(
            path,
            expectedDirectory,
            scope,
            DeleteAccess | ReadAttributes | WriteAttributes,
            FileShare.Read | FileShare.Write,
            out target,
            out reason,
            out _);

    internal static bool TryCaptureStablePathIdentity(
        string path,
        bool expectedDirectory,
        out string finalPath,
        out FileObjectIdentity identity,
        out long sizeBytes,
        out string reason)
    {
        finalPath = "";
        identity = default;
        sizeBytes = 0;

        if (!SafetyGuard.IsPathSafeToDelete(path))
        {
            reason = "The path is protected or invalid.";
            return false;
        }

        FileOperationScope scope;
        try { scope = FileOperationScope.Exact(path); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryOpenValidated(
                path,
                expectedDirectory,
                scope,
                ReadAttributes,
                FileShare.Read | FileShare.Write,
                out var first,
                out reason,
                out _))
            return false;

        using (first)
        {
            if (!first!.Revalidate(out reason)) return false;
            finalPath = first.CurrentPath;
            identity = first.Identity;
            sizeBytes = first.SizeBytes;
        }

        if (!TryOpenValidated(
                path,
                expectedDirectory,
                scope,
                ReadAttributes,
                FileShare.Read | FileShare.Write,
                out var second,
                out reason,
                out _))
            return false;

        using (second)
        {
            if (!second!.Revalidate(out reason) ||
                second.Identity != identity ||
                !second.CurrentPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "The target drifted while preparing the path-based operation.";
                return false;
            }
        }

        reason = "";
        return true;
    }

    internal static bool TryOpenValidated(
        string path,
        bool? expectedDirectory,
        FileOperationScope scope,
        uint desiredAccess,
        FileShare share,
        out ValidatedFileTarget? target,
        out string reason,
        out int win32Error,
        bool requireGlobalSafety = true)
    {
        target = null;
        reason = "";
        win32Error = 0;

        string normalized;
        try { normalized = SafetyGuard.NormalizePath(path); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!scope.Allows(normalized) ||
            (requireGlobalSafety && !SafetyGuard.IsPathSafeToDelete(normalized)))
        {
            reason = "The requested path is outside the approved operation scope.";
            return false;
        }

        var handle = CreateFileW(
            normalized,
            desiredAccess,
            share,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            reason = new Win32Exception(win32Error).Message;
            handle.Dispose();
            return false;
        }

        if (!TryReadMetadata(
                handle,
                out var identity,
                out var attributes,
                out var sizeBytes,
                out reason))
        {
            handle.Dispose();
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            reason = "Reparse points are not valid destructive-operation targets.";
            handle.Dispose();
            return false;
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (expectedDirectory.HasValue && expectedDirectory.Value != isDirectory)
        {
            reason = expectedDirectory.Value
                ? "The target is not a directory."
                : "The target is not a file.";
            handle.Dispose();
            return false;
        }

        if (!TryGetFinalPath(handle, out var finalPath, out reason) ||
            !scope.Allows(finalPath) ||
            (requireGlobalSafety && !SafetyGuard.IsPathSafeToDelete(finalPath)))
        {
            if (string.IsNullOrEmpty(reason))
                reason = $"The final path escaped its approved scope: {finalPath}";
            handle.Dispose();
            return false;
        }

        target = new ValidatedFileTarget(
            handle,
            finalPath,
            identity,
            attributes,
            sizeBytes,
            scope,
            requireGlobalSafety);
        return true;
    }

    internal static bool TryReadMetadata(
        SafeFileHandle handle,
        out FileObjectIdentity identity,
        out FileAttributes attributes,
        out long sizeBytes,
        out string reason)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            identity = default;
            attributes = default;
            sizeBytes = 0;
            reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        identity = new FileObjectIdentity(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        attributes = (FileAttributes)info.FileAttributes;
        sizeBytes = (long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow);
        reason = "";
        return true;
    }

    internal static bool TryGetFinalPath(
        SafeFileHandle handle,
        out string finalPath,
        out string reason)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }

        if (length == 0 || length >= buffer.Capacity)
        {
            finalPath = "";
            reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            var raw = buffer.ToString();
            if (raw.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                raw = @"\\" + raw[8..];
            else if (raw.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                raw = raw[4..];

            finalPath = SafetyGuard.NormalizePath(raw);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            finalPath = "";
            reason = ex.Message;
            return false;
        }
    }

    internal static bool TryClearReadOnly(ValidatedFileTarget target, out string reason)
    {
        if ((target.Attributes & FileAttributes.ReadOnly) == 0)
        {
            reason = "";
            return true;
        }

        if (!GetFileInformationByHandleEx(
                target.Handle,
                FileBasicInfoClass,
                out var basic,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        basic.FileAttributes &= ~FileAttributeReadOnly;
        if (basic.FileAttributes == 0) basic.FileAttributes = FileAttributeNormal;
        if (!SetFileInformationByHandle(
                target.Handle,
                FileBasicInfoClass,
                ref basic,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        reason = "";
        return true;
    }

    internal static bool TryRename(
        SafeFileHandle handle,
        string destination,
        out string reason,
        out int win32Error)
    {
        win32Error = 0;
        var nameBytes = Encoding.Unicode.GetBytes(destination);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bufferSize = checked(nameOffset + nameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var offset = 0; offset < nameOffset; offset += sizeof(int))
                Marshal.WriteInt32(buffer, offset, 0);
            Marshal.WriteInt32(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);

            if (SetFileInformationByHandle(
                    handle,
                    FileRenameInfoClass,
                    buffer,
                    (uint)bufferSize))
            {
                reason = "";
                return true;
            }

            win32Error = Marshal.GetLastWin32Error();
            reason = new Win32Exception(win32Error).Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileDispositionInfo
    {
        internal int DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private const int FileRenameInfoClass = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
