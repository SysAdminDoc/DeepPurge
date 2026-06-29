using System.Text.Json;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.App;

public class AppSettings
{
    public bool ExpertMode { get; set; }
    public List<string> ExcludedPaths { get; set; } = new();
    public int MinAgeDaysJunk { get; set; }
    public int MinAgeDaysEvidence { get; set; }
    public int RetentionDaysLogs { get; set; } = 30;
    public int RetentionDaysActivity { get; set; } = 90;
    public int RetentionDaysDeletionManifests { get; set; } = 90;
    public bool ScrubSensitivePathsInReports { get; set; }
    public List<string> CookieWhitelist { get; set; } = new();
    public Dictionary<string, string> ProgramNotes { get; set; } = new();

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

    public void ExportTo(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
    }

    public static AppSettings ImportFrom(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidOperationException("Invalid settings file");
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
