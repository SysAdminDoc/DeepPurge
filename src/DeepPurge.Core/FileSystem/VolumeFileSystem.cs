using System.Runtime.InteropServices;
using System.Text;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.FileSystem;

public sealed record VolumeFileSystemInfo(string RootPath, string FileSystemName)
{
    public bool IsKnown => !string.IsNullOrWhiteSpace(FileSystemName);
    public bool IsNtfs => FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    public bool UsesFallbackEnumeration => !IsNtfs;
}

public static class VolumeFileSystem
{
    public static VolumeFileSystemInfo GetForPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return new VolumeFileSystemInfo("", "");
            if (!root.EndsWith('\\'))
                root += "\\";

            var fs = GetFileSystemName(root);
            return new VolumeFileSystemInfo(root, fs);
        }
        catch (Exception ex)
        {
            Log.Warn($"Volume filesystem detection failed for '{path}': {ex.Message}");
            return new VolumeFileSystemInfo("", "");
        }
    }

    public static bool IsNtfs(string path) => GetForPath(path).IsNtfs;

    public static bool UsesFallbackEnumeration(string path) => GetForPath(path).UsesFallbackEnumeration;

    private static string GetFileSystemName(string rootPath)
    {
        var fsName = new StringBuilder(64);
        if (!GetVolumeInformationW(
                rootPath,
                null,
                0,
                out _,
                out _,
                out _,
                fsName,
                fsName.Capacity))
        {
            return "";
        }
        return fsName.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
