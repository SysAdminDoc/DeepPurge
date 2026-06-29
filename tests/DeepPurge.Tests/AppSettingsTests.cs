using System.Text.Json;
using DeepPurge.Core.App;
using Xunit;

namespace DeepPurge.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Current_returns_singleton()
    {
        var a = AppSettings.Current;
        var b = AppSettings.Current;
        Assert.Same(a, b);
    }

    [Fact]
    public void ExcludedPaths_defaults_to_empty()
    {
        Assert.NotNull(AppSettings.Current.ExcludedPaths);
    }

    [Fact]
    public void Privacy_retention_defaults_are_initialized()
    {
        var settings = new AppSettings();

        Assert.Equal(30, settings.RetentionDaysLogs);
        Assert.Equal(90, settings.RetentionDaysActivity);
        Assert.Equal(90, settings.RetentionDaysDeletionManifests);
        Assert.False(settings.ScrubSensitivePathsInReports);
    }

    [Fact]
    public void Save_does_not_throw()
    {
        var ex = Record.Exception(() => AppSettings.Current.Save());
        Assert.Null(ex);
    }

    [Fact]
    public void ExportTo_writes_versioned_document_and_redacted_preview()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "settings-export.json");
            var settings = new AppSettings
            {
                ExpertMode = true,
                ExcludedPaths = new() { @"C:\Users\Alice\AppData\Secret" },
                CookieWhitelist = new() { "Example.COM" },
                ProgramNotes = new() { ["Secret Tool"] = @"contains C:\Users\Alice" },
                MinAgeDaysJunk = 7,
                MinAgeDaysEvidence = 14,
                RetentionDaysLogs = 30,
                RetentionDaysActivity = 60,
                RetentionDaysDeletionManifests = 90,
                ScrubSensitivePathsInReports = true,
            };

            var preview = settings.ExportTo(path);
            var summary = preview.ToRedactedSummary();

            Assert.DoesNotContain("Alice", summary);
            Assert.DoesNotContain("Secret Tool", summary);
            Assert.Contains("1 excluded path", summary);
            Assert.Contains("1 cookie domain", summary);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(AppSettings.CurrentSchemaVersion, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(AppSettings.SchemaId, doc.RootElement.GetProperty("$schema").GetString());
            Assert.Equal("example.com", doc.RootElement.GetProperty("Settings").GetProperty("CookieWhitelist")[0].GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImportFrom_accepts_legacy_raw_settings_and_reports_migration()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "legacy-settings.json");
            var legacy = new AppSettings
            {
                ExcludedPaths = new() { @"C:\Temp\Keep" },
                CookieWhitelist = new() { "github.com" },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(legacy));

            var plan = AppSettings.PreviewImportFromFile(path);
            var imported = AppSettings.ImportFrom(path);

            Assert.True(plan.IsValid);
            Assert.Equal(0, plan.SchemaVersion);
            Assert.Contains(plan.Issues, i => i.Severity == "Warning" && i.Field == "SchemaVersion");
            Assert.Equal(@"C:\Temp\Keep", imported.ExcludedPaths.Single());
            Assert.Equal("github.com", imported.CookieWhitelist.Single());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImportFrom_rejects_future_schema_versions()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "future-settings.json");
            File.WriteAllText(path, """
{
  "SchemaVersion": 99,
  "Settings": {
    "ExpertMode": true
  }
}
""");

            var plan = AppSettings.PreviewImportFromFile(path);

            Assert.False(plan.IsValid);
            Assert.Contains(plan.Issues, i => i.Severity == "Error" && i.Field == "SchemaVersion");
            Assert.Throws<InvalidOperationException>(() => AppSettings.ImportFrom(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_preview_reports_validation_errors_without_leaking_values()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "invalid-settings.json");
            File.WriteAllText(path, """
{
  "SchemaVersion": 1,
  "Settings": {
    "MinAgeDaysJunk": -1,
    "ExcludedPaths": [ "C:\\Users\\Alice\\..\\Windows" ],
    "CookieWhitelist": [ "C:\\Users\\Alice\\Cookies" ]
  }
}
""");

            var plan = AppSettings.PreviewImportFromFile(path);
            var summary = plan.Preview.ToRedactedSummary();

            Assert.False(plan.IsValid);
            Assert.Contains(plan.Issues, i => i.Field == "MinAgeDaysJunk");
            Assert.Contains(plan.Issues, i => i.Field == "ExcludedPaths");
            Assert.Contains(plan.Issues, i => i.Field == "CookieWhitelist");
            Assert.DoesNotContain("Alice", summary);
            Assert.DoesNotContain("Windows", summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImportAndApply_writes_target_and_keeps_rollback_backup()
    {
        var dir = NewTempDir();
        try
        {
            var target = Path.Combine(dir, "settings.json");
            var import = Path.Combine(dir, "import.json");
            new AppSettings { ExcludedPaths = new() { @"C:\Old" } }.ExportTo(target);
            new AppSettings
            {
                ExpertMode = true,
                ExcludedPaths = new() { @"C:\New" },
                CookieWhitelist = new() { "example.com" },
            }.ExportTo(import);

            var outcome = AppSettings.ImportAndApply(import, target, applyToCurrent: false);
            var applied = AppSettings.ImportFrom(target);
            var backup = AppSettings.ImportFrom(outcome.BackupPath!);

            Assert.True(applied.ExpertMode);
            Assert.Equal(@"C:\New", applied.ExcludedPaths.Single());
            Assert.Equal(@"C:\Old", backup.ExcludedPaths.Single());
            Assert.True(File.Exists(outcome.BackupPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeepPurgeSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
