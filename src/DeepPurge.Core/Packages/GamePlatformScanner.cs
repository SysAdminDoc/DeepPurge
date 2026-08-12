using System.Text.Json;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Packages;

public sealed record GameEntry(string Name, string Platform, string InstallPath, string Version);

public static class GamePlatformScanner
{
    public static List<GameEntry> ScanAll()
    {
        var games = new List<GameEntry>();
        ScanSteam(games);
        ScanEpic(games);
        ScanGog(games);
        return games;
    }

    public static void InjectIntoPrograms(IList<InstalledProgram> programs, List<GameEntry> games)
    {
        var existing = new HashSet<string>(
            programs.Select(p => p.DisplayName), StringComparer.OrdinalIgnoreCase);

        foreach (var g in games)
        {
            if (existing.Contains(g.Name)) continue;
            var program = new InstalledProgram
            {
                DisplayName = g.Name,
                DisplayVersion = g.Version,
                InstallLocation = g.InstallPath,
                Publisher = g.Platform,
                PackageManager = g.Platform.ToLowerInvariant(),
                Source = RegistrySource.Portable,
            };
            RemovalCapabilityInspector.Populate(program);
            programs.Add(program);
            existing.Add(g.Name);
        }
    }

    private static void ScanSteam(List<GameEntry> games)
    {
        try
        {
            var steamPath = FindSteamPath();
            if (steamPath == null) return;

            var libraryFolders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFolders)) return;

            var libraries = ParseVdfLibraryPaths(File.ReadAllText(libraryFolders));
            libraries.Add(steamPath);

            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var appsDir = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(appsDir)) continue;

                try
                {
                    foreach (var acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
                    {
                        try
                        {
                            var content = File.ReadAllText(acf);
                            var name = ExtractVdfValue(content, "name");
                            var installDir = ExtractVdfValue(content, "installdir");
                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir)) continue;

                            var fullPath = Path.Combine(appsDir, "common", installDir);
                            games.Add(new GameEntry(name, "Steam", fullPath, ""));
                        }
                        catch (Exception ex) { Log.Warn($"Steam ACF parse '{acf}': {ex.Message}"); }
                    }
                }
                catch (Exception ex) { Log.Warn($"Steam library scan '{lib}': {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Steam scan: {ex.Message}"); }
    }

    private static void ScanEpic(List<GameEntry> games)
    {
        try
        {
            var manifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestDir)) return;

            foreach (var file in Directory.GetFiles(manifestDir, "*.item"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? "" : "";
                    var installLoc = root.TryGetProperty("InstallLocation", out var loc) ? loc.GetString() ?? "" : "";
                    var version = root.TryGetProperty("AppVersionString", out var v) ? v.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(name))
                        games.Add(new GameEntry(name, "Epic", installLoc, version));
                }
                catch (Exception ex) { Log.Warn($"Epic manifest parse '{file}': {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Epic scan: {ex.Message}"); }
    }

    private static void ScanGog(List<GameEntry> games)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var gameKey = key.OpenSubKey(subKeyName);
                    if (gameKey == null) continue;

                    var name = gameKey.GetValue("gameName") as string ?? "";
                    var path = gameKey.GetValue("path") as string ?? "";
                    var version = gameKey.GetValue("ver") as string ?? "";

                    if (!string.IsNullOrEmpty(name))
                        games.Add(new GameEntry(name, "GOG", path, version));
                }
                catch (Exception ex) { Log.Warn($"GOG registry parse '{subKeyName}': {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"GOG scan: {ex.Message}"); }
    }

    private static string? FindSteamPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch { return null; }
    }

    private static List<string> ParseVdfLibraryPaths(string vdf)
    {
        var paths = new List<string>();
        foreach (var line in vdf.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split('"');
            if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]))
                paths.Add(parts[3].Replace("\\\\", "\\"));
        }
        return paths;
    }

    private static string ExtractVdfValue(string vdf, string key)
    {
        foreach (var line in vdf.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith($"\"{key}\"", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split('"');
            if (parts.Length >= 4) return parts[3];
        }
        return "";
    }
}
