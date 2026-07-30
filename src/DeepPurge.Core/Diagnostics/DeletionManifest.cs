using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Execution;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Diagnostics;

public record DeletionEntry(
    string Path,
    string Type,
    long SizeBytes,
    DateTime TimestampUtc,
    string Operation,
    int SchemaVersion = 1,
    string? OperationId = null,
    string Outcome = "Succeeded",
    string? BackupPath = null,
    string? BackupSha256 = null,
    string? RegistryHive = null,
    string? RegistrySubKey = null,
    string? RegistryValueName = null,
    string? RegistryView = null,
    string? ObjectIdentity = null,
    string? BackupOwnerSid = null,
    string? BackupDaclSddl = null,
    bool BackupAclTrusted = false);

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
    private static readonly AsyncLocal<string?> ManifestPathOverride = new();

    public static string CurrentManifestPath =>
        ManifestPathOverride.Value ??
        System.IO.Path.Combine(DataPaths.Logs, $"deletions-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    internal static IDisposable UseManifestPathForTests(string path)
    {
        var previous = ManifestPathOverride.Value;
        ManifestPathOverride.Value = System.IO.Path.GetFullPath(path);
        return new ManifestPathScope(previous);
    }

    public static void Record(string path, string type, long sizeBytes, string operation)
    {
        var entry = new DeletionEntry(path, type, sizeBytes, DateTime.UtcNow, operation);
        TryAppend(entry);
    }

    public static void RecordFile(string path, string operation)
    {
        long size = 0;
        try { if (File.Exists(path)) size = new FileInfo(path).Length; }
        catch (Exception ex) { Log.Warn($"Failed to get file size for manifest: {ex.Message}"); }
        Record(path, "file", size, operation);
    }

    public static void RecordDirectory(string path, string operation)
    {
        Record(path, "directory", 0, operation);
    }

    public static void RecordRegistry(string path, string operation)
    {
        Record(path, "registry", 0, operation);
    }

    internal static bool RecordRegistryTransaction(
        string path,
        string operation,
        RegistryBackupArtifact artifact,
        string outcome)
    {
        var entry = new DeletionEntry(
            path,
            "registry",
            0,
            DateTime.UtcNow,
            operation,
            SchemaVersion: 2,
            OperationId: artifact.OperationId,
            Outcome: outcome,
            BackupPath: artifact.BackupPath,
            BackupSha256: artifact.BackupSha256,
            RegistryHive: artifact.Hive,
            RegistrySubKey: artifact.SubKey,
            RegistryValueName: artifact.ValueName,
            RegistryView: artifact.RegistryView,
            ObjectIdentity: artifact.ObjectIdentity,
            BackupOwnerSid: artifact.BackupOwnerSid,
            BackupDaclSddl: artifact.BackupDaclSddl,
            BackupAclTrusted: artifact.BackupAclTrusted);
        return TryAppend(entry);
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
        var path = ManifestPathOverride.Value ??
            System.IO.Path.Combine(DataPaths.Logs, $"deletions-{date:yyyy-MM-dd}.jsonl");
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
                var store = RegistryBackupStore.Production;
                if (store.TryValidateForRestore(entry, out var backupFile, out var validationReason))
                {
                    if (!dryRun)
                    {
                        try
                        {
                            var registryTool = Path.Combine(Environment.SystemDirectory, "reg.exe");
                            var process = ExternalProcessRunner.Run(new ExternalProcessCommand(registryTool)
                            {
                                Arguments = new[] { "import", backupFile },
                                Timeout = TimeSpan.FromSeconds(15),
                                RedactedArgumentIndexes = new HashSet<int> { 1 },
                                RedactAbsolutePaths = true,
                            });
                            if (process.Success) regRestored++;
                            else details.Add($"reg import failed for backup: {process.Status}");
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
                    details.Add($"Registry restore blocked for {entry.Path}: {validationReason}");
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

    private static List<DeletionEntry> LoadEntriesFromFile(string filePath)
    {
        var entries = new List<DeletionEntry>();
        var operationIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = reader.ReadToEnd().Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<DeletionEntry>(line);
                    if (e == null) continue;
                    if (!string.IsNullOrWhiteSpace(e.OperationId) &&
                        operationIndexes.TryGetValue(e.OperationId, out var existingIndex))
                    {
                        entries[existingIndex] = e;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(e.OperationId))
                            operationIndexes[e.OperationId] = entries.Count;
                        entries.Add(e);
                    }
                }
                catch (Exception ex) { Log.Warn($"Malformed manifest line in {filePath}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"Failed to load manifest {filePath}: {ex.Message}"); }
        return entries;
    }

    private static bool TryAppend(DeletionEntry entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry);
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CurrentManifestPath)!);
                using var stream = new FileStream(
                    CurrentManifestPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.WriteLine(json);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to write deletion manifest: {ex.Message}");
            return false;
        }
    }

    private sealed class ManifestPathScope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            ManifestPathOverride.Value = previous;
            _disposed = true;
        }
    }
}
