using System.Reflection;
using System.Text.Json;
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
                    catch { }
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

    private static List<LeftoverSignature> Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("leftover-signatures.json", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return new();

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return new();

            return JsonSerializer.Deserialize<List<LeftoverSignature>>(stream) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load leftover signatures: {ex.Message}");
            return new();
        }
    }
}
