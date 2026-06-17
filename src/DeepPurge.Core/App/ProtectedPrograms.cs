using System.Text.Json;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.App;

public static class ProtectedPrograms
{
    private static readonly string FilePath = Path.Combine(DataPaths.Config, "protected-programs.json");
    private static HashSet<string>? _list;

    public static HashSet<string> List
    {
        get
        {
            _list ??= Load();
            return _list;
        }
    }

    public static bool IsProtected(string displayName)
        => !string.IsNullOrEmpty(displayName) && List.Contains(displayName);

    public static void Add(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        List.Add(displayName);
        Save();
    }

    public static void Remove(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        List.Remove(displayName);
        Save();
    }

    public static void Toggle(string displayName)
    {
        if (IsProtected(displayName)) Remove(displayName);
        else Add(displayName);
    }

    private static HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list != null ? new(list, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load protected programs: {ex.Message}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(_list?.ToList() ?? new());
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) { Log.Warn($"Failed to save protected programs: {ex.Message}"); }
    }
}
