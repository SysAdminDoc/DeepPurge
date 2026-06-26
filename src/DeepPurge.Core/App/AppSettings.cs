using System.Text.Json;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.App;

public class AppSettings
{
    public bool ExpertMode { get; set; }
    public List<string> ExcludedPaths { get; set; } = new();
    public int MinAgeDaysJunk { get; set; }
    public int MinAgeDaysEvidence { get; set; }

    private static readonly Lazy<AppSettings> _instance = new(Load);
    private static readonly object _saveLock = new();
    public static AppSettings Current => _instance.Value;

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(DataPaths.SettingsFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                var tmp = DataPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, DataPaths.SettingsFile, overwrite: true);
            }
            catch (Exception ex) { Log.Warn($"Failed to save settings: {ex.Message}"); }
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(DataPaths.SettingsFile)) return new AppSettings();
            var json = File.ReadAllText(DataPaths.SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load settings: {ex.Message}");
            return new AppSettings();
        }
    }
}
