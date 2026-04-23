using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Updates;

public record UpdateInfo(
    bool   HasUpdate,
    string LatestVersion,
    string CurrentVersion,
    string ReleaseUrl,
    string ReleaseNotes,
    string DownloadUrl);

/// <summary>
/// Minimal GitHub-Releases-based update checker. Hits the GitHub REST API
/// <c>/repos/{owner}/{repo}/releases/latest</c> and compares semver against
/// the running assembly version.
///
/// Version-compare correctness note: <see cref="Version"/> treats missing
/// components as -1, so "0.9.0" (build=0, revision=-1) compares as LESS
/// than "0.9.0.0" (build=0, revision=0). We normalise both sides to 4 parts
/// (missing components → 0) before comparing, otherwise every call against
/// a 3-part GitHub tag would falsely report an available update.
/// </summary>
public class UpdateChecker
{
    // Shared HttpClient — reusing a single instance avoids socket exhaustion
    // under repeated polling and picks up DNS changes correctly under .NET 8.
    private static readonly HttpClient _http = CreateHttpClient();

    public string Owner { get; set; } = "SysAdminDoc";
    public string Repo  { get; set; } = "DeepPurge";

    /// <summary>Returns null on network failure / parse failure. Never throws.</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag      = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var htmlUrl  = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";
            var body     = root.TryGetProperty("body",     out var b) ? (b.GetString() ?? "") : "";

            string download = htmlUrl;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    // Prefer the GUI exe; fall back to any .exe.
                    if (name.Equals("DeepPurge.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        download = asset.TryGetProperty("browser_download_url", out var dl)
                            ? (dl.GetString() ?? htmlUrl) : htmlUrl;
                        break;
                    }
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && download == htmlUrl)
                    {
                        download = asset.TryGetProperty("browser_download_url", out var dl)
                            ? (dl.GetString() ?? htmlUrl) : htmlUrl;
                    }
                }
            }

            var latest  = Normalise(tag);
            var current = Normalise(currentVersion);
            var isNewer = CompareVersions(latest, current) > 0;

            return new UpdateInfo(isNewer, latest, current, htmlUrl, body, download);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn($"UpdateChecker.CheckAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepPurge-UpdateChecker");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Trim leading "v" / whitespace; leave dotted components alone.</summary>
    private static string Normalise(string v) => (v ?? "").Trim().TrimStart('v', 'V');

    /// <summary>
    /// Semver-aware comparison: returns 1 if <paramref name="a"/> &gt; b, -1 if less, 0 if equal.
    /// Missing components treat as 0 (not -1 as <see cref="Version"/> does).
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = ParseParts(a);
        var pb = ParseParts(b);
        int max = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < max; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    private static int[] ParseParts(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return Array.Empty<int>();
        var chunks = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var parts = new int[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            // Strip pre-release / build metadata (e.g. "0.9.0-beta" → 0) for ordering.
            var clean = new string(chunks[i].TakeWhile(char.IsDigit).ToArray());
            parts[i] = int.TryParse(clean, out var n) ? n : 0;
        }
        return parts;
    }
}
