using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

public record HealthScore(string Category, int Score, string Summary, string Action, string? CommandTarget = null);

public record HealthReport(
    int OverallScore,
    string Grade,
    List<HealthScore> Categories,
    HealthTrend Trend = HealthTrend.Unknown);

public enum HealthTrend { Improved, Worsened, Stable, Unknown }

public record HealthHistoryEntry(DateTime TimestampUtc, int OverallScore, string Grade);

public static class HealthHistory
{
    private static readonly object _lock = new();
    private static string FilePath => Path.Combine(DataPaths.Logs, "health-history.jsonl");
    private const int MaxEntries = 90;

    public static void Record(HealthReport report)
    {
        try
        {
            var entry = new HealthHistoryEntry(DateTime.UtcNow, report.OverallScore, report.Grade);
            var json = JsonSerializer.Serialize(entry);
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, json + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    public static List<HealthHistoryEntry> LoadRecent(int max = 30)
    {
        try
        {
            string[] lines;
            lock (_lock)
            {
                if (!File.Exists(FilePath)) return new();
                lines = File.ReadAllLines(FilePath);
            }
            var entries = new List<HealthHistoryEntry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<HealthHistoryEntry>(line);
                    if (e != null) entries.Add(e);
                }
                catch { }
            }
            return entries.OrderByDescending(e => e.TimestampUtc).Take(max).ToList();
        }
        catch { return new(); }
    }

    public static HealthTrend CompareTrend(int currentScore)
    {
        var history = LoadRecent(5);
        if (history.Count < 2) return HealthTrend.Unknown;
        var previous = history.Skip(1).First().OverallScore;
        if (currentScore > previous) return HealthTrend.Improved;
        if (currentScore < previous) return HealthTrend.Worsened;
        return HealthTrend.Stable;
    }

    public static void Prune()
    {
        try
        {
            lock (_lock)
            {
                if (!File.Exists(FilePath)) return;
                var lines = File.ReadAllLines(FilePath);
                if (lines.Length <= MaxEntries) return;
                var keep = lines.Skip(lines.Length - MaxEntries).ToArray();
                File.WriteAllLines(FilePath, keep, Encoding.UTF8);
            }
        }
        catch { }
    }
}

public static class HealthScorer
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(30);

    public static async Task<HealthReport> AssessAsync(CancellationToken ct = default)
    {
        var cats = new List<HealthScore>
        {
            await RunWithTimeoutAsync(AssessJunk, "Junk Files", ct),
            await RunWithTimeoutAsync(AssessPrivacy, "Privacy", ct),
            await RunWithTimeoutAsync(AssessStartup, "Startup Impact", ct),
            AssessDisk(),
        };

        var overall = cats.Count > 0 ? (int)Math.Round(cats.Average(c => c.Score)) : 100;
        var grade = GradeFromScore(overall);
        var trend = HealthHistory.CompareTrend(overall);
        var report = new HealthReport(overall, grade, cats, trend);
        HealthHistory.Record(report);
        return report;
    }

    public static HealthReport Assess()
    {
        var cats = new List<HealthScore>
        {
            AssessJunk(),
            AssessPrivacy(),
            AssessStartup(),
            AssessDisk(),
        };

        var overall = cats.Count > 0 ? (int)Math.Round(cats.Average(c => c.Score)) : 100;
        var grade = GradeFromScore(overall);
        var trend = HealthHistory.CompareTrend(overall);
        var report = new HealthReport(overall, grade, cats, trend);
        HealthHistory.Record(report);
        return report;
    }

    private static string GradeFromScore(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F",
    };

    private static async Task<HealthScore> RunWithTimeoutAsync(Func<HealthScore> scanner, string category, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ScanTimeout);
            return await Task.Run(scanner, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Health {category} assessment timed out after {ScanTimeout.TotalSeconds}s");
            return new HealthScore(category, 50, "Scan timed out", $"Try again");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health {category} assessment: {ex.Message}");
            return new HealthScore(category, 50, "Could not assess", $"Try again");
        }
    }

    private static HealthScore AssessJunk()
    {
        try
        {
            var junk = FileSystem.JunkFilesCleaner.ScanForJunk();
            var totalBytes = junk.Sum(c => c.TotalSize);
            var totalMb = totalBytes / (1024.0 * 1024);

            var score = totalMb switch
            {
                < 50 => 100,
                < 200 => 85,
                < 500 => 70,
                < 1000 => 50,
                < 2000 => 30,
                _ => 10,
            };

            return new HealthScore("Junk Files", score,
                $"{totalMb:F0} MB across {junk.Sum(c => c.Files.Count)} items",
                score < 80 ? "Run Junk Cleaner" : "Clean",
                CommandTarget: "JunkCleaner");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health junk assessment: {ex.Message}");
            return new HealthScore("Junk Files", 50, "Could not assess", "Run Junk Cleaner", CommandTarget: "JunkCleaner");
        }
    }

    private static HealthScore AssessPrivacy()
    {
        try
        {
            var traces = Privacy.EvidenceRemover.ScanAllTraces();
            var totalItems = traces.Sum(c => c.ItemCount);
            var totalBytes = traces.Sum(c => c.TotalSize);
            var totalMb = totalBytes / (1024.0 * 1024);

            var score = totalItems switch
            {
                < 10 => 100,
                < 50 => 85,
                < 200 => 70,
                < 500 => 50,
                _ => 30,
            };

            return new HealthScore("Privacy", score,
                $"{totalItems} traces ({totalMb:F0} MB)",
                score < 80 ? "Run Evidence Remover" : "Clean",
                CommandTarget: "EvidenceRemover");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health privacy assessment: {ex.Message}");
            return new HealthScore("Privacy", 50, "Could not assess", "Run Evidence Remover", CommandTarget: "EvidenceRemover");
        }
    }

    private static HealthScore AssessStartup()
    {
        try
        {
            var autoruns = Startup.AutorunScanner.GetAllAutoruns();
            var enabled = autoruns.Count(a => a.IsEnabled && a.Type is Startup.AutorunType.RegistryRun or Startup.AutorunType.RegistryRunOnce);

            var score = enabled switch
            {
                <= 5 => 100,
                <= 10 => 85,
                <= 20 => 70,
                <= 30 => 50,
                _ => 30,
            };

            return new HealthScore("Startup Impact", score,
                $"{enabled} enabled autorun entries",
                score < 80 ? "Review Autorun Manager" : "Optimized",
                CommandTarget: "AutorunManager");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health startup assessment: {ex.Message}");
            return new HealthScore("Startup Impact", 50, "Could not assess", "Check Autorun Manager", CommandTarget: "AutorunManager");
        }
    }

    private static HealthScore AssessDisk()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
            var driveInfo = new DriveInfo(systemDrive);
            var freePercent = 100.0 * driveInfo.AvailableFreeSpace / driveInfo.TotalSize;

            var score = freePercent switch
            {
                >= 30 => 100,
                >= 20 => 85,
                >= 15 => 70,
                >= 10 => 50,
                >= 5 => 30,
                _ => 10,
            };

            var freeGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            return new HealthScore("Disk Space", score,
                $"{freeGb:F1} GB free ({freePercent:F0}%)",
                score < 80 ? "Free up disk space" : "Healthy",
                CommandTarget: "DiskAnalyzer");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health disk assessment: {ex.Message}");
            return new HealthScore("Disk Space", 50, "Could not assess", "Check Disk Analyzer", CommandTarget: "DiskAnalyzer");
        }
    }
}
