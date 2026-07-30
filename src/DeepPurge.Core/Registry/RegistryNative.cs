using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DeepPurge.Core.Registry;

internal enum RegistryOpenStatus
{
    Opened,
    Missing,
    SymbolicLink,
}

internal sealed record RegistryValueSnapshot(
    string Name,
    uint Type,
    byte[] Data);

internal sealed record RegistryKeySnapshot(
    string RelativePath,
    string SecuritySddl,
    long LastWriteFileTimeUtc,
    IReadOnlyList<RegistryValueSnapshot> Values);

internal sealed record RegistryObjectSnapshot(
    string Hive,
    string SubKey,
    string? ValueName,
    string RegistryView,
    IReadOnlyList<RegistryKeySnapshot> Keys,
    string ObjectIdentity);

internal sealed class RegistryHandleChain : IDisposable
{
    private readonly List<SafeRegistryHandle> _handles;

    internal RegistryHandleChain(List<SafeRegistryHandle> handles)
    {
        _handles = handles;
    }

    internal SafeRegistryHandle Target => _handles[^1];

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--)
            _handles[index].Dispose();
    }
}

/// <summary>
/// Native, handle-bound registry access. Every component is opened with
/// REG_OPTION_OPEN_LINK so a registry link is inspected rather than followed.
/// </summary>
internal static class RegistryNative
{
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorMoreData = 234;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;

    private const uint RegOptionOpenLink = 0x00000008;
    private const uint RegLink = 6;

    private const uint Delete = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint KeyQueryValue = 0x0001;
    private const uint KeySetValue = 0x0002;
    private const uint KeyEnumerateSubKeys = 0x0008;
    private const uint KeyRead = ReadControl | KeyQueryValue | KeyEnumerateSubKeys;
    private const uint KeyDeleteTree =
        Delete | ReadControl | KeyQueryValue | KeySetValue | KeyEnumerateSubKeys;

    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;

    internal static RegistryOpenStatus TryOpenForKeyDeletion(
        RegistryPathParts target,
        out RegistryHandleChain? chain)
        => TryOpen(target, KeyDeleteTree, out chain);

    internal static RegistryOpenStatus TryOpenForValueDeletion(
        RegistryPathParts target,
        out RegistryHandleChain? chain)
        => TryOpen(target, KeyRead | KeySetValue, out chain);

