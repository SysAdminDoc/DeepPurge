using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeepPurge.Core.Icons;

/// <summary>
/// Shell-assisted icon extraction for InstalledProgram rows. All returned
/// <see cref="ImageSource"/> instances are frozen, so they can be assigned
/// across threads without WPF dispatcher affinity issues.
/// </summary>
public static class IconExtractor
{
    private static readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BitmapSource _defaultIcon = CreateDefaultIcon();

    // ── Shell32 P/Invoke ──────────────────────────────────────────
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // ── Public API ────────────────────────────────────────────────

    public static ImageSource DefaultIcon => _defaultIcon;

    public static ImageSource? GetProgramIcon(string? displayIconPath, string? uninstallString, string? installLocation)
    {
        var cacheKey = BuildCacheKey(displayIconPath, uninstallString, installLocation);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached ?? _defaultIcon;

        ImageSource? result = null;

        // 1. Try DisplayIcon path (most reliable)
        if (!string.IsNullOrEmpty(displayIconPath))
            result = ExtractFromDisplayIcon(displayIconPath);

        // 2. Try exe from UninstallString
        if (result == null && !string.IsNullOrEmpty(uninstallString))
        {
            var exePath = ExtractExePath(uninstallString);
            if (!string.IsNullOrEmpty(exePath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(exePath);
                if (File.Exists(expanded)) result = ExtractFromFile(expanded);
            }
        }

        // 3. Try InstallLocation (top-level, then one level deep, at most 5 exes each)
        if (result == null && !string.IsNullOrEmpty(installLocation))
        {
            var loc = Environment.ExpandEnvironmentVariables(installLocation);
            if (Directory.Exists(loc))
            {
                try
                {
                    result = ProbeFolderForIcon(loc, SearchOption.TopDirectoryOnly);
                    result ??= ProbeFolderForIcon(loc, SearchOption.AllDirectories, maxDepth: 1);
                }
                catch { /* skip */ }
            }
        }

        // 4. Try App Paths registry lookup
        if (result == null && !string.IsNullOrEmpty(uninstallString))
            result = TryAppPathsLookup(uninstallString);

        // 5. Last resort: SHGetFileInfo with USEFILEATTRIBUTES (works even if file is missing)
        if (result == null && !string.IsNullOrEmpty(displayIconPath))
            result = TryUseFileAttributesFallback(displayIconPath);

        _cache[cacheKey] = result;
        return result ?? _defaultIcon;
    }

    public static void ClearCache() => _cache.Clear();

    // ═══════════════════════════════════════════════════════
    //  Extraction Methods
    // ═══════════════════════════════════════════════════════

    private static ImageSource? ProbeFolderForIcon(string folder, SearchOption option, int maxDepth = 0)
    {
        IEnumerable<string> files;
        try
        {
            files = option == SearchOption.TopDirectoryOnly
                ? Directory.EnumerateFiles(folder, "*.exe", option)
                : Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = maxDepth,
                    IgnoreInaccessible = true,
                });
        }
        catch { return null; }

