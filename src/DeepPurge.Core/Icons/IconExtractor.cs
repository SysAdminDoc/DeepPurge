using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeepPurge.Core.Icons;

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

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    // ── Public API ────────────────────────────────────────────────

    public static ImageSource DefaultIcon => _defaultIcon;

    /// <summary>
    /// Extracts icon for a program. Tries DisplayIcon, UninstallString exe, InstallLocation exe.
    /// Returns a cached frozen BitmapSource suitable for binding from any thread.
    /// </summary>
    public static ImageSource? GetProgramIcon(string? displayIconPath, string? uninstallString, string? installLocation)
    {
        var cacheKey = $"{displayIconPath}|{uninstallString}|{installLocation}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached ?? _defaultIcon;

        ImageSource? result = null;

        // 1. Try DisplayIcon path (most reliable)
        if (!string.IsNullOrEmpty(displayIconPath))
        {
            result = ExtractFromDisplayIcon(displayIconPath);
        }

        // 2. Try exe from UninstallString
        if (result == null && !string.IsNullOrEmpty(uninstallString))
        {
            var exePath = ExtractExePath(uninstallString);
            if (!string.IsNullOrEmpty(exePath))
            {
                // Expand environment variables
                exePath = Environment.ExpandEnvironmentVariables(exePath);
                if (File.Exists(exePath))
                    result = ExtractFromFile(exePath);
            }
        }

        // 3. Try finding exes in InstallLocation (top-level + one level deep)
        if (result == null && !string.IsNullOrEmpty(installLocation))
        {
            var loc = Environment.ExpandEnvironmentVariables(installLocation);
            if (Directory.Exists(loc))
            {
                try
                {
                    // Top-level first
                    foreach (var exe in Directory.GetFiles(loc, "*.exe", SearchOption.TopDirectoryOnly).Take(5))
                    {
                        result = ExtractFromFile(exe);
                        if (result != null) break;
                    }
                    // One level deep if still null
                    if (result == null)
                    {
                        foreach (var exe in Directory.EnumerateFiles(loc, "*.exe", new EnumerationOptions
                            { MaxRecursionDepth = 1, RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(5))
                        {
                            result = ExtractFromFile(exe);
                            if (result != null) break;
                        }
                    }
                }
                catch { }
            }
        }

        // 4. Try App Paths registry lookup
        if (result == null && !string.IsNullOrEmpty(uninstallString))
        {
            result = TryAppPathsLookup(uninstallString);
        }

        // 5. Try SHGetFileInfo on DisplayIcon path with USEFILEATTRIBUTES flag (works for non-existent files)
        if (result == null && !string.IsNullOrEmpty(displayIconPath))
        {
            var path = displayIconPath.Trim().Trim('"');
            var comma = path.LastIndexOf(',');
            if (comma > 2) path = path[..comma].Trim().Trim('"');
            path = Environment.ExpandEnvironmentVariables(path);
            if (!string.IsNullOrEmpty(path) && path.Contains('.'))
            {
                var shfi = new SHFILEINFO();
                var r = SHGetFileInfo(path, 0x80, ref shfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                if (r != 0 && shfi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(32, 32));
                        source.Freeze();
                        result = source;
                    }
                    catch { }
                    finally { DestroyIcon(shfi.hIcon); }
                }
            }
        }

        _cache[cacheKey] = result;
        return result ?? _defaultIcon;
    }

    /// <summary>
    /// Pre-loads icons for a batch of programs on a background thread.
    /// Returns a dictionary mapping cache keys to ImageSource.
    /// </summary>
    public static Dictionary<string, ImageSource> BatchExtract(
        IEnumerable<(string? displayIcon, string? uninstall, string? installLoc)> programs)
    {
        var results = new Dictionary<string, ImageSource>();
        foreach (var (di, us, il) in programs)
        {
            var icon = GetProgramIcon(di, us, il);
            var key = $"{di}|{us}|{il}";
            if (icon != null) results[key] = icon;
        }
        return results;
    }

    public static void ClearCache() => _cache.Clear();

    // ── Extraction Methods ────────────────────────────────────────

    private static ImageSource? ExtractFromDisplayIcon(string displayIconPath)
    {
        if (string.IsNullOrEmpty(displayIconPath)) return null;

        var path = displayIconPath.Trim().Trim('"');
        int iconIndex = 0;

        // Handle "path,index" format
        var lastComma = path.LastIndexOf(',');
        if (lastComma > 2)
        {
            var indexStr = path[(lastComma + 1)..].Trim();
            if (int.TryParse(indexStr, out var idx))
            {
                iconIndex = idx;
                path = path[..lastComma].Trim().Trim('"');
            }
        }

        // Expand environment variables
        path = Environment.ExpandEnvironmentVariables(path);

        // Handle .ico files directly
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
            catch { }
        }

        // Extract from exe/dll
        if (File.Exists(path))
        {
            return ExtractIconAtIndex(path, iconIndex);
        }

        return null;
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
            if (!string.IsNullOrEmpty(appPath))
            {
                appPath = Environment.ExpandEnvironmentVariables(appPath.Trim('"'));
                if (File.Exists(appPath))
                    return ExtractFromFile(appPath);
            }
        }
        catch { }
        return null;
    }

    private static ImageSource? ExtractIconAtIndex(string filePath, int index)
    {
        var hIcon = IntPtr.Zero;
        try
        {
            hIcon = ExtractIcon(IntPtr.Zero, filePath, index);
            if (hIcon != IntPtr.Zero && hIcon.ToInt64() > 1)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                return source;
            }
        }
        catch { }
        finally
        {
            if (hIcon != IntPtr.Zero && hIcon.ToInt64() > 1)
                DestroyIcon(hIcon);
        }
        return null;
    }

    private static ImageSource? ExtractFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        // Try SHGetFileInfo first
        var shfi = new SHFILEINFO();
        var result = SHGetFileInfo(filePath, 0, ref shfi,
            (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);

        if (result != 0 && shfi.hIcon != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                return source;
            }
            catch { }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        // Fallback to ExtractIcon
        return ExtractIconAtIndex(filePath, 0);
    }

    private static string ExtractExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return command[1..end];
        }

        // Handle "MsiExec.exe /X{GUID}" etc.
        if (command.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            return "";

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static BitmapSource CreateDefaultIcon()
    {
        // 32x32 muted rounded square default icon
        int w = 32, h = 32;
        int stride = w * 4;
        var pixels = new byte[h * stride];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * stride) + (x * 4);
                // Rounded rect with 5px corner radius
                int dx = Math.Min(x - 3, 28 - x);
                int dy = Math.Min(y - 3, 28 - y);
                if (dx < 0 || dy < 0) { pixels[idx + 3] = 0; continue; }

                // Corner rounding
                if (dx < 5 && dy < 5)
                {
                    double dist = Math.Sqrt((5 - dx) * (5 - dx) + (5 - dy) * (5 - dy));
                    if (dist > 5.5) { pixels[idx + 3] = 0; continue; }
                }

                // Inner app icon shape (small square in center)
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
