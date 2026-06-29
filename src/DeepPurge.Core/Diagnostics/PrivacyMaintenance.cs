using System.Text.Json;
using System.Text.RegularExpressions;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

public sealed record PrivacyMaintenanceResult(
    int LogFilesDeleted,
    int ActivityEntriesDeleted,
    int DeletionManifestsDeleted,
    int FilesScrubbed,
    long BytesDeleted,
    bool DryRun,
    List<string> Details)
{
    public int FilesDeleted => LogFilesDeleted + DeletionManifestsDeleted;
}

public static class PrivacyRedactor
{
    private static readonly Regex DrivePathRegex = new(
        @"(?i)\b[A-Z]:\\(?:[^\s""'<>|]+\\?)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RedactPaths(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var result = value;
        foreach (var (path, token) in SensitiveRoots())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            result = result.Replace(path, token, StringComparison.OrdinalIgnoreCase);
        }

        return DrivePathRegex.Replace(result, "<local-path>");
    }

    private static IEnumerable<(string Path, string Token)> SensitiveRoots()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        yield return (UserIdentity.RealLocalAppData, "%LOCALAPPDATA%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        yield return (Path.GetTempPath().TrimEnd('\\'), "%TEMP%");
        yield return (DataPaths.Root, "%DEEPPURGE_DATA%");
    }
}

public static class PrivacyMaintenance
{
    public static PrivacyMaintenanceResult Apply(
        AppSettings settings,
        bool dryRun = false,
        string? logsDirectory = null,
        DateTime? nowUtc = null)
    {
        var details = new List<string>();
        var dir = logsDirectory ?? DataPaths.Logs;
        var now = nowUtc ?? DateTime.UtcNow;
        long bytesDeleted = 0;
        int logFilesDeleted = 0;
        int activityEntriesDeleted = 0;
        int deletionManifestsDeleted = 0;
        int filesScrubbed = 0;

        Directory.CreateDirectory(dir);

        PruneLogFiles(settings.RetentionDaysLogs, dir, now, dryRun, details, ref logFilesDeleted, ref bytesDeleted);
        PruneActivityEntries(settings.RetentionDaysActivity, dir, now, dryRun, details, ref activityEntriesDeleted);
        PruneDeletionManifests(settings.RetentionDaysDeletionManifests, dir, now, dryRun, details, ref deletionManifestsDeleted, ref bytesDeleted);

        if (settings.ScrubSensitivePathsInReports)
        {
            filesScrubbed += ScrubTextFiles(Directory.EnumerateFiles(dir, "deeppurge.log*"), dryRun, details);
            filesScrubbed += ScrubActivityLog(Path.Combine(dir, "activity.jsonl"), dryRun, details);
        }

        return new PrivacyMaintenanceResult(
            logFilesDeleted,
            activityEntriesDeleted,
            deletionManifestsDeleted,
            filesScrubbed,
            bytesDeleted,
            dryRun,
            details);
    }

    private static void PruneLogFiles(
        int retentionDays,
        string dir,
        DateTime now,
        bool dryRun,
        List<string> details,
        ref int deleted,
        ref long bytesDeleted)
    {
        if (retentionDays <= 0) return;
        var cutoff = now.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(dir, "deeppurge.log*"))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc >= cutoff) continue;
            deleted++;
            bytesDeleted += info.Length;
            details.Add($"{(dryRun ? "Would delete" : "Deleted")} log file: {info.Name}");
            if (!dryRun) TryDelete(file);
        }
    }

    private static void PruneActivityEntries(
        int retentionDays,
        string dir,
        DateTime now,
        bool dryRun,
        List<string> details,
        ref int removed)
    {
        if (retentionDays <= 0) return;
        var path = Path.Combine(dir, "activity.jsonl");
        if (!File.Exists(path)) return;

        var cutoff = now.AddDays(-retentionDays);
        var keep = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ActivityEntry>(line);
                if (entry != null && entry.TimestampUtc < cutoff)
                {
                    removed++;
                    continue;
                }
            }
            catch
            {
                // Keep malformed lines; diagnostics should preserve evidence
                // unless a valid timestamp proves the entry expired.
            }
            keep.Add(line);
        }

        if (removed > 0)
        {
            details.Add($"{(dryRun ? "Would remove" : "Removed")} {removed} expired activity entr{(removed == 1 ? "y" : "ies")}");
            if (!dryRun) File.WriteAllLines(path, keep, System.Text.Encoding.UTF8);
        }
    }

    private static void PruneDeletionManifests(
        int retentionDays,
        string dir,
        DateTime now,
        bool dryRun,
        List<string> details,
        ref int deleted,
        ref long bytesDeleted)
    {
        if (retentionDays <= 0) return;
        var cutoff = now.AddDays(-retentionDays).Date;

        foreach (var file in Directory.EnumerateFiles(dir, "deletions-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace("deletions-", "");
            if (!DateTime.TryParse(name, out var date)) continue;
            if (date.Date >= cutoff) continue;

            var info = new FileInfo(file);
            deleted++;
            bytesDeleted += info.Length;
            details.Add($"{(dryRun ? "Would delete" : "Deleted")} deletion manifest: {info.Name}");
            if (!dryRun) TryDelete(file);
        }
    }

    private static int ScrubTextFiles(IEnumerable<string> files, bool dryRun, List<string> details)
    {
        int scrubbed = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            var original = File.ReadAllText(file);
            var redacted = PrivacyRedactor.RedactPaths(original);
            if (redacted == original) continue;
            scrubbed++;
            details.Add($"{(dryRun ? "Would scrub" : "Scrubbed")} path details in {Path.GetFileName(file)}");
            if (!dryRun) File.WriteAllText(file, redacted, System.Text.Encoding.UTF8);
        }
        return scrubbed;
    }

    private static int ScrubActivityLog(string path, bool dryRun, List<string> details)
    {
        if (!File.Exists(path)) return 0;

        var changed = false;
        var output = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ActivityEntry>(line);
                if (entry == null) continue;
                var redacted = PrivacyRedactor.RedactPaths(entry.Summary);
                if (redacted != entry.Summary) changed = true;
                output.Add(JsonSerializer.Serialize(entry with { Summary = redacted }));
            }
            catch
            {
                var redacted = PrivacyRedactor.RedactPaths(line);
                if (redacted != line) changed = true;
                output.Add(redacted);
            }
        }

        if (!changed) return 0;

        details.Add($"{(dryRun ? "Would scrub" : "Scrubbed")} path details in activity.jsonl");
        if (!dryRun) File.WriteAllLines(path, output, System.Text.Encoding.UTF8);
        return 1;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) { Log.Warn($"Privacy retention delete failed for '{file}': {ex.Message}"); }
    }
}