        foreach (var exe in files.Take(5))
        {
            var icon = ExtractFromFile(exe);
            if (icon != null) return icon;
        }
        return null;
    }

    private static ImageSource? ExtractFromDisplayIcon(string displayIconPath)
    {
        var (path, iconIndex) = ParseIconSpec(displayIconPath);
        if (string.IsNullOrEmpty(path)) return null;

        path = Environment.ExpandEnvironmentVariables(path);

        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 32;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { /* fall through */ }
        }

        return File.Exists(path) ? ExtractIconAtIndex(path, iconIndex) : null;
    }

    private static ImageSource? TryUseFileAttributesFallback(string displayIconPath)
    {
        var (path, _) = ParseIconSpec(displayIconPath);
        if (string.IsNullOrEmpty(path) || !path.Contains('.')) return null;

        path = Environment.ExpandEnvironmentVariables(path);
        var shfi = new SHFILEINFO();
        var r = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref shfi,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        if (r == 0 || shfi.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally { DestroyIcon(shfi.hIcon); }
    }

    private static ImageSource? TryAppPathsLookup(string uninstallString)
    {
        try
        {
            var exeName = ExtractExePath(uninstallString);
            if (string.IsNullOrEmpty(exeName)) return null;
            exeName = Path.GetFileName(exeName);
            if (string.IsNullOrEmpty(exeName)) return null;

            using var key = global::Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            var appPath = key?.GetValue(null)?.ToString();
            if (string.IsNullOrEmpty(appPath)) return null;

            appPath = Environment.ExpandEnvironmentVariables(appPath.Trim('"'));
            return File.Exists(appPath) ? ExtractFromFile(appPath) : null;
        }
        catch { return null; }
    }

    private static ImageSource? ExtractIconAtIndex(string filePath, int index)
    {
        var hIcon = IntPtr.Zero;
        try
        {
            hIcon = ExtractIcon(IntPtr.Zero, filePath, index);
            // hIcon == 1 means "no icons in file"; treat as miss.
            if (hIcon == IntPtr.Zero || hIcon.ToInt64() == 1) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally
        {
            if (hIcon != IntPtr.Zero && hIcon.ToInt64() != 1) DestroyIcon(hIcon);
        }
    }

    private static ImageSource? ExtractFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var shfi = new SHFILEINFO();
        var result = SHGetFileInfo(filePath, 0, ref shfi,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);

        if (result != 0 && shfi.hIcon != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                return source;
            }
            catch { /* fall through to ExtractIcon */ }
            finally { DestroyIcon(shfi.hIcon); }
        }

        return ExtractIconAtIndex(filePath, 0);
    }

    private static (string path, int index) ParseIconSpec(string displayIconPath)
    {
        var path = displayIconPath.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 2)
        {
            var indexStr = path[(comma + 1)..].Trim();
            if (int.TryParse(indexStr, out var idx))
                return (path[..comma].Trim().Trim('"'), idx);
        }
        return (path, 0);
    }

    private static string ExtractExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return command[1..end];
        }

        if (command.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return "";

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    /// <summary>
    /// Uses null separators in the cache key so path components with `|`
    /// never collide with each other. Null bytes cannot appear in real paths.
    /// </summary>
    private static string BuildCacheKey(string? displayIcon, string? uninstall, string? installLoc)
    {
        var sb = new StringBuilder();
        sb.Append(displayIcon ?? "").Append('\0');
        sb.Append(uninstall ?? "").Append('\0');
        sb.Append(installLoc ?? "");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════
    //  Default icon
    // ═══════════════════════════════════════════════════════

    private static BitmapSource CreateDefaultIcon()
    {
        const int w = 32, h = 32;
        const int stride = w * 4;
        var pixels = new byte[h * stride];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * stride) + (x * 4);

                // Outer rounded rect with ~5px corner radius.
                int dx = Math.Min(x - 3, 28 - x);
                int dy = Math.Min(y - 3, 28 - y);
                if (dx < 0 || dy < 0) { pixels[idx + 3] = 0; continue; }
                if (dx < 5 && dy < 5)
                {
                    double dist = Math.Sqrt((5 - dx) * (5 - dx) + (5 - dy) * (5 - dy));
                    if (dist > 5.5) { pixels[idx + 3] = 0; continue; }
                }

                // Little inner "box + bar" glyph.
                bool isInnerSquare = x >= 11 && x <= 20 && y >= 10 && y <= 19;
                bool isBar = x >= 10 && x <= 21 && y >= 22 && y <= 23;

                if (isInnerSquare || isBar)
                {
                    pixels[idx + 0] = 140; pixels[idx + 1] = 140; pixels[idx + 2] = 155; pixels[idx + 3] = 200;
                }
                else
                {
                    pixels[idx + 0] = 60; pixels[idx + 1] = 62; pixels[idx + 2] = 78; pixels[idx + 3] = 160;
                }
            }
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
