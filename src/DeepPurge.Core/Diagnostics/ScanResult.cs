using System.Diagnostics;
using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Diagnostics;

/// <summary>
/// Describes how much confidence a scan can provide about its result set.
/// A clean result may be empty; it means every source completed successfully.
/// </summary>
public enum ScanCompletionStatus
{
    Clean,
    Partial,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed record ScanIssue(
    string Source,
    string Message,
    string? ExceptionType = null,
    bool IsWarning = false);

/// <summary>
/// The common envelope returned by multi-source and long-running scans.
/// Items are deliberately retained when a peer source fails so callers can
/// render useful partial results without overstating scan confidence.
/// </summary>
public sealed record ScanResult<T>(
    IReadOnlyList<T> Items,
    IReadOnlyList<ScanIssue> FailedSources,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration,
    ScanCompletionStatus Status,
    bool IsCancelled = false,
    string ScanName = "")
{
    public bool IsDegraded => Status != ScanCompletionStatus.Clean;

    public bool Succeeded => Status is ScanCompletionStatus.Clean or ScanCompletionStatus.Partial;

    public string StatusDisplay => Status switch
    {
        ScanCompletionStatus.Clean => "Clean",
        ScanCompletionStatus.Partial => "Partial",
        ScanCompletionStatus.Failed => "Failed",
        ScanCompletionStatus.TimedOut => "Timed out",
        ScanCompletionStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };

    public static ScanResult<T> Complete(
        string scanName,
        IReadOnlyList<T> items,
        TimeSpan duration,
        IEnumerable<string>? warnings = null)
        => Create(scanName, items, Array.Empty<ScanIssue>(), warnings, duration);

    public static ScanResult<T> Create(
        string scanName,
        IReadOnlyList<T>? items,
        IEnumerable<ScanIssue>? failedSources,
        IEnumerable<string>? warnings,
        TimeSpan duration,
        ScanCompletionStatus? status = null,
        bool isCancelled = false)
    {
        var sourceFailures = (failedSources ?? Array.Empty<ScanIssue>()).ToArray();
        var warningList = (warnings ?? Array.Empty<string>())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var resolvedStatus = status ?? Classify(items?.Count ?? 0, sourceFailures, warningList, isCancelled);
        return new ScanResult<T>(
            items ?? Array.Empty<T>(),
            sourceFailures,
            warningList,
            duration,
            resolvedStatus,
            isCancelled || resolvedStatus == ScanCompletionStatus.Cancelled,
            scanName);
    }

    public static ScanCompletionStatus Classify(
        int itemCount,
        IReadOnlyCollection<ScanIssue> failedSources,
        IReadOnlyCollection<string> warnings,
        bool isCancelled = false)
    {
        if (isCancelled) return ScanCompletionStatus.Cancelled;
        if (failedSources.Count == 0 && warnings.Count == 0) return ScanCompletionStatus.Clean;
        if (failedSources.Count == 0) return ScanCompletionStatus.Partial;
        return itemCount > 0 ? ScanCompletionStatus.Partial : ScanCompletionStatus.Failed;
    }
}

public sealed record ScanDiagnosticEntry(
    DateTime TimestampUtc,
    string ScanName,
    ScanCompletionStatus Status,
    int ItemCount,
    int FailedSourceCount,
    int WarningCount,
    long DurationMilliseconds,
    bool IsCancelled,
    IReadOnlyList<ScanIssue> FailedSources,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Stores only scan metadata, not scanned items. The JSONL file is included in
/// support bundles so a degraded result remains explainable after the UI has
/// been closed.
/// </summary>
public static class ScanDiagnosticsLedger
{
    private static readonly object Sync = new();
    private static string FilePath => Path.Combine(DataPaths.Logs, "scan-diagnostics-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");

    internal static string UsePathForTests(string path)
    {
        lock (Sync)
        {
            var previous = FilePath;
            _testPath = path;
            return previous;
        }
    }

    private static string? _testPath;
    private static string CurrentFilePath => _testPath ?? FilePath;

    public static void Record<T>(string scanName, ScanResult<T> result)
    {
        try
        {
            var entry = new ScanDiagnosticEntry(
                DateTime.UtcNow,
                scanName,
                result.Status,
                result.Items.Count,
                result.FailedSources.Count,
                result.Warnings.Count,
                (long)Math.Max(0, result.Duration.TotalMilliseconds),
                result.IsCancelled,
                result.FailedSources,
                result.Warnings);
            var json = JsonSerializer.Serialize(entry);
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CurrentFilePath)!);
                File.AppendAllText(CurrentFilePath, json + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never turn a usable scan into a failure.
        }
    }

    public static IReadOnlyList<ScanDiagnosticEntry> LoadRecent(int max = 100)
    {
        try
        {
            string[] lines;
            lock (Sync)
            {
                if (!File.Exists(CurrentFilePath)) return Array.Empty<ScanDiagnosticEntry>();
                lines = File.ReadAllLines(CurrentFilePath);
            }

            var entries = new List<ScanDiagnosticEntry>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<ScanDiagnosticEntry>(line);
                    if (entry != null) entries.Add(entry);
                }
                catch { }
            }

            return entries
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(Math.Max(0, max))
                .ToArray();
        }
        catch
        {
            return Array.Empty<ScanDiagnosticEntry>();
        }
    }

    public static void Prune(int maxEntries = 500)
    {
        try
        {
            lock (Sync)
            {
                if (!File.Exists(CurrentFilePath)) return;
                var lines = File.ReadAllLines(CurrentFilePath);
                if (lines.Length <= maxEntries) return;
                File.WriteAllLines(CurrentFilePath, lines[^maxEntries..]);
            }
        }
        catch { }
    }
}

