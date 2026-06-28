using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Cleaning;

public class CleanerRule
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Detect { get; set; } = new();
    public List<string> DetectFile { get; set; } = new();
    public List<CleanerFileRule> Files { get; set; } = new();
    public List<string> Registry { get; set; } = new();
}

public class CleanerFileRule
{
    public string Path { get; set; } = "";
    public string Pattern { get; set; } = "*";
    public bool Recurse { get; set; }
    public bool RemoveSelf { get; set; }
}

public static class CleanerDefinitionRunner
{
    public static List<CleanerRule> LoadAll()
    {
        var rules = new List<CleanerRule>();
        try
        {
            EnsureBundledCleaners();
            var dir = DataPaths.Cleaners;
            if (!Directory.Exists(dir)) return rules;

            foreach (var file in Directory.GetFiles(dir, "*.cleaner.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var parsed = JsonSerializer.Deserialize<List<CleanerRule>>(json);
                    if (parsed != null) rules.AddRange(parsed);
                }
                catch (Exception ex) { Log.Warn($"Cleaner parse '{file}': {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Cleaner scan: {ex.Message}"); }
        return rules;
    }

    public static List<CleanerRule> FilterApplicable(List<CleanerRule> rules)
    {
        return rules.Where(IsApplicable).ToList();
    }

    public static bool IsApplicable(CleanerRule rule)
    {
        foreach (var regKey in rule.Detect)
        {
            if (!RegistryKeyExists(regKey)) return false;
        }
        foreach (var filePath in rule.DetectFile)
        {
            var expanded = Environment.ExpandEnvironmentVariables(filePath);
            if (expanded.Contains("..")) return false;
            if (!File.Exists(expanded) && !Directory.Exists(expanded)) return false;
        }
        return true;
    }

    public static (long Size, int ItemCount) Preview(CleanerRule rule)
    {
        long size = 0;
        int count = 0;
        foreach (var fr in rule.Files)
        {
            var expanded = Environment.ExpandEnvironmentVariables(fr.Path);
            if (!Directory.Exists(expanded)) continue;

            try
            {
                var enumFiles = fr.Recurse
                    ? SafetyGuard.SafeEnumerateFiles(expanded, fr.Pattern)
                    : Directory.EnumerateFiles(expanded, fr.Pattern, SearchOption.TopDirectoryOnly);
                foreach (var f in enumFiles)
                {
                    if (!SafetyGuard.IsPathSafeToDelete(f)) continue;
                    try { size += new FileInfo(f).Length; count++; }
                    catch { /* skip */ }
                }
            }
            catch { /* skip */ }
        }
        return (size, count);
    }

    public static DeleteSummary Execute(CleanerRule rule, DeleteOptions options,
        IProgress<DeleteProgress>? progress = null, CancellationToken ct = default)
    {
        long freed = 0;
        int cleaned = 0, skipped = 0;
        var files = new List<string>();

        foreach (var fr in rule.Files)
        {
            var expanded = Environment.ExpandEnvironmentVariables(fr.Path);
            if (!Directory.Exists(expanded)) continue;

            try
            {
                var enumFiles = fr.Recurse
                    ? SafetyGuard.SafeEnumerateFiles(expanded, fr.Pattern)
                    : Directory.EnumerateFiles(expanded, fr.Pattern, SearchOption.TopDirectoryOnly);
                files.AddRange(enumFiles);
            }
            catch { /* skip */ }
        }

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];

            if (!SafetyGuard.IsPathSafeToDelete(f)) { skipped++; continue; }

            try
            {
                var fi = new FileInfo(f);
                var sz = fi.Length;

                if (!options.DryRun)
                {
                    if (options.SecureDelete) SecureDelete.Wipe(f);
                    else SafetyGuard.SafeDeleteFile(f);
                }

                freed += sz;
                cleaned++;
            }
            catch { skipped++; }

            progress?.Report(new DeleteProgress(i + 1, files.Count, freed, f, false));
        }

        foreach (var fr in rule.Files.Where(f => f.RemoveSelf))
        {
            var expanded = Environment.ExpandEnvironmentVariables(fr.Path);
            if (!options.DryRun && Directory.Exists(expanded) && SafetyGuard.IsPathSafeToDelete(expanded))
            {
                try { SafetyGuard.SafeDeleteDirectory(expanded); }
                catch (Exception ex) { Log.Warn($"RemoveSelf '{expanded}': {ex.Message}"); }
            }
        }

        foreach (var regPath in rule.Registry)
        {
            var result = RegistryDeletion.DeleteKeyTree(regPath, "cleaner-regkey", options.DryRun);
            if (result.Status is RegistryDeletionStatus.Deleted or RegistryDeletionStatus.DryRun or RegistryDeletionStatus.SkippedMissing)
                continue;

            Log.Warn($"Cleaner registry delete '{regPath}' skipped: {result.Status} {result.ErrorMessage}");
        }

        if (!options.DryRun && cleaned > 0)
            ActivityLog.Record("cleaner", $"{rule.Name}: {cleaned} items", freed, cleaned);

        return new DeleteSummary(cleaned, skipped, freed, options.DryRun);
    }

