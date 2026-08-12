using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepPurge.Core.Cleaning;

public sealed record Winapp2RemoteInfo(
    string CommitSha,
    DateTime CommitDateUtc,
    string SourceUrl,
    string CommitUrl)
{
    public string ShortCommit => CommitSha.Length <= 12 ? CommitSha : CommitSha[..12];
}

public sealed record Winapp2Metadata(
    string SourceUrl,
    string CommitSha,
    DateTime CommitDateUtc,
    DateTime DownloadedAtUtc,
    string Sha256,
    long ByteCount,
    string? BackupPath,
    string? PreviousSha256,
    long? PreviousByteCount,
    string? CommitUrl)
{
    public string ShortCommit => CommitSha.Length <= 12 ? CommitSha : CommitSha[..12];
    public string ShortSha256 => Sha256.Length <= 12 ? Sha256 : Sha256[..12];
    public int SchemaVersion { get; init; } = 1;
    public CleanerTargetDiff? TargetDiff { get; init; }
    public string Origin => SourceUrl;
    public string TrustState => "Remote commit metadata verified";
}

public sealed record Winapp2Provenance(
    string LocalPath,
    bool LocalExists,
    DateTime? LocalWriteTimeUtc,
    long? LocalByteCount,
    string? LocalSha256,
    Winapp2Metadata? LocalMetadata,
    Winapp2RemoteInfo? Remote,
    string? RemoteError)
{
    public DateTime? LocalSourceDateUtc => LocalMetadata?.CommitDateUtc ?? LocalWriteTimeUtc;

    public bool IsStale
    {
        get
        {
            if (!LocalExists) return true;
            if (Remote is null) return false;
            if (!string.IsNullOrWhiteSpace(LocalMetadata?.CommitSha) &&
                string.Equals(LocalMetadata.CommitSha, Remote.CommitSha, StringComparison.OrdinalIgnoreCase))
                return false;
            return LocalSourceDateUtc is null || LocalSourceDateUtc.Value.ToUniversalTime() < Remote.CommitDateUtc;
        }
    }
}

public sealed record Winapp2UpdateResult(
    bool Success,
    string? ErrorMessage,
    Winapp2Metadata? Metadata,
    string? BackupPath)
{
    public CleanerTargetDiff? Diff { get; init; }
}

public static class Winapp2Updater
{
    private const string RawUrl = "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";
    private const string ApiUrl = "https://api.github.com/repos/MoscaDotTo/Winapp2/commits?path=Winapp2.ini&per_page=1";
    private const int MinimumWinapp2Bytes = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string LocalPath => Path.Combine(DataPaths.Cleaners, "winapp2.ini");
    public static string MetadataPath => Path.Combine(DataPaths.Cleaners, "winapp2.metadata.json");
    public static string BackupDirectory => Path.Combine(DataPaths.Cleaners, "Backups");

    public static async Task<(bool IsStale, DateTime? LocalDate, DateTime? RemoteDate)> CheckStalenessAsync(CancellationToken ct = default)
    {
        var provenance = await GetProvenanceAsync(ct);
        return (provenance.IsStale, provenance.LocalSourceDateUtc, provenance.Remote?.CommitDateUtc);
    }

    public static async Task<Winapp2Provenance> GetProvenanceAsync(CancellationToken ct = default)
    {
        var local = ReadLocalProvenance(LocalPath, MetadataPath);
        Winapp2RemoteInfo? remote = null;
        string? remoteError = null;

        try
        {
            remote = await FetchRemoteInfoAsync(ct);
        }
        catch (Exception ex)
        {
            remoteError = ex.Message;
            Log.Warn($"Winapp2 remote provenance check failed: {ex.Message}");
        }

        return local with { Remote = remote, RemoteError = remoteError };
    }

    public static async Task<bool> UpdateAsync(CancellationToken ct = default)
        => (await UpdateDetailedAsync(ct)).Success;

