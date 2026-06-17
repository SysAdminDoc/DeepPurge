using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

public record ActivityEntry(
    DateTime TimestampUtc,
    string Operation,
    string Summary,
    long BytesFreed,
    int ItemCount,
    bool DryRun);

public static class ActivityLog
{
    private static readonly object _lock = new();
    private static string FilePath => Path.Combine(DataPaths.Logs, "activity.jsonl");
    private const int MaxEntries = 500;

    public static void Record(string operation, string summary, long bytesFreed = 0, int itemCount = 0, bool dryRun = false)
    {
        try
        {
            var entry = new ActivityEntry(DateTime.UtcNow, operation, summary, bytesFreed, itemCount, dryRun);
            var json = JsonSerializer.Serialize(entry);
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, json + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch { }
    }

    public static List<ActivityEntry> LoadRecent(int max = 100)
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var lines = File.ReadAllLines(FilePath);
            var entries = new List<ActivityEntry>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<ActivityEntry>(line);
                    if (e != null) entries.Add(e);
                }
                catch { }
            }
            return entries.OrderByDescending(e => e.TimestampUtc).Take(max).ToList();
        }
        catch { return new(); }
    }

    public static void Prune()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var lines = File.ReadAllLines(FilePath);
            if (lines.Length <= MaxEntries) return;
            var keep = lines.Skip(lines.Length - MaxEntries).ToArray();
            File.WriteAllLines(FilePath, keep, System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