    private static void EnsureBundledCleaners()
    {
        try
        {
            var dir = DataPaths.Cleaners;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "bundled-modern-apps.cleaner.json");
            if (File.Exists(target)) return;
            File.WriteAllText(target, BundledCleaners.ModernApps, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { Log.Warn($"Bundled cleaner extract: {ex.Message}"); }
    }

    private static bool RegistryKeyExists(string path)
    {
        try
        {
            var parts = path.Split('\\', 2);
            if (parts.Length < 2) return false;
            var hive = parts[0].ToUpperInvariant() switch
            {
                "HKCU" => Microsoft.Win32.Registry.CurrentUser,
                "HKLM" => Microsoft.Win32.Registry.LocalMachine,
                "HKCR" => Microsoft.Win32.Registry.ClassesRoot,
                _ => null
            };
            if (hive == null) return false;
            using var key = hive.OpenSubKey(parts[1]);
            return key != null;
        }
        catch { return false; }
    }
}

internal static class BundledCleaners
{
    internal const string ModernApps = """
[
  {
    "Name": "VS Code",
    "Description": "Visual Studio Code caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\Microsoft VS Code"],
    "Files": [
      { "Path": "%APPDATA%\\Code\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\CachedData", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\CachedExtensionVSIXs", "Pattern": "*.vsix", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\logs", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Code\\Service Worker\\CacheStorage", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Cursor",
    "Description": "Cursor AI editor caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\cursor"],
    "Files": [
      { "Path": "%APPDATA%\\Cursor\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Cursor\\CachedData", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Cursor\\logs", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Cursor\\Service Worker\\CacheStorage", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Windsurf",
    "Description": "Windsurf editor caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\windsurf"],
    "Files": [
      { "Path": "%APPDATA%\\Windsurf\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Windsurf\\CachedData", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Windsurf\\logs", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Discord",
    "Description": "Discord caches and crash reports",
    "DetectFile": ["%LOCALAPPDATA%\\Discord"],
    "Files": [
      { "Path": "%APPDATA%\\discord\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\discord\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\discord\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Slack",
    "Description": "Slack caches and logs",
    "DetectFile": ["%LOCALAPPDATA%\\slack"],
    "Files": [
      { "Path": "%APPDATA%\\Slack\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Slack\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Slack\\logs", "Pattern": "*.log", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Slack\\Service Worker\\CacheStorage", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Microsoft Teams",
    "Description": "Teams caches and logs (new Teams app)",
    "DetectFile": ["%LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\\Default\\Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\\EBWebView\\Default\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Notion",
    "Description": "Notion desktop caches",
    "DetectFile": ["%LOCALAPPDATA%\\Programs\\Notion"],
    "Files": [
      { "Path": "%APPDATA%\\Notion\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Notion\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Notion\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Obsidian",
    "Description": "Obsidian caches and crash reports",
    "DetectFile": ["%LOCALAPPDATA%\\Obsidian"],
    "Files": [
      { "Path": "%APPDATA%\\obsidian\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\obsidian\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\obsidian\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Figma",
    "Description": "Figma desktop caches",
    "DetectFile": ["%LOCALAPPDATA%\\Figma"],
    "Files": [
      { "Path": "%APPDATA%\\Figma\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Figma\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Figma\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Docker Desktop",
    "Description": "Docker Desktop logs and caches",
    "DetectFile": ["%PROGRAMFILES%\\Docker\\Docker"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Docker\\log", "Pattern": "*.log", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Docker\\wsl\\data\\tmp", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Zen Browser",
    "Description": "Zen Browser caches (Firefox-based)",
    "DetectFile": ["%APPDATA%\\zen"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\zen\\Profiles", "Pattern": "cache2", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\zen\\Profiles", "Pattern": "startupCache", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Arc Browser",
    "Description": "Arc Browser caches (Chromium-based)",
    "DetectFile": ["%LOCALAPPDATA%\\Arc"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Arc\\User Data\\Default\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Arc\\User Data\\Default\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Arc\\User Data\\Default\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Claude Desktop",
    "Description": "Claude Desktop caches and logs",
    "DetectFile": ["%APPDATA%\\Claude"],
    "Files": [
      { "Path": "%APPDATA%\\Claude\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Claude\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Claude\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Claude\\logs", "Pattern": "*.log", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "WSL Caches",
    "Description": "Windows Subsystem for Linux temp and cache files",
    "DetectFile": ["%LOCALAPPDATA%\\Packages\\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Packages\\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\\LocalState\\rootfs\\tmp", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Postman",
    "Description": "Postman API client caches",
    "DetectFile": ["%LOCALAPPDATA%\\Postman"],
    "Files": [
      { "Path": "%APPDATA%\\Postman\\Cache\\Cache_Data", "Pattern": "*", "Recurse": false, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Postman\\Code Cache", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%APPDATA%\\Postman\\GPUCache", "Pattern": "*", "Recurse": false, "RemoveSelf": false }
    ]
  },
  {
    "Name": "Spotify",
    "Description": "Spotify caches (streaming data, album art)",
    "DetectFile": ["%APPDATA%\\Spotify"],
    "Files": [
      { "Path": "%LOCALAPPDATA%\\Spotify\\Storage", "Pattern": "*", "Recurse": true, "RemoveSelf": false },
      { "Path": "%LOCALAPPDATA%\\Spotify\\Data", "Pattern": "*", "Recurse": true, "RemoveSelf": false }
    ]
  }
]
""";
}
