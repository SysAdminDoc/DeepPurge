using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Registry;

public record AmcacheEntry(string Name, string Publisher, string InstallPath, string Version, DateTime InstallDate);

public static class AmcacheParser
{
    public static List<AmcacheEntry> Parse()
    {
        var entries = new List<AmcacheEntry>();
        try { ParseFromBam(entries); }
        catch (Exception ex) { Log.Warn($"Amcache parse: {ex.Message}"); }
        return entries;
    }

    public static List<AmcacheEntry> FindRemnants(ISet<string> installedNames)
    {
        var all = Parse();
        return all.Where(e =>
            !string.IsNullOrEmpty(e.InstallPath) &&
            !string.IsNullOrEmpty(e.Name) &&
            !installedNames.Any(n => !string.IsNullOrEmpty(n) &&
                n.Contains(e.Name, StringComparison.OrdinalIgnoreCase)) &&
            (Directory.Exists(e.InstallPath) || File.Exists(e.InstallPath)))
            .ToList();
    }

    private static void ParseFromBam(List<AmcacheEntry> entries)
    {
        try
        {
            using var arpKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\ControlSet001\Services\bam\State\UserSettings");
            if (arpKey == null) return;

            foreach (var sid in arpKey.GetSubKeyNames())
            {
                try
                {
                    using var userKey = arpKey.OpenSubKey(sid);
                    if (userKey == null) continue;

                    foreach (var valueName in userKey.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(valueName) || !valueName.Contains('\\')) continue;

                        try
                        {
                            var expanded = Environment.ExpandEnvironmentVariables(valueName);
                            if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (expanded.Contains("..")) continue;

                            var name = Path.GetFileNameWithoutExtension(expanded);
                            var dir = Path.GetDirectoryName(expanded) ?? "";

                            entries.Add(new AmcacheEntry(name, "", dir, "", DateTime.MinValue));
                        }
                        catch { /* skip individual entries */ }
                    }
                }
                catch { /* skip inaccessible SIDs */ }
            }
        }
        catch (Exception ex) { Log.Warn($"BAM registry parse: {ex.Message}"); }
    }
}
