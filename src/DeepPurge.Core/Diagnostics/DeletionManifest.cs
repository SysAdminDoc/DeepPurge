using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

public record DeletionEntry(
    string Path,
    string Type,
    long SizeBytes,
    DateTime TimestampUtc,
    string Operation);

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
        catch { }
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
}
