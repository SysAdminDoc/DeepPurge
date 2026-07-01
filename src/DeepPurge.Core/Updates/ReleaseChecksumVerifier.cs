using System.Security.Cryptography;
using System.Text.Json;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Updates;

public enum ChecksumVerifyStatus
{
    Match,
    Mismatch,
    AssetNotFound,
    ReleaseNotFound,
    NetworkError,
    FileNotFound,
}

public record ChecksumVerifyResult(
    ChecksumVerifyStatus Status,
    string? LocalHash,
    string? RemoteHash,
    string? AssetName,
    string? ReleaseTag,
    string? ErrorDetail)
{
    public string StatusDisplay => Status switch
    {
        ChecksumVerifyStatus.Match => "MATCH",
        ChecksumVerifyStatus.Mismatch => "MISMATCH",
        ChecksumVerifyStatus.AssetNotFound => "SHA256SUMS.txt not found in release",
        ChecksumVerifyStatus.ReleaseNotFound => "No release found",
        ChecksumVerifyStatus.NetworkError => $"Network error: {ErrorDetail}",
        ChecksumVerifyStatus.FileNotFound => "Executable not found",
        _ => "Unknown",
    };
}

public class ReleaseChecksumVerifier
{
    private static readonly HttpClient _http = CreateHttpClient();

    public string Owner { get; set; } = "SysAdminDoc";
    public string Repo { get; set; } = "DeepPurge";

    public async Task<ChecksumVerifyResult> VerifyAsync(
        string? exePath = null,
        CancellationToken ct = default)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return new(ChecksumVerifyStatus.FileNotFound, null, null, null, null, "Executable path unavailable");

        string localHash;
        try
        {
            using var stream = File.OpenRead(exePath);
            localHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return new(ChecksumVerifyStatus.FileNotFound, null, null, null, null, ex.Message);
        }

        var exeName = Path.GetFileName(exePath);

        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";

            string? checksumUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        checksumUrl = asset.TryGetProperty("browser_download_url", out var dl)
                            ? dl.GetString() : null;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(checksumUrl))
                return new(ChecksumVerifyStatus.AssetNotFound, localHash, null, exeName, tag, null);

            var checksumContent = await _http.GetStringAsync(checksumUrl, ct);
            var remoteHash = ParseChecksumFile(checksumContent, exeName);

            if (string.IsNullOrEmpty(remoteHash))
                return new(ChecksumVerifyStatus.AssetNotFound, localHash, null, exeName, tag,
                    $"No entry for '{exeName}' in SHA256SUMS.txt");

            var match = string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase);
            return new(
                match ? ChecksumVerifyStatus.Match : ChecksumVerifyStatus.Mismatch,
                localHash, remoteHash, exeName, tag, null);
        }
        catch (OperationCanceledException)
        {
            return new(ChecksumVerifyStatus.NetworkError, localHash, null, exeName, null, "Request cancelled");
        }
        catch (Exception ex)
        {
            Log.Warn($"ReleaseChecksumVerifier: {ex.GetType().Name}: {ex.Message}");
            return new(ChecksumVerifyStatus.NetworkError, localHash, null, exeName, null, ex.Message);
        }
    }

    public static string? ParseChecksumFile(string content, string fileName)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var sep = trimmed.IndexOf("  ", StringComparison.Ordinal);
            if (sep < 0) sep = trimmed.IndexOf(' ');
            if (sep < 0) continue;

            var hash = trimmed[..sep].Trim();
            var name = trimmed[(sep + 1)..].Trim();

            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepPurge-ChecksumVerifier");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