    internal static RegistryObjectSnapshot CaptureTree(
        RegistryPathParts target,
        SafeRegistryHandle handle)
    {
        var keys = new List<RegistryKeySnapshot>();
        CaptureTreeCore(handle, string.Empty, keys);
        var ordered = keys
            .OrderBy(key => key.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return CreateSnapshot(target, valueName: null, ordered);
    }

    internal static RegistryObjectSnapshot? CaptureValue(
        RegistryPathParts target,
        string valueName,
        SafeRegistryHandle handle)
    {
        var value = ReadValue(handle, valueName);
        if (value == null) return null;

        var key = QueryKeyInfo(handle);
        var snapshotKey = new RegistryKeySnapshot(
            string.Empty,
            key.SecuritySddl,
            LastWriteFileTimeUtc: 0,
            new[] { value });
        return CreateSnapshot(target, valueName, new[] { snapshotKey });
    }

    internal static void DeleteTree(SafeRegistryHandle handle)
    {
        while (true)
        {
            var childName = EnumerateFirstSubKey(handle);
            if (childName == null) break;

            var error = RegOpenKeyExW(
                handle.DangerousGetHandle(),
                childName,
                RegOptionOpenLink,
                KeyDeleteTree,
                out var child);
            ThrowIfError(error, $"open registry child '{childName}' for deletion");
            using (child)
            {
                if (IsSymbolicLink(child))
                    throw new InvalidOperationException(
                        $"Registry link appeared during deletion: {childName}");
                DeleteTree(child);
            }
        }

        while (true)
        {
            var valueName = EnumerateFirstValueName(handle);
            if (valueName == null) break;
            ThrowIfError(
                RegDeleteValueW(handle.DangerousGetHandle(), valueName),
                $"delete registry value '{valueName}'");
        }

        var status = NtDeleteKey(handle.DangerousGetHandle());
        if (status != 0)
        {
            var win32Error = unchecked((int)RtlNtStatusToDosError(status));
            throw new Win32Exception(
                win32Error,
                $"Could not delete the exact open registry key (NTSTATUS 0x{status:X8}).");
        }
    }

    internal static bool DeleteValue(SafeRegistryHandle handle, string valueName)
    {
        var result = RegDeleteValueW(handle.DangerousGetHandle(), valueName);
        if (result == ErrorFileNotFound) return false;
        ThrowIfError(result, $"delete registry value '{valueName}'");
        return true;
    }

    internal static bool IsSymbolicLink(SafeRegistryHandle handle)
    {
        uint type = 0;
        uint size = 0;
        var result = RegQueryValueExW(
            handle.DangerousGetHandle(),
            "SymbolicLinkValue",
            IntPtr.Zero,
            out type,
            null,
            ref size);
        return result == ErrorSuccess && type == RegLink;
    }

    private static RegistryOpenStatus TryOpen(
        RegistryPathParts target,
        uint finalAccess,
        out RegistryHandleChain? chain)
    {
        chain = null;
        var opened = new List<SafeRegistryHandle>();
        var current = GetPredefinedHiveHandle(target.Hive);
        var components = target.SubKey.Split('\\');

        try
        {
            for (var index = 0; index < components.Length; index++)
            {
                var access = index == components.Length - 1 ? finalAccess : KeyRead;
                var error = RegOpenKeyExW(
                    current,
                    components[index],
                    RegOptionOpenLink,
                    access,
                    out var next);
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return RegistryOpenStatus.Missing;
                ThrowIfError(error, $"open registry path component '{components[index]}'");

                opened.Add(next);
                current = next.DangerousGetHandle();
                if (IsSymbolicLink(next))
                    return RegistryOpenStatus.SymbolicLink;
            }

            chain = new RegistryHandleChain(opened);
            opened = new List<SafeRegistryHandle>();
            return RegistryOpenStatus.Opened;
        }
        finally
        {
            foreach (var handle in opened)
                handle.Dispose();
        }
    }

    private static void CaptureTreeCore(
        SafeRegistryHandle handle,
        string relativePath,
        List<RegistryKeySnapshot> keys)
    {
        if (IsSymbolicLink(handle))
            throw new InvalidOperationException(
                $"Registry link is not eligible for backup: {relativePath}");

        var info = QueryKeyInfo(handle);
        var values = EnumerateValues(handle, info.ValueCount, info.MaxValueNameLength, info.MaxValueDataLength)
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        keys.Add(new RegistryKeySnapshot(
            relativePath,
            info.SecuritySddl,
            info.LastWriteFileTimeUtc,
            values));

        var childNames = EnumerateSubKeys(handle, info.SubKeyCount, info.MaxSubKeyNameLength)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var childName in childNames)
        {
            var error = RegOpenKeyExW(
                handle.DangerousGetHandle(),
                childName,
                RegOptionOpenLink,
                KeyRead,
                out var child);
            ThrowIfError(error, $"open registry child '{childName}' for backup");
            using (child)
            {
                if (IsSymbolicLink(child))
                    throw new InvalidOperationException(
                        $"Registry link is not eligible for backup: {childName}");
                var childRelative = string.IsNullOrEmpty(relativePath)
                    ? childName
                    : $"{relativePath}\\{childName}";
                CaptureTreeCore(child, childRelative, keys);
            }
        }
    }

