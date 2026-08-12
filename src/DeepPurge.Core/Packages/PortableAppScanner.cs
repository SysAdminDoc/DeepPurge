using System.Diagnostics;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Packages;

public sealed record PortableApp(string Name, string ExePath, string Version, long SizeBytes);

public static class PortableAppScanner
{
    private const long MinExeBytes = 512 * 1024;

    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "install", "installer", "uninstall", "unins000", "unins001",
        "update", "updater", "helper", "crash_reporter", "crashreporter",
        "7zS", "7zFM", "elevate", "patcher",
    };

    public static List<PortableApp> Scan(ISet<string>? knownNames = null)
    {
        var known = knownNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PortableApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in GetScanRoots())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                ScanDirectory(root, results, known, seen, depth: 0, maxDepth: 2);
            }
            catch (Exception ex) { Log.Warn($"Portable scan failed for '{root}': {ex.Message}"); }
        }

        return results;
    }

    public static void InjectIntoPrograms(IList<InstalledProgram> programs, List<PortableApp> portables)
    {
        var existing = new HashSet<string>(
            programs.Select(p => p.DisplayName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var app in portables)
        {
            if (existing.Contains(app.Name)) continue;
            var program = new InstalledProgram
            {
                DisplayName = app.Name,
                DisplayVersion = app.Version,
                InstallLocation = Path.GetDirectoryName(app.ExePath) ?? "",
                Publisher = "Portable",
                Source = RegistrySource.Portable,
                PackageManager = "portable",
                ActualSizeBytes = app.SizeBytes,
            };
            RemovalCapabilityInspector.Populate(program);
            programs.Add(program);
            existing.Add(app.Name);
        }
    }

    private static void ScanDirectory(string dir, List<PortableApp> results,
        ISet<string> knownNames, ISet<string> seen, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        try
        {
            foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fi = new FileInfo(exe);
                    if (fi.Length < MinExeBytes) continue;

                    var baseName = Path.GetFileNameWithoutExtension(exe);
                    if (IgnoredNames.Contains(baseName)) continue;
                    if (knownNames.Contains(baseName)) continue;
                    if (!seen.Add(baseName)) continue;

                    var version = GetFileVersion(exe);
                    var folderSize = depth > 0
                        ? GetDirectorySize(Path.GetDirectoryName(exe)!)
                        : fi.Length;

                    results.Add(new PortableApp(baseName, exe, version, folderSize));
                }
                catch (Exception ex) { Log.Warn($"Portable exe check '{exe}': {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Portable dir enum '{dir}': {ex.Message}"); }

        if (depth >= maxDepth) return;
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
                    ScanDirectory(subDir, results, knownNames, seen, depth + 1, maxDepth);
                }
                catch { /* skip inaccessible subdirs */ }
            }
        }
        catch { /* skip inaccessible parent */ }
    }

    private static IEnumerable<string> GetScanRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemDrive = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";

        yield return Path.Combine(systemDrive, "PortableApps");
        yield return Path.Combine(userProfile, "PortableApps");
        yield return Path.Combine(userProfile, "Desktop");
        yield return Path.Combine(userProfile, "Downloads");

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
            yield return drive.RootDirectory.FullName;
            var paDir = Path.Combine(drive.RootDirectory.FullName, "PortableApps");
            if (Directory.Exists(paDir)) yield return paDir;
        }
    }

    private static string GetFileVersion(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            return !string.IsNullOrEmpty(vi.FileVersion) ? vi.FileVersion : "";
        }
        catch { return ""; }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Sum(fi => { try { return fi.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }
}
