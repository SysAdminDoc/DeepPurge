using System.Text.Json;
using System.Text.Json.Serialization;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.App;

public class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://sysadmindoc.github.io/deeppurge/schemas/settings.v1.json";

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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Current => _instance.Value;

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Normalize(this);
                WriteSettingsDocument(DataPaths.SettingsFile, this);
            }
            catch (Exception ex) { Log.Warn($"Failed to save settings: {ex.Message}"); }
        }
    }

    public SettingsImportPreview ExportTo(string path)
    {
        Normalize(this);
        WriteSettingsDocument(path, this);
        return CreatePreview(this, CurrentSchemaVersion, GetAppVersion(), DateTimeOffset.UtcNow);
    }

    public static AppSettings ImportFrom(string path)
    {
        var plan = PreviewImportFromFile(path);
        if (!plan.IsValid)
            throw new InvalidOperationException($"Invalid settings file: {plan.ErrorSummary}");
        return plan.Settings;
    }

    public static SettingsImportPlan PreviewImportFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return ParseImportDocument(json, path);
    }

    public static SettingsImportOutcome ImportAndApply(string path)
        => ImportAndApply(path, DataPaths.SettingsFile, applyToCurrent: true);

    public static SettingsImportOutcome ImportAndApply(string path, string targetSettingsFile, bool applyToCurrent)
    {
        var plan = PreviewImportFromFile(path);
        if (!plan.IsValid)
            throw new InvalidOperationException($"Invalid settings file: {plan.ErrorSummary}");

        var hadExisting = File.Exists(targetSettingsFile);
        var backupPath = hadExisting ? CreateRollbackBackup(targetSettingsFile) : null;

        try
        {
            WriteSettingsDocument(targetSettingsFile, plan.Settings);

            var verifyPlan = PreviewImportFromFile(targetSettingsFile);
            if (!verifyPlan.IsValid)
                throw new InvalidOperationException($"Imported settings failed verification: {verifyPlan.ErrorSummary}");

            if (applyToCurrent)
                Current.ApplyFrom(plan.Settings);

            return new SettingsImportOutcome(plan, backupPath);
        }
        catch
        {
            TryRollback(targetSettingsFile, backupPath, hadExisting);
            throw;
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(DataPaths.SettingsFile)) return new AppSettings();
            var plan = PreviewImportFromFile(DataPaths.SettingsFile);
            if (plan.IsValid) return plan.Settings;
            Log.Warn($"Failed to validate settings: {plan.ErrorSummary}");
            return new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load settings: {ex.Message}");
            return new AppSettings();
        }
    }

    private void ApplyFrom(AppSettings source)
    {
        ExpertMode = source.ExpertMode;
        ExcludedPaths = source.ExcludedPaths.ToList();
        MinAgeDaysJunk = source.MinAgeDaysJunk;
        MinAgeDaysEvidence = source.MinAgeDaysEvidence;
        RetentionDaysLogs = source.RetentionDaysLogs;
        RetentionDaysActivity = source.RetentionDaysActivity;
        RetentionDaysDeletionManifests = source.RetentionDaysDeletionManifests;
        ScrubSensitivePathsInReports = source.ScrubSensitivePathsInReports;
        CookieWhitelist = source.CookieWhitelist.ToList();
        ProgramNotes = new Dictionary<string, string>(source.ProgramNotes, StringComparer.OrdinalIgnoreCase);
    }

    private static SettingsImportPlan ParseImportDocument(string json, string source)
    {
        var issues = new List<SettingsImportIssue>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(SettingsImportIssue.Error("(file)", "Settings import must be a JSON object."));
                return BuildPlan(new AppSettings(), CurrentSchemaVersion, "", null, source, issues);
            }

            var root = doc.RootElement;
            var looksWrapped = root.TryGetProperty("Settings", out _) ||
                               root.TryGetProperty("SchemaVersion", out _) ||
                               root.TryGetProperty("$schema", out _);

            if (!looksWrapped)
            {
                var legacy = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                issues.Add(SettingsImportIssue.Warning(
                    "SchemaVersion",
                    "Legacy raw settings format detected; import will migrate it to settings schema v1."));
                return BuildPlan(legacy, 0, "", null, source, issues);
            }

            if (!root.TryGetProperty("SchemaVersion", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var schemaVersion))
            {
                issues.Add(SettingsImportIssue.Error("SchemaVersion", "SchemaVersion must be an integer."));
                return BuildPlan(new AppSettings(), CurrentSchemaVersion, "", null, source, issues);
            }

            var appVersion = root.TryGetProperty("AppVersion", out var appVersionElement)
                ? appVersionElement.GetString() ?? ""
                : "";
            var exportedAtUtc = TryReadExportedAtUtc(root);

            if (schemaVersion > CurrentSchemaVersion)
            {
                issues.Add(SettingsImportIssue.Error(
                    "SchemaVersion",
                    $"Unsupported future schema version {schemaVersion}; this DeepPurge build supports version {CurrentSchemaVersion}."));
            }
            else if (schemaVersion < CurrentSchemaVersion)
            {
                issues.Add(SettingsImportIssue.Warning(
                    "SchemaVersion",
                    $"Older schema version {schemaVersion}; import will migrate it to version {CurrentSchemaVersion}."));
            }

            if (!root.TryGetProperty("Settings", out var settingsElement) ||
                settingsElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(SettingsImportIssue.Error("Settings", "Settings must be an object."));
                return BuildPlan(new AppSettings(), schemaVersion, appVersion, exportedAtUtc, source, issues);
            }

            var settings = settingsElement.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
            return BuildPlan(settings, schemaVersion, appVersion, exportedAtUtc, source, issues);
        }
        catch (JsonException ex)
        {
            issues.Add(SettingsImportIssue.Error("(file)", $"Settings JSON is invalid: {ex.Message}"));
            return BuildPlan(new AppSettings(), CurrentSchemaVersion, "", null, source, issues);
        }
    }

    private static DateTimeOffset? TryReadExportedAtUtc(JsonElement root)
    {
        if (!root.TryGetProperty("ExportedAtUtc", out var element) ||
            element.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(element.GetString(), out var value) ? value : null;
    }

    private static SettingsImportPlan BuildPlan(
        AppSettings settings,
        int schemaVersion,
        string appVersion,
        DateTimeOffset? exportedAtUtc,
        string source,
        List<SettingsImportIssue> issues)
    {
        Normalize(settings);
        Validate(settings, issues);
        var preview = CreatePreview(settings, schemaVersion, appVersion, exportedAtUtc);
        return new SettingsImportPlan(settings, preview, schemaVersion, appVersion, exportedAtUtc, source, issues);
    }

    private static SettingsImportPreview CreatePreview(
        AppSettings settings,
        int schemaVersion,
        string appVersion,
        DateTimeOffset? exportedAtUtc)
        => new(
            schemaVersion,
            schemaVersion <= 0 ? "legacy raw settings" : $"settings schema v{schemaVersion}",
            appVersion,
            exportedAtUtc,
            settings.ExpertMode,
            settings.ExcludedPaths.Count,
            settings.CookieWhitelist.Count,
            settings.ProgramNotes.Count,
            settings.MinAgeDaysJunk,
            settings.MinAgeDaysEvidence,
            settings.RetentionDaysLogs,
            settings.RetentionDaysActivity,
            settings.RetentionDaysDeletionManifests,
            settings.ScrubSensitivePathsInReports);

    private static void Validate(AppSettings settings, List<SettingsImportIssue> issues)
    {
        ValidateDays(settings.MinAgeDaysJunk, "MinAgeDaysJunk", issues);
        ValidateDays(settings.MinAgeDaysEvidence, "MinAgeDaysEvidence", issues);
        ValidateDays(settings.RetentionDaysLogs, "RetentionDaysLogs", issues);
        ValidateDays(settings.RetentionDaysActivity, "RetentionDaysActivity", issues);
        ValidateDays(settings.RetentionDaysDeletionManifests, "RetentionDaysDeletionManifests", issues);

        foreach (var path in settings.ExcludedPaths)
        {
            if (ContainsParentTraversal(path))
                issues.Add(SettingsImportIssue.Error("ExcludedPaths", "Excluded paths cannot contain '..' path traversal segments."));
            if (path.IndexOf('\0') >= 0)
                issues.Add(SettingsImportIssue.Error("ExcludedPaths", "Excluded paths cannot contain null characters."));
        }

        foreach (var domain in settings.CookieWhitelist)
        {
            if (domain.IndexOfAny(new[] { '\\', '/', ' ' }) >= 0)
                issues.Add(SettingsImportIssue.Error("CookieWhitelist", "Cookie whitelist entries must be domains, not paths or phrases."));
        }

        foreach (var key in settings.ProgramNotes.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                issues.Add(SettingsImportIssue.Error("ProgramNotes", "Program note keys cannot be empty."));
        }
    }

    private static void ValidateDays(int value, string field, List<SettingsImportIssue> issues)
    {
        if (value < 0)
            issues.Add(SettingsImportIssue.Error(field, $"{field} must be 0 or higher."));
        else if (value > 36500)
            issues.Add(SettingsImportIssue.Warning(field, $"{field} is unusually high; 0 already means keep forever."));
    }

    private static void Normalize(AppSettings settings)
    {
        settings.ExcludedPaths = NormalizeList(settings.ExcludedPaths, lower: false);
        settings.CookieWhitelist = NormalizeList(settings.CookieWhitelist, lower: true);
        settings.ProgramNotes = settings.ProgramNotes
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
            .GroupBy(kvp => kvp.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeList(List<string>? values, bool lower)
    {
        return (values ?? new List<string>())
            .Select(v => (v ?? "").Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => lower ? v.ToLowerInvariant() : v)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsParentTraversal(string path)
    {
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p == "..");
    }

    private static void WriteSettingsDocument(string path, AppSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var doc = new SettingsExportDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            AppVersion = GetAppVersion(),
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Settings = settings,
        };

        var json = JsonSerializer.Serialize(doc, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private static string CreateRollbackBackup(string targetSettingsFile)
    {
        var backup = $"{targetSettingsFile}.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
        File.Copy(targetSettingsFile, backup, overwrite: true);
        return backup;
    }

    private static void TryRollback(string targetSettingsFile, string? backupPath, bool hadExisting)
    {
        try
        {
            if (backupPath != null && File.Exists(backupPath))
            {
                File.Copy(backupPath, targetSettingsFile, overwrite: true);
            }
            else if (!hadExisting && File.Exists(targetSettingsFile))
            {
                File.Delete(targetSettingsFile);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings rollback failed: {ex.Message}");
        }
    }

    private static string GetAppVersion()
        => (typeof(AppSettings).Assembly.GetName().Version ?? new Version(0, 9, 0)).ToString(3);
}

public sealed class SettingsExportDocument
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = AppSettings.SchemaId;

    public int SchemaVersion { get; set; } = AppSettings.CurrentSchemaVersion;
    public string AppVersion { get; set; } = "";
    public DateTimeOffset ExportedAtUtc { get; set; }
    public AppSettings Settings { get; set; } = new();
}

public sealed record SettingsImportIssue(string Severity, string Field, string Message)
{
    public static SettingsImportIssue Error(string field, string message) => new("Error", field, message);
    public static SettingsImportIssue Warning(string field, string message) => new("Warning", field, message);
}

public sealed record SettingsImportPreview(
    int SchemaVersion,
    string SchemaDisplay,
    string AppVersion,
    DateTimeOffset? ExportedAtUtc,
    bool ExpertMode,
    int ExcludedPathCount,
    int CookieWhitelistCount,
    int ProgramNoteCount,
    int MinAgeDaysJunk,
    int MinAgeDaysEvidence,
    int RetentionDaysLogs,
    int RetentionDaysActivity,
    int RetentionDaysDeletionManifests,
    bool ScrubSensitivePathsInReports)
{
    public string ToRedactedSummary()
        => $"{SchemaDisplay}; expert mode {(ExpertMode ? "on" : "off")}; " +
           $"{ExcludedPathCount} excluded path(s); {CookieWhitelistCount} cookie domain(s); " +
           $"{ProgramNoteCount} program note(s); junk/evidence age {MinAgeDaysJunk}/{MinAgeDaysEvidence}d; " +
           $"retention logs/activity/manifests {FormatRetention(RetentionDaysLogs)}/" +
           $"{FormatRetention(RetentionDaysActivity)}/{FormatRetention(RetentionDaysDeletionManifests)}; " +
           $"path scrub {(ScrubSensitivePathsInReports ? "on" : "off")}";

    private static string FormatRetention(int days) => days <= 0 ? "forever" : $"{days}d";
}

public sealed record SettingsImportPlan(
    AppSettings Settings,
    SettingsImportPreview Preview,
    int SchemaVersion,
    string AppVersion,
    DateTimeOffset? ExportedAtUtc,
    string SourcePath,
    IReadOnlyList<SettingsImportIssue> Issues)
{
    public bool IsValid => Issues.All(i => !i.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
    public string ErrorSummary => string.Join("; ", Issues
        .Where(i => i.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
        .Select(i => $"{i.Field}: {i.Message}"));
}

public sealed record SettingsImportOutcome(SettingsImportPlan Plan, string? BackupPath)
{
    public SettingsImportPreview Preview => Plan.Preview;
}