    private static RegistryObjectSnapshot CreateSnapshot(
        RegistryPathParts target,
        string? valueName,
        IReadOnlyList<RegistryKeySnapshot> keys)
    {
        var identityPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            target.HiveName,
            target.SubKey,
            ValueName = valueName,
            RegistryView = "Default",
            Keys = keys,
        });
        var identity = $"sha256:{Convert.ToHexString(SHA256.HashData(identityPayload)).ToLowerInvariant()}";
        return new RegistryObjectSnapshot(
            target.HiveName,
            target.SubKey,
            valueName,
            "Default",
            keys,
            identity);
    }

    private static RegistryKeyInfo QueryKeyInfo(SafeRegistryHandle handle)
    {
        uint classLength = 0;
        var result = RegQueryInfoKeyW(
            handle.DangerousGetHandle(),
            null,
            ref classLength,
            IntPtr.Zero,
            out var subKeyCount,
            out var maxSubKeyNameLength,
            IntPtr.Zero,
            out var valueCount,
            out var maxValueNameLength,
            out var maxValueDataLength,
            IntPtr.Zero,
            out var lastWrite);
        ThrowIfError(result, "query registry key metadata");

        return new RegistryKeyInfo(
            subKeyCount,
            maxSubKeyNameLength,
            valueCount,
            maxValueNameLength,
            maxValueDataLength,
            lastWrite.ToLong(),
            ReadSecuritySddl(handle));
    }

    private static IEnumerable<string> EnumerateSubKeys(
        SafeRegistryHandle handle,
        uint expectedCount,
        uint maxNameLength)
    {
        var names = new List<string>();
        for (uint index = 0; index < expectedCount; index++)
        {
            var capacity = checked((int)Math.Max(maxNameLength + 2, 2));
            var name = new StringBuilder(capacity);
            uint length = (uint)capacity;
            var result = RegEnumKeyExW(
                handle.DangerousGetHandle(),
                index,
                name,
                ref length,
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                IntPtr.Zero);
            if (result == ErrorNoMoreItems) break;
            ThrowIfError(result, "enumerate registry subkeys");
            names.Add(name.ToString());
        }
        return names;
    }

    private static IEnumerable<RegistryValueSnapshot> EnumerateValues(
        SafeRegistryHandle handle,
        uint expectedCount,
        uint maxNameLength,
        uint maxDataLength)
    {
        var values = new List<RegistryValueSnapshot>();
        for (uint index = 0; index < expectedCount; index++)
        {
            var nameCapacity = checked((int)Math.Max(maxNameLength + 2, 2));
            var dataCapacity = checked((int)Math.Max(maxDataLength, 1));

            while (true)
            {
                var name = new StringBuilder(nameCapacity);
                uint nameLength = (uint)nameCapacity;
                var data = new byte[dataCapacity];
                uint dataLength = (uint)data.Length;
                var result = RegEnumValueW(
                    handle.DangerousGetHandle(),
                    index,
                    name,
                    ref nameLength,
                    IntPtr.Zero,
                    out var type,
                    data,
                    ref dataLength);
                if (result == ErrorMoreData)
                {
                    nameCapacity = checked((int)Math.Max(nameLength + 2, (uint)nameCapacity * 2));
                    dataCapacity = checked((int)Math.Max(dataLength, (uint)dataCapacity * 2));
                    continue;
                }
                if (result == ErrorNoMoreItems) return values;
                ThrowIfError(result, "enumerate registry values");
                Array.Resize(ref data, checked((int)dataLength));
                values.Add(new RegistryValueSnapshot(name.ToString(), type, data));
                break;
            }
        }
        return values;
    }

    private static RegistryValueSnapshot? ReadValue(
        SafeRegistryHandle handle,
        string valueName)
    {
        uint type;
        uint size = 0;
        var result = RegQueryValueExW(
            handle.DangerousGetHandle(),
            valueName,
            IntPtr.Zero,
            out type,
            null,
            ref size);
        if (result == ErrorFileNotFound) return null;
        if (result != ErrorSuccess && result != ErrorMoreData)
            ThrowIfError(result, $"query registry value '{valueName}'");

        var data = new byte[Math.Max(size, 1)];
        var dataLength = (uint)data.Length;
        result = RegQueryValueExW(
            handle.DangerousGetHandle(),
            valueName,
            IntPtr.Zero,
            out type,
            data,
            ref dataLength);
        ThrowIfError(result, $"read registry value '{valueName}'");
        Array.Resize(ref data, checked((int)dataLength));
        return new RegistryValueSnapshot(valueName, type, data);
    }

    private static string? EnumerateFirstSubKey(SafeRegistryHandle handle)
    {
        var capacity = 256;
        while (true)
        {
            var name = new StringBuilder(capacity);
            uint length = (uint)capacity;
            var result = RegEnumKeyExW(
                handle.DangerousGetHandle(),
                0,
                name,
                ref length,
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                IntPtr.Zero);
            if (result == ErrorNoMoreItems) return null;
            if (result == ErrorMoreData)
            {
                capacity *= 2;
                continue;
            }
            ThrowIfError(result, "enumerate registry child for deletion");
            return name.ToString();
        }
    }

    private static string? EnumerateFirstValueName(SafeRegistryHandle handle)
    {
        var capacity = 256;
        while (true)
        {
            var name = new StringBuilder(capacity);
            uint length = (uint)capacity;
            uint dataLength = 0;
            var result = RegEnumValueW(
                handle.DangerousGetHandle(),
                0,
                name,
                ref length,
                IntPtr.Zero,
                out _,
                null,
                ref dataLength);
            if (result == ErrorNoMoreItems) return null;
            if (result == ErrorMoreData && length >= capacity)
            {
                capacity = checked((int)length + 2);
                continue;
            }
            if (result != ErrorSuccess && result != ErrorMoreData)
                ThrowIfError(result, "enumerate registry value for deletion");
            return name.ToString();
        }
    }

    private static string ReadSecuritySddl(SafeRegistryHandle handle)
    {
        uint length = 0;
        var information = OwnerSecurityInformation | DaclSecurityInformation;
        var result = RegGetKeySecurity(
            handle.DangerousGetHandle(),
            information,
            null,
            ref length);
        if (result != ErrorInsufficientBuffer)
            ThrowIfError(result, "query registry security descriptor size");

        var buffer = new byte[length];
        result = RegGetKeySecurity(
            handle.DangerousGetHandle(),
            information,
            buffer,
            ref length);
        ThrowIfError(result, "read registry security descriptor");
        return new RawSecurityDescriptor(buffer, 0).GetSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
    }

    private static IntPtr GetPredefinedHiveHandle(global::Microsoft.Win32.RegistryHive hive)
        => hive switch
        {
            global::Microsoft.Win32.RegistryHive.ClassesRoot => HKeyClassesRoot,
            global::Microsoft.Win32.RegistryHive.CurrentUser => HKeyCurrentUser,
            global::Microsoft.Win32.RegistryHive.LocalMachine => HKeyLocalMachine,
            global::Microsoft.Win32.RegistryHive.Users => HKeyUsers,
            _ => throw new NotSupportedException($"Registry hive {hive} is not supported."),
        };

    private static void ThrowIfError(int error, string operation)
    {
        if (error != ErrorSuccess)
            throw new Win32Exception(error, $"Could not {operation}.");
    }

    private static readonly IntPtr HKeyClassesRoot = new(unchecked((int)0x80000000));
    private static readonly IntPtr HKeyCurrentUser = new(unchecked((int)0x80000001));
    private static readonly IntPtr HKeyLocalMachine = new(unchecked((int)0x80000002));
    private static readonly IntPtr HKeyUsers = new(unchecked((int)0x80000003));

    private sealed record RegistryKeyInfo(
        uint SubKeyCount,
        uint MaxSubKeyNameLength,
        uint ValueCount,
        uint MaxValueNameLength,
        uint MaxValueDataLength,
        long LastWriteFileTimeUtc,
        string SecuritySddl);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint Low;
        internal uint High;

        internal long ToLong() => ((long)High << 32) | Low;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(
        IntPtr hKey,
        string lpSubKey,
        uint ulOptions,
        uint samDesired,
        out SafeRegistryHandle phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(
        IntPtr hKey,
        string? lpValueName,
        IntPtr lpReserved,
        out uint lpType,
        byte[]? lpData,
        ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(
        IntPtr hKey,
        StringBuilder? lpClass,
        ref uint lpcchClass,
        IntPtr lpReserved,
        out uint lpcSubKeys,
        out uint lpcbMaxSubKeyLen,
        IntPtr lpcbMaxClassLen,
        out uint lpcValues,
        out uint lpcbMaxValueNameLen,
        out uint lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor,
        out NativeFileTime lpftLastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyExW(
        IntPtr hKey,
        uint dwIndex,
        StringBuilder lpName,
        ref uint lpcchName,
        IntPtr lpReserved,
        StringBuilder? lpClass,
        IntPtr lpcchClass,
        IntPtr lpftLastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegEnumValueW(
        IntPtr hKey,
        uint dwIndex,
        StringBuilder lpValueName,
        ref uint lpcchValueName,
        IntPtr lpReserved,
        out uint lpType,
        byte[]? lpData,
        ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValueW(IntPtr hKey, string? lpValueName);

    [DllImport("advapi32.dll")]
    private static extern int RegGetKeySecurity(
        IntPtr hKey,
        uint securityInformation,
        byte[]? securityDescriptor,
        ref uint securityDescriptorSize);

    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(IntPtr keyHandle);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