    public static async Task<Winapp2UpdateResult> UpdateDetailedAsync(CancellationToken ct = default)
    {
        try
        {
            var remote = await FetchRemoteInfoAsync(ct);
            using var http = CreateHttpClient(TimeSpan.FromSeconds(30));
            var content = await http.GetByteArrayAsync(RawUrl, ct);
            var result = await CommitDownloadedDatabaseAsync(
                content,
                remote,
                LocalPath,
                MetadataPath,
                BackupDirectory,
                ct: ct);
            if (!result.Success)
            {
                Log.Warn($"Winapp2 update failed: {result.ErrorMessage}");
                return result;
            }

            Log.Info($"Winapp2.ini updated ({result.Metadata!.ByteCount:N0} bytes, sha256 {result.Metadata.ShortSha256})");
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"Winapp2 update failed: {ex.Message}");
            return new Winapp2UpdateResult(false, ex.Message, null, null);
        }
    }

    public static Winapp2Metadata? ReadMetadata()
        => ReadMetadata(MetadataPath);

    public static Winapp2Metadata? ReadMetadata(string metadataPath)
    {
        try
        {
            if (!File.Exists(metadataPath)) return null;
            var json = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<Winapp2Metadata>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"Winapp2 metadata read failed: {ex.Message}");
            return null;
        }
    }

    public static Winapp2Provenance ReadLocalProvenance(string localPath, string metadataPath)
    {
        var metadata = ReadMetadata(metadataPath);
        if (!File.Exists(localPath))
        {
            return new Winapp2Provenance(localPath, false, null, null, null, metadata, null, null);
        }

        try
        {
            var info = new FileInfo(localPath);
            var sha = ComputeFileSha256(localPath);
            return new Winapp2Provenance(
                localPath,
                true,
                info.LastWriteTimeUtc,
                info.Length,
                sha,
                metadata,
                null,
                null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Winapp2 local provenance failed: {ex.Message}");
            return new Winapp2Provenance(localPath, true, null, null, null, metadata, null, null);
        }
    }

    public static async Task<Winapp2UpdateResult> CommitDownloadedDatabaseAsync(
        byte[] content,
        Winapp2RemoteInfo remote,
        string localPath,
        string metadataPath,
        string backupDirectory,
        int minimumBytes = MinimumWinapp2Bytes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remote.CommitSha))
            return new Winapp2UpdateResult(false, "Remote commit SHA was missing.", null, null);

        if (content.Length < minimumBytes)
            return new Winapp2UpdateResult(false, "Downloaded winapp2.ini was suspiciously small.", null, null);

        List<Winapp2Entry> candidateEntries;
        try
        {
            candidateEntries = Winapp2Parser.Parse(new StringReader(Encoding.UTF8.GetString(content)));
            if (!TryValidateDownloadedEntries(candidateEntries, out var candidateError))
                return new Winapp2UpdateResult(false, candidateError, null, null);
        }
        catch (Exception ex)
        {
            return new Winapp2UpdateResult(false, $"Downloaded winapp2.ini could not be parsed: {ex.Message}", null, null);
        }

        var localDir = Path.GetDirectoryName(localPath);
        var metadataDir = Path.GetDirectoryName(metadataPath);
        if (string.IsNullOrWhiteSpace(localDir) || string.IsNullOrWhiteSpace(metadataDir))
            return new Winapp2UpdateResult(false, "Invalid winapp2 storage path.", null, null);

        var downloadedAt = DateTime.UtcNow;
        var sha256 = ComputeSha256(content);
        string? backupPath = null;
        string? previousSha = null;
        long? previousBytes = null;
        var previousLocalContent = Array.Empty<byte>();
        var hadPreviousLocal = File.Exists(localPath);
        string? previousMetadataContent = null;
        var previousEntries = new List<Winapp2Entry>();
        CleanerTargetDiff? diff = null;

        try
        {
            Directory.CreateDirectory(localDir);
            Directory.CreateDirectory(metadataDir);

            if (File.Exists(localPath))
            {
                var previous = new FileInfo(localPath);
                previousBytes = previous.Length;
                previousLocalContent = File.ReadAllBytes(localPath);
                previousSha = ComputeFileSha256(localPath);
                previousMetadataContent = File.Exists(metadataPath)
                    ? File.ReadAllText(metadataPath)
                    : null;
                Directory.CreateDirectory(backupDirectory);
                backupPath = UniqueBackupPath(backupDirectory, previousSha, downloadedAt);
                File.Copy(localPath, backupPath, overwrite: false);
                previousEntries = Winapp2Parser.Parse(new StringReader(Encoding.UTF8.GetString(previousLocalContent)));
            }

            diff = CleanerDefinitionRunner.CompareWinapp2Targets(previousEntries, candidateEntries);

            var metadata = new Winapp2Metadata(
                remote.SourceUrl,
                remote.CommitSha,
                remote.CommitDateUtc.ToUniversalTime(),
                downloadedAt,
                sha256,
                content.LongLength,
                backupPath,
                previousSha,
                previousBytes,
                remote.CommitUrl)
            {
                TargetDiff = diff,
            };

            await WriteBytesAtomicAsync(localPath, content, ct);
            await WriteTextAtomicAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), ct);

            return new Winapp2UpdateResult(true, null, metadata, backupPath) { Diff = diff };
        }
        catch (Exception ex)
        {
            try
            {
                if (hadPreviousLocal && backupPath is not null)
                    File.Copy(backupPath, localPath, overwrite: true);
                else if (!hadPreviousLocal)
                    TryDelete(localPath);

                if (previousMetadataContent is not null)
                    File.WriteAllText(metadataPath, previousMetadataContent, Encoding.UTF8);
                else if (File.Exists(metadataPath))
                    TryDelete(metadataPath);
            }
            catch (Exception restoreEx)
            {
                Log.Error("Winapp2 update rollback", restoreEx);
            }
            return new Winapp2UpdateResult(false, ex.Message, null, backupPath);
        }
    }

    private static bool TryValidateDownloadedEntries(
        IReadOnlyList<Winapp2Entry> entries,
        out string reason)
    {
        if (entries.Count == 0)
        {
            reason = "Downloaded winapp2.ini contained no cleaner entries.";
            return false;
        }

        foreach (var entry in entries)
        {
            foreach (var fileKey in entry.FileKeys)
            {
                var path = fileKey.Split('|', 2)[0].Trim();
                var expanded = Environment.ExpandEnvironmentVariables(path);
                if (string.IsNullOrWhiteSpace(path) || expanded.Contains("..", StringComparison.Ordinal) ||
                    !Path.IsPathFullyQualified(expanded))
                {
                    reason = $"Downloaded cleaner entry '{entry.Section}' contains an invalid file target.";
                    return false;
                }
                if (CleanerDefinitionRunner.IsProtectedCleanerPath(expanded))
                {
                    reason = $"Downloaded cleaner entry '{entry.Section}' targets protected Windows package-manager state.";
                    return false;
                }
            }

            foreach (var registryKey in entry.RegKeys)
            {
                if (!Registry.RegistryDeletion.TryParseKeyPath(registryKey, out var target) ||
                    !SafetyGuard.IsRegistryPathSafeToDelete(target.CanonicalPath))
                {
                    reason = $"Downloaded cleaner entry '{entry.Section}' contains an unsafe registry target.";
                    return false;
                }
            }
        }

        reason = "";
        return true;
    }

    private static async Task<Winapp2RemoteInfo> FetchRemoteInfoAsync(CancellationToken ct)
    {
        using var http = CreateHttpClient(TimeSpan.FromSeconds(10));
        var json = await http.GetStringAsync(ApiUrl, ct);
        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : default;

        if (first.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("GitHub returned no commits for Winapp2.ini.");

        var sha = first.GetProperty("sha").GetString();
        var commitUrl = first.TryGetProperty("html_url", out var html)
            ? html.GetString() ?? ""
            : "";
        var dateText = first
            .GetProperty("commit")
            .GetProperty("committer")
            .GetProperty("date")
            .GetString();

        if (string.IsNullOrWhiteSpace(sha) ||
            string.IsNullOrWhiteSpace(dateText) ||
            !DateTimeOffset.TryParse(dateText, out var date))
            throw new InvalidOperationException("GitHub winapp2 commit metadata was incomplete.");

        return new Winapp2RemoteInfo(
            sha,
            date.UtcDateTime,
            RawUrl,
            commitUrl);
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepPurge");
        return http;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string UniqueBackupPath(string backupDirectory, string previousSha, DateTime timestampUtc)
    {
        var prefix = previousSha.Length <= 12 ? previousSha : previousSha[..12];
        var stem = $"winapp2-{timestampUtc:yyyyMMdd-HHmmss}-{prefix}";
        var path = Path.Combine(backupDirectory, $"{stem}.ini");
        var index = 1;
        while (File.Exists(path))
            path = Path.Combine(backupDirectory, $"{stem}-{index++}.ini");
        return path;
    }

    private static async Task WriteBytesAtomicAsync(string path, byte[] content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, content, ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static async Task WriteTextAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content, ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                HandleBoundFileOperations.DeleteFileWithinScope(
                    path,
                    Path.GetDirectoryName(path)!,
                    out _);
            }
        }
        catch { /* best-effort temp cleanup */ }
    }
}