public static class ScanExecution
{
    public static ScanResult<T> RunItems<T>(
        string scanName,
        Func<IEnumerable<T>> scan,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var items = scan()?.ToArray() ?? Array.Empty<T>();
            var result = ScanResult<T>.Create(
                scanName,
                items,
                Array.Empty<ScanIssue>(),
                Array.Empty<string>(),
                stopwatch.Elapsed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                Array.Empty<ScanIssue>(),
                new[] { "The scan was cancelled before the source completed." },
                stopwatch.Elapsed,
                ScanCompletionStatus.Cancelled,
                isCancelled: true);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (Exception ex)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                new[] { new ScanIssue(scanName, ex.Message, ex.GetType().Name) },
                Array.Empty<string>(),
                stopwatch.Elapsed,
                ScanCompletionStatus.Failed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
    }

    public static async Task<ScanResult<T>> RunItemsAsync<T>(
        string scanName,
        Func<Task<IEnumerable<T>>> scan,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var items = (await scan().ConfigureAwait(false))?.ToArray() ?? Array.Empty<T>();
            var result = ScanResult<T>.Create(
                scanName,
                items,
                Array.Empty<ScanIssue>(),
                Array.Empty<string>(),
                stopwatch.Elapsed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                Array.Empty<ScanIssue>(),
                new[] { "The scan was cancelled before the source completed." },
                stopwatch.Elapsed,
                ScanCompletionStatus.Cancelled,
                isCancelled: true);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (Exception ex)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                new[] { new ScanIssue(scanName, ex.Message, ex.GetType().Name) },
                Array.Empty<string>(),
                stopwatch.Elapsed,
                ScanCompletionStatus.Failed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
    }

    public static ScanResult<T> Run<T>(
        string scanName,
        Func<T> scan,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = ScanResult<T>.Create(
                scanName,
                new[] { scan() },
                Array.Empty<ScanIssue>(),
                Array.Empty<string>(),
                stopwatch.Elapsed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                Array.Empty<ScanIssue>(),
                new[] { "The scan was cancelled before the source completed." },
                stopwatch.Elapsed,
                ScanCompletionStatus.Cancelled,
                isCancelled: true);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (Exception ex)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                new[] { new ScanIssue(scanName, ex.Message, ex.GetType().Name) },
                Array.Empty<string>(),
                stopwatch.Elapsed,
                ScanCompletionStatus.Failed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
    }

    public static async Task<ScanResult<T>> RunAsync<T>(
        string scanName,
        Func<Task<T>> scan,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = ScanResult<T>.Create(
                scanName,
                new[] { await scan().ConfigureAwait(false) },
                Array.Empty<ScanIssue>(),
                Array.Empty<string>(),
                stopwatch.Elapsed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                Array.Empty<ScanIssue>(),
                new[] { "The scan was cancelled before the source completed." },
                stopwatch.Elapsed,
                ScanCompletionStatus.Cancelled,
                isCancelled: true);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
        catch (Exception ex)
        {
            var result = ScanResult<T>.Create(
                scanName,
                Array.Empty<T>(),
                new[] { new ScanIssue(scanName, ex.Message, ex.GetType().Name) },
                Array.Empty<string>(),
                stopwatch.Elapsed,
                ScanCompletionStatus.Failed);
            ScanDiagnosticsLedger.Record(scanName, result);
            return result;
        }
    }
}
