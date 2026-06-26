using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
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
                var opt = fr.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(expanded, fr.Pattern, opt))
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
                var opt = fr.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                files.AddRange(Directory.EnumerateFiles(expanded, fr.Pattern, opt));
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
                    else fi.Delete();
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
                try { Directory.Delete(expanded, recursive: true); }
                catch (Exception ex) { Log.Warn($"RemoveSelf '{expanded}': {ex.Message}"); }
            }
        }

        foreach (var regPath in rule.Registry)
        {
            if (!SafetyGuard.IsRegistryPathSafeToDelete(regPath)) continue;
            if (options.DryRun) continue;
            try
            {
                var parts = regPath.Split('\\', 2);
                if (parts.Length < 2 || string.IsNullOrEmpty(parts[1])) continue;
                var hive = parts[0].ToUpperInvariant() switch
                {
                    "HKCU" => Microsoft.Win32.Registry.CurrentUser,
                    "HKLM" => Microsoft.Win32.Registry.LocalMachine,
                    _ => null
                };
                hive?.DeleteSubKeyTree(parts[1], throwOnMissingSubKey: false);
            }
            catch (Exception ex) { Log.Warn($"Cleaner registry delete '{regPath}': {ex.Message}"); }
        }

        if (!options.DryRun && cleaned > 0)
            ActivityLog.Record("cleaner", $"{rule.Name}: {cleaned} items", freed, cleaned);

        return new DeleteSummary(cleaned, skipped, freed, options.DryRun);
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
