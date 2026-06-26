using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

public record DeletionEntry(
    string Path,
    string Type,
    long SizeBytes,
    DateTime TimestampUtc,
    string Operation);

public record ManifestSummary(
    string FilePath,
    DateTime Date,
    int EntryCount,
    long TotalBytes);

public record RestoreResult(
    int RegistryRestored,
    int FilesRecoverable,
    int Unrecoverable,
    List<string> Details);

public static class DeletionManifest
{
    private static readonly object _lock = new();

    public static string CurrentManifestPath =>
        System.IO.Path.Combine(DataPaths.Logs, $"deletions-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    public static void Record(string path, string type, long sizeBytes, string operation)
    {
        try
        {
            var entry = new DeletionEntry(path, type, sizeBytes, DateTime.UtcNow, operation);
            var json = JsonSerializer.Serialize(entry);
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CurrentManifestPath)!);
                File.AppendAllText(CurrentManifestPath, json + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to write deletion manifest: {ex.Message}"); }
    }

    public static void RecordFile(string path, string operation)
    {
        long size = 0;
        try { if (File.Exists(path)) size = new FileInfo(path).Length; } catch { }
        Record(path, "file", size, operation);
    }

    public static void RecordDirectory(string path, string operation)
    {
        Record(path, "directory", 0, operation);
    }

    public static List<ManifestSummary> ListManifests()
    {
        var results = new List<ManifestSummary>();
        try
        {
            var dir = DataPaths.Logs;
            if (!Directory.Exists(dir)) return results;
            foreach (var file in Directory.GetFiles(dir, "deletions-*.jsonl").OrderByDescending(f => f))
            {
                var entries = LoadEntriesFromFile(file);
                if (entries.Count == 0) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                var dateStr = name.Replace("deletions-", "");
                if (!DateTime.TryParse(dateStr, out var date)) continue;
                results.Add(new ManifestSummary(file, date, entries.Count, entries.Sum(e => e.SizeBytes)));
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to list manifests: {ex.Message}"); }
        return results;
    }

    public static List<DeletionEntry> LoadManifest(DateTime date)
    {
        var path = System.IO.Path.Combine(DataPaths.Logs, $"deletions-{date:yyyy-MM-dd}.jsonl");
        return File.Exists(path) ? LoadEntriesFromFile(path) : new();
    }

    public static RestoreResult RestoreFromManifest(DateTime date, bool dryRun = false)
    {
        var entries = LoadManifest(date);
        int regRestored = 0, recoverable = 0, unrecoverable = 0;
        var details = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.Type == "registry" || entry.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.StartsWith("HKCR", StringComparison.OrdinalIgnoreCase))
            {
                var backupFile = FindMatchingBackup(entry.Path, entry.TimestampUtc);
                if (backupFile != null)
                {
                    if (!dryRun)
                    {
                        try
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "reg.exe",
                                Arguments = $"import \"{backupFile}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardError = true,
                            };
                            using var p = System.Diagnostics.Process.Start(psi);
                            p?.WaitForExit(15000);
                            if (p?.ExitCode == 0) regRestored++;
                            else details.Add($"reg import failed for {backupFile}: exit {p?.ExitCode}");
                        }
                        catch (Exception ex) { details.Add($"reg import failed: {ex.Message}"); }
                    }
                    else
                    {
                        regRestored++;
                        details.Add($"[dry-run] Would restore registry from {backupFile}");
                    }
                }
                else
                {
                    unrecoverable++;
                    details.Add($"No backup found for registry path: {entry.Path}");
                }
            }
            else if (entry.Operation.Contains("secure", StringComparison.OrdinalIgnoreCase) ||
                     entry.Operation.Contains("wipe", StringComparison.OrdinalIgnoreCase))
            {
                unrecoverable++;
                details.Add($"Secure-deleted (unrecoverable): {entry.Path}");
            }
            else
            {
                if (File.Exists(entry.Path) || Directory.Exists(entry.Path))
                {
                    details.Add($"Already exists (skipped): {entry.Path}");
                }
                else
                {
                    recoverable++;
                    details.Add($"Check Recycle Bin: {entry.Path}");
                }
            }
        }

        return new RestoreResult(regRestored, recoverable, unrecoverable, details);
    }

    private static string? FindMatchingBackup(string registryPath, DateTime deleteTime)
    {
        try
        {
            var backupDir = DataPaths.Backups;
            if (!Directory.Exists(backupDir)) return null;
            var backups = Directory.GetFiles(backupDir, "*.reg")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTimeUtc <= deleteTime.AddMinutes(5) &&
                            f.LastWriteTimeUtc >= deleteTime.AddHours(-1))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            return backups.FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private static List<DeletionEntry> LoadEntriesFromFile(string filePath)
    {
        var entries = new List<DeletionEntry>();
        try
        {
            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<DeletionEntry>(line);
                    if (e != null) entries.Add(e);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to load manifest {filePath}: {ex.Message}"); }
        return entries;
    }
}
