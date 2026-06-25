using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Registry;

public record AmcacheEntry(string Name, string Publisher, string InstallPath, string Version, DateTime InstallDate);

public static class AmcacheParser
{
    private const string AmcacheRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\InboxApplications";
    private const string InventoryPath = @"ROOT\InventoryApplication";

    public static List<AmcacheEntry> Parse()
    {
        var entries = new List<AmcacheEntry>();

        try
        {
            var amcachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "appcompat", "Programs", "Amcache.hve");

            if (!File.Exists(amcachePath))
            {
                Log.Warn("Amcache.hve not found");
                return entries;
            }

            ParseFromRegistry(entries);
        }
        catch (Exception ex) { Log.Warn($"Amcache parse: {ex.Message}"); }

        return entries;
    }

    public static List<AmcacheEntry> FindRemnants(ISet<string> installedNames)
    {
        var all = Parse();
        return all.Where(e =>
            !string.IsNullOrEmpty(e.InstallPath) &&
            !installedNames.Any(n => n.Contains(e.Name, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(e.Name) &&
            (Directory.Exists(e.InstallPath) || File.Exists(e.InstallPath)))
            .ToList();
    }

    private static void ParseFromRegistry(List<AmcacheEntry> entries)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key == null) return;

            using var amKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications");

            var arpKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\ControlSet001\Services\bam\State\UserSettings");

            if (arpKey != null)
            {
                foreach (var sid in arpKey.GetSubKeyNames())
                {
                    try
                    {
                        using var userKey = arpKey.OpenSubKey(sid);
                        if (userKey == null) continue;

                        foreach (var valueName in userKey.GetValueNames())
                        {
                            if (string.IsNullOrEmpty(valueName)) continue;
                            if (!valueName.Contains('\\')) continue;

                            try
                            {
                                var exePath = valueName;
                                var expanded = Environment.ExpandEnvironmentVariables(exePath);
                                if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                                var name = Path.GetFileNameWithoutExtension(expanded);
                                var dir = Path.GetDirectoryName(expanded) ?? "";

                                entries.Add(new AmcacheEntry(name, "", dir, "", DateTime.MinValue));
                            }
                            catch { /* skip individual entries */ }
                        }
                    }
                    catch { /* skip inaccessible SIDs */ }
                }
                arpKey.Dispose();
            }
        }
        catch (Exception ex) { Log.Warn($"Amcache registry parse: {ex.Message}"); }
    }
}
