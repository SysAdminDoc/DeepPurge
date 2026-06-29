using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using Xunit;

namespace DeepPurge.Tests;

public class PrivacyMaintenanceTests
{
    [Fact]
    public void Apply_prunes_expired_logs_activity_and_manifests()
    {
        var dir = CreateTempDir();
        var now = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            var oldLog = Path.Combine(dir, "deeppurge.log");
            var recentLog = Path.Combine(dir, "deeppurge.log.1");
            File.WriteAllText(oldLog, @"old C:\Users\Alice\AppData\Local\Temp\a.tmp");
            File.WriteAllText(recentLog, @"recent C:\Users\Alice\Downloads\b.tmp");
            File.SetLastWriteTimeUtc(oldLog, now.AddDays(-20));
            File.SetLastWriteTimeUtc(recentLog, now.AddDays(-1));

            var oldManifest = Path.Combine(dir, "deletions-2026-01-01.jsonl");
            var recentManifest = Path.Combine(dir, "deletions-2026-06-28.jsonl");
            File.WriteAllText(oldManifest, "{}");
            File.WriteAllText(recentManifest, "{}");

            var activityPath = Path.Combine(dir, "activity.jsonl");
            File.WriteAllLines(activityPath, new[]
            {
                JsonSerializer.Serialize(new ActivityEntry(now.AddDays(-30), "clean", @"Deleted C:\Users\Alice\old.tmp", 1, 1, false)),
                JsonSerializer.Serialize(new ActivityEntry(now.AddDays(-1), "clean", @"Deleted C:\Users\Alice\new.tmp", 1, 1, false)),
            });

            var settings = new AppSettings
            {
                RetentionDaysLogs = 7,
                RetentionDaysActivity = 7,
                RetentionDaysDeletionManifests = 7,
                ScrubSensitivePathsInReports = true,
            };

            var dryRun = PrivacyMaintenance.Apply(settings, dryRun: true, logsDirectory: dir, nowUtc: now);
            Assert.Equal(2, dryRun.FilesDeleted);
            Assert.Equal(1, dryRun.ActivityEntriesDeleted);
            Assert.True(File.Exists(oldLog));
            Assert.True(File.Exists(oldManifest));

            var result = PrivacyMaintenance.Apply(settings, logsDirectory: dir, nowUtc: now);

            Assert.Equal(2, result.FilesDeleted);
            Assert.Equal(1, result.LogFilesDeleted);
            Assert.Equal(1, result.DeletionManifestsDeleted);
            Assert.Equal(1, result.ActivityEntriesDeleted);
            Assert.False(File.Exists(oldLog));
            Assert.False(File.Exists(oldManifest));
            Assert.True(File.Exists(recentLog));
            Assert.True(File.Exists(recentManifest));

            var retainedActivity = File.ReadAllText(activityPath);
            Assert.DoesNotContain("old.tmp", retainedActivity);
            Assert.DoesNotContain("Users", retainedActivity);
            Assert.DoesNotContain("Alice", retainedActivity);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Zero_retention_keeps_files_until_user_changes_policy()
    {
        var dir = CreateTempDir();
        var now = new DateTime(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            var oldLog = Path.Combine(dir, "deeppurge.log");
            var oldManifest = Path.Combine(dir, "deletions-2026-01-01.jsonl");
            File.WriteAllText(oldLog, "old");
            File.WriteAllText(oldManifest, "{}");
            File.SetLastWriteTimeUtc(oldLog, now.AddYears(-1));

            var settings = new AppSettings
            {
                RetentionDaysLogs = 0,
                RetentionDaysActivity = 0,
                RetentionDaysDeletionManifests = 0,
            };

            var result = PrivacyMaintenance.Apply(settings, logsDirectory: dir, nowUtc: now);

            Assert.Equal(0, result.FilesDeleted);
            Assert.True(File.Exists(oldLog));
            Assert.True(File.Exists(oldManifest));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public void Redactor_masks_absolute_local_paths()
    {
        var redacted = PrivacyRedactor.RedactPaths(@"See C:\Users\Alice\AppData\Local\DeepPurge\Logs\activity.jsonl");

        Assert.DoesNotContain(@"C:\Users\Alice", redacted);
        Assert.Contains("<local-path>", redacted);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeepPurgePrivacyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
