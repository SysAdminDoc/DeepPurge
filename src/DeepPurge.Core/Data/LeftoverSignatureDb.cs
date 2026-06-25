using System.Reflection;
using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Data;

public class LeftoverSignature
{
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public List<string> Registry { get; set; } = new();
}

public class LeftoverMatch
{
    public List<string> FilePaths { get; set; } = new();
    public List<string> RegistryPaths { get; set; } = new();
}

public static class LeftoverSignatureDb
{
    private static readonly Lazy<List<LeftoverSignature>> _signatures = new(Load);

    public static LeftoverMatch? FindMatch(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var sig = _signatures.Value.FirstOrDefault(s =>
            s.Aliases.Any(a => displayName.Contains(a, StringComparison.OrdinalIgnoreCase)));
        if (sig == null) return null;

        var match = new LeftoverMatch();
        foreach (var pattern in sig.Files)
        {
            var expanded = Environment.ExpandEnvironmentVariables(pattern);
            if (expanded.Contains('*'))
            {
                var dir = Path.GetDirectoryName(expanded);
                var glob = Path.GetFileName(expanded);
                if (dir != null && Directory.Exists(dir))
                {
                    try
                    {
                        foreach (var d in Directory.GetDirectories(dir, glob))
                            match.FilePaths.Add(d);
                    }
                    catch (Exception ex) { Log.Warn($"Signature glob '{glob}' in '{dir}': {ex.Message}"); }
                }
            }
            else if (Directory.Exists(expanded) || File.Exists(expanded))
            {
                match.FilePaths.Add(expanded);
            }
        }

        foreach (var regPath in sig.Registry)
            match.RegistryPaths.Add(regPath);

        return (match.FilePaths.Count > 0 || match.RegistryPaths.Count > 0) ? match : null;
    }

    public record OrphanResult(string ProgramName, LeftoverMatch Match);

    public static List<OrphanResult> ScanForOrphans(ISet<string> installedNames)
    {
        var orphans = new List<OrphanResult>();

        foreach (var sig in _signatures.Value)
        {
            if (installedNames.Any(n => sig.Aliases.Any(a =>
                n.Contains(a, StringComparison.OrdinalIgnoreCase))))
                continue;

            var match = new LeftoverMatch();
            foreach (var pattern in sig.Files)
            {
                var expanded = Environment.ExpandEnvironmentVariables(pattern);
                if (expanded.Contains('*'))
                {
                    var dir = Path.GetDirectoryName(expanded);
                    var glob = Path.GetFileName(expanded);
                    if (dir != null && Directory.Exists(dir))
                    {
                        try { foreach (var d in Directory.GetDirectories(dir, glob)) match.FilePaths.Add(d); }
                        catch { /* skip inaccessible */ }
                    }
                }
                else if (Directory.Exists(expanded) || File.Exists(expanded))
                {
                    match.FilePaths.Add(expanded);
                }
            }

            foreach (var regPath in sig.Registry)
            {
                try
                {
                    var parts = regPath.Split('\\', 2);
                    if (parts.Length < 2) continue;
                    var hive = parts[0].ToUpperInvariant() switch
                    {
                        "HKLM" => Microsoft.Win32.Registry.LocalMachine,
                        "HKCU" => Microsoft.Win32.Registry.CurrentUser,
                        _ => null
                    };
                    if (hive == null) continue;
                    using var key = hive.OpenSubKey(parts[1]);
                    if (key != null) match.RegistryPaths.Add(regPath);
                }
                catch { /* skip inaccessible */ }
            }

            if (match.FilePaths.Count > 0 || match.RegistryPaths.Count > 0)
                orphans.Add(new OrphanResult(sig.Name, match));
        }

        return orphans;
    }

    private static List<LeftoverSignature> Load()
    {
        var all = new List<LeftoverSignature>();

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("leftover-signatures.json", StringComparison.OrdinalIgnoreCase));
            if (resourceName != null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    var embedded = JsonSerializer.Deserialize<List<LeftoverSignature>>(stream);
                    if (embedded != null) all.AddRange(embedded);
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to load embedded signatures: {ex.Message}"); }

        try
        {
            var cleanersDir = DataPaths.Cleaners;
            if (Directory.Exists(cleanersDir))
            {
                foreach (var file in Directory.GetFiles(cleanersDir, "*.signatures.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var external = JsonSerializer.Deserialize<List<LeftoverSignature>>(json);
                        if (external == null) continue;
                        foreach (var ext in external)
                        {
                            var existing = all.FindIndex(s =>
                                s.Name.Equals(ext.Name, StringComparison.OrdinalIgnoreCase));
                            if (existing >= 0)
                                all[existing] = ext;
                            else
                                all.Add(ext);
                        }
                    }
                    catch (Exception ex) { Log.Warn($"Failed to load {file}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to scan external signatures: {ex.Message}"); }

        return all;
    }
}
