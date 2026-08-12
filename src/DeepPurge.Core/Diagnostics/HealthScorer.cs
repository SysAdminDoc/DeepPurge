using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

public record HealthScore(string Category, int Score, string Summary, string Action, string? CommandTarget = null);

public record HealthReport(
    int OverallScore,
    string Grade,
    List<HealthScore> Categories,
    HealthTrend Trend = HealthTrend.Unknown,
    IReadOnlyList<ScanIssue>? FailedSources = null,
    IReadOnlyList<string>? Warnings = null,
    TimeSpan Duration = default,
    ScanCompletionStatus Status = ScanCompletionStatus.Clean,
    bool IsCancelled = false)
{
    public bool IsDegraded => Status != ScanCompletionStatus.Clean;

    public string StatusDisplay => Status switch
    {
        ScanCompletionStatus.Clean => "Clean",
        ScanCompletionStatus.Partial => "Partial",
        ScanCompletionStatus.Failed => "Failed",
        ScanCompletionStatus.TimedOut => "Timed out",
        ScanCompletionStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };
}

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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = new List<HealthCategoryResult>
        {
            await RunWithTimeoutAsync(AssessJunk, "Junk Files", ct),
            await RunWithTimeoutAsync(AssessPrivacy, "Privacy", ct),
            await RunWithTimeoutAsync(AssessStartup, "Startup Impact", ct),
            RunImmediate(AssessDisk, "Disk Space", ct),
        };
        return CompleteReport(outcomes, stopwatch.Elapsed, ct.IsCancellationRequested);
    }

    public static HealthReport Assess()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = new List<HealthCategoryResult>
        {
            RunImmediate(AssessJunk, "Junk Files"),
            RunImmediate(AssessPrivacy, "Privacy"),
            RunImmediate(AssessStartup, "Startup Impact"),
            RunImmediate(AssessDisk, "Disk Space"),
        };
        return CompleteReport(outcomes, stopwatch.Elapsed, isCancelled: false);
    }

    private static string GradeFromScore(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F",
    };

    private sealed record HealthCategoryResult(
        HealthScore Score,
        ScanIssue? Issue = null,
        bool TimedOut = false,
        bool Cancelled = false);

    private static async Task<HealthCategoryResult> RunWithTimeoutAsync(
        Func<HealthScore> scanner,
        string category,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ScanTimeout);
            return new HealthCategoryResult(await Task.Run(scanner, cts.Token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warn($"Health {category} assessment was cancelled");
            return new HealthCategoryResult(
                new HealthScore(category, 50, "Scan cancelled", "Try again"),
                new ScanIssue(category, "The health category scan was cancelled."),
                Cancelled: true);
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Health {category} assessment timed out after {ScanTimeout.TotalSeconds}s");
            return new HealthCategoryResult(
                new HealthScore(category, 50, "Scan timed out", "Try again"),
                new ScanIssue(category, $"The health category scan timed out after {ScanTimeout.TotalSeconds:F0} seconds."),
                TimedOut: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Health {category} assessment: {ex.Message}");
            return new HealthCategoryResult(
                new HealthScore(category, 50, "Could not assess", "Try again"),
                new ScanIssue(category, ex.Message, ex.GetType().Name));
        }
    }

    private static HealthCategoryResult RunImmediate(
        Func<HealthScore> scanner,
        string category,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return new HealthCategoryResult(
                new HealthScore(category, 50, "Scan cancelled", "Try again"),
                new ScanIssue(category, "The health category scan was cancelled."),
                Cancelled: true);

        try
        {
            return new HealthCategoryResult(scanner());
        }
        catch (Exception ex)
        {
            Log.Warn($"Health {category} assessment: {ex.Message}");
            return new HealthCategoryResult(
                new HealthScore(category, 50, "Could not assess", "Try again"),
                new ScanIssue(category, ex.Message, ex.GetType().Name));
        }
    }

    private static HealthReport CompleteReport(
        IReadOnlyList<HealthCategoryResult> outcomes,
        TimeSpan duration,
        bool isCancelled)
    {
        var categories = outcomes.Select(outcome => outcome.Score).ToList();
        var overall = categories.Count > 0 ? (int)Math.Round(categories.Average(c => c.Score)) : 100;
        var grade = GradeFromScore(overall);
        var trend = HealthHistory.CompareTrend(overall);
        var failures = outcomes
            .Where(outcome => outcome.Issue != null)
            .Select(outcome => outcome.Issue!)
            .ToList();
        var warnings = outcomes
            .Where(outcome => outcome.Score.Summary is "Could not assess" or "Scan timed out" or "Scan cancelled")
            .Select(outcome => $"{outcome.Score.Category}: {outcome.Score.Summary}")
            .ToList();
        var timedOut = outcomes.Any(outcome => outcome.TimedOut);
        var cancelled = isCancelled || outcomes.Any(outcome => outcome.Cancelled);
        var status = cancelled
            ? ScanCompletionStatus.Cancelled
            : timedOut
                ? ScanCompletionStatus.TimedOut
                : ScanResult<HealthScore>.Classify(categories.Count, failures, warnings);

        var report = new HealthReport(
            overall,
            grade,
            categories,
            trend,
            failures,
            warnings,
            duration,
            status,
            cancelled);
        HealthHistory.Record(report);
        ScanDiagnosticsLedger.Record(
            "health",
            ScanResult<HealthScore>.Create(
                "health",
                categories,
                failures,
                warnings,
                duration,
                status,
                cancelled));
        return report;
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
