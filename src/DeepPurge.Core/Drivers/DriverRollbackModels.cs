using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Drivers;

public enum DriverMutationOutcome
{
    Preview,
    Exported,
    Deleted,
    Restored,
    Skipped,
    Failed,
    Cancelled,
}

public sealed record DriverToolResult(
    bool Started,
    int ExitCode,
    string Output,
    string Error = "",
    bool TimedOut = false,
    bool Canceled = false,
    string? StartError = null)
{
    public bool Succeeded => Started && !TimedOut && !Canceled && ExitCode == 0;

    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { Output, Error }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}

public interface IDriverPackageTool
{
    Task<DriverToolResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class PnpUtilDriverPackageTool : IDriverPackageTool
{
    public async Task<DriverToolResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var encoding = System.Text.Encoding.GetEncoding(
                System.Text.Encoding.Default.CodePage);
            var result = await Execution.ExternalProcessRunner.RunAsync(
                new Execution.ExternalProcessCommand("pnputil.exe")
                {
                    Arguments = arguments,
                    Timeout = TimeSpan.FromSeconds(120),
                    StandardOutputEncoding = encoding,
                    StandardErrorEncoding = encoding,
                    OutputLimitChars = 512 * 1024,
                    ErrorLimitChars = 128 * 1024,
                },
                ct: cancellationToken).ConfigureAwait(false);

            return new DriverToolResult(
                result.Started,
                result.ExitCode,
                result.Output,
                result.Error,
                result.TimedOut,
                result.Canceled,
                result.StartError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"pnputil failed: {ex.Message}");
            return new DriverToolResult(
                Started: false,
                ExitCode: -1,
                Output: "",
                Error: ex.Message,
                StartError: ex.Message);
        }
    }
}

public sealed record DriverFileHash(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record DriverRollbackArtifact(
    string OperationId,
    string PublishedName,
    string OriginalName,
    string BackupDirectory,
    string InfRelativePath,
    string InfSha256,
    string PackageSha256,
    IReadOnlyList<DriverFileHash> Files,
    DateTimeOffset ExportedAtUtc)
{
    public string InfPath => Path.Combine(BackupDirectory, InfRelativePath);
}

public sealed record DriverMutationResult(
    string OperationId,
    string PublishedName,
    DriverMutationOutcome Outcome,
    DriverRollbackArtifact? Artifact,
    string Output,
    string? Reason = null)
{
    public bool Succeeded => Outcome is
        DriverMutationOutcome.Deleted or DriverMutationOutcome.Restored;

    public bool RollbackAvailable => Artifact is not null &&
        Outcome is DriverMutationOutcome.Deleted or DriverMutationOutcome.Failed;

    public string BackupDirectory => Artifact?.BackupDirectory ?? "";
    public string PackageSha256 => Artifact?.PackageSha256 ?? "";
}

public sealed record DriverOperationEntry(
    int SchemaVersion,
    string OperationId,
    string PublishedName,
    string OriginalName,
    DriverMutationOutcome Outcome,
    DriverRollbackArtifact? Artifact,
    string Output,
    string? Reason,
    DateTimeOffset RecordedAtUtc)
{
    public DriverMutationResult ToResult()
        => new(OperationId, PublishedName, Outcome, Artifact, Output, Reason);
}

/// <summary>
/// Append-only operation ledger for driver exports, removals, and restores.
/// Unlike a best-effort diagnostic log, a successful export is not eligible
/// for deletion until this ledger has recorded the complete artifact hashes.
/// </summary>
public sealed class DriverOperationLedger
{
    private static readonly object Sync = new();
    private readonly string _path;
    private readonly string? _searchDirectory;

    public DriverOperationLedger(string? path = null)
    {
        _path = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(
            DataPaths.DriverBackups,
            $"driver-operations-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"));
        _searchDirectory = path is null ? System.IO.Path.GetDirectoryName(_path) : null;
    }

    public string Path => _path;

    public bool TryRecord(DriverOperationEntry entry, out string reason)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry);
            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.AppendAllText(
                    _path,
                    json + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }

            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Could not write driver operation ledger: {ex.Message}";
            return false;
        }
    }

    public DriverOperationEntry? LoadLatest(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;

        IEnumerable<string> paths;
        try
        {
            paths = _searchDirectory is null
                ? new[] { _path }
                : Directory.Exists(_searchDirectory)
                    ? Directory.EnumerateFiles(
                        _searchDirectory,
                        "driver-operations-*.jsonl",
                        SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();

            return paths
                .SelectMany(ReadEntries)
                .Where(e => string.Equals(e.OperationId, operationId, StringComparison.Ordinal))
                .OrderByDescending(e => e.RecordedAtUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<DriverOperationEntry> LoadRecent(int max = 100)
    {
        try
        {
            var paths = _searchDirectory is null
                ? new[] { _path }
                : Directory.Exists(_searchDirectory)
                    ? Directory.EnumerateFiles(
                        _searchDirectory,
                        "driver-operations-*.jsonl",
                        SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();

            return paths
                .SelectMany(ReadEntries)
                .OrderByDescending(e => e.RecordedAtUtc)
                .Take(max)
                .ToList();
        }
        catch { return Array.Empty<DriverOperationEntry>(); }
    }

    private static IEnumerable<DriverOperationEntry> ReadEntries(string path)
    {
        IEnumerable<string> lines;
        try { lines = File.ReadLines(path); }
        catch { yield break; }

        foreach (var line in lines)
        {
            DriverOperationEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<DriverOperationEntry>(line); }
            catch { /* tolerate a torn final JSONL line */ }
            if (entry is not null) yield return entry;
        }
    }
}
