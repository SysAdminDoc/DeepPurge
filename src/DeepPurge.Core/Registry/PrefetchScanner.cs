using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Registry;

public static class PrefetchScanner
{
    private static readonly string PrefetchDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

    public static void EnrichWithLastUsed(IList<InstalledProgram> programs)
    {
        if (!Directory.Exists(PrefetchDir)) return;

        Dictionary<string, DateTime> prefetchMap;
        try
        {
            prefetchMap = BuildPrefetchMap();
        }
        catch (Exception ex)
        {
            Log.Warn($"Prefetch scan: {ex.Message}");
            return;
        }

        foreach (var p in programs)
        {
            var exeName = GuessExeName(p);
            if (string.IsNullOrEmpty(exeName)) continue;

            var key = exeName.ToUpperInvariant();
            if (prefetchMap.TryGetValue(key, out var lastUsed))
                p.LastUsedDate = lastUsed;
        }
    }

    private static Dictionary<string, DateTime> BuildPrefetchMap()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pf in Directory.EnumerateFiles(PrefetchDir, "*.pf"))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(pf);
                    var dashIdx = fileName.LastIndexOf('-');
                    if (dashIdx <= 0) continue;
                    var exeName = fileName[..dashIdx];
                    var lastWrite = File.GetLastWriteTime(pf);

                    if (!map.TryGetValue(exeName, out var existing) || lastWrite > existing)
                        map[exeName] = lastWrite;
                }
                catch { /* skip individual entries */ }
            }
        }
        catch (Exception ex) { Log.Warn($"Prefetch enumerate: {ex.Message}"); }
        return map;
    }

    private static string? GuessExeName(InstalledProgram p)
    {
        if (!string.IsNullOrEmpty(p.DisplayIconPath))
        {
            var icon = p.DisplayIconPath.Split(',')[0].Trim('"', ' ');
            if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(icon);
        }

        if (!string.IsNullOrEmpty(p.UninstallString))
        {
            var cmd = p.UninstallString.Trim('"', ' ');
            var spaceIdx = cmd.IndexOf(' ');
            if (spaceIdx > 0) cmd = cmd[..spaceIdx];
            cmd = cmd.Trim('"');
            if (cmd.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(cmd);
                if (!IsInstallerExe(name)) return name;
            }
        }

        return null;
    }

    private static bool IsInstallerExe(string name) =>
        name.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("unins000", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("unins001", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("uninst", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("setup", StringComparison.OrdinalIgnoreCase);
}
