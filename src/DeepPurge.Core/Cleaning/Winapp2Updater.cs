using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Cleaning;

public static class Winapp2Updater
{
    private const string RawUrl = "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";
    private const string ApiUrl = "https://api.github.com/repos/MoscaDotTo/Winapp2/commits?path=Winapp2.ini&per_page=1";

    public static string LocalPath => Path.Combine(DataPaths.Cleaners, "winapp2.ini");

    public static async Task<(bool IsStale, DateTime? LocalDate, DateTime? RemoteDate)> CheckStalenessAsync(CancellationToken ct = default)
    {
        var localDate = File.Exists(LocalPath)
            ? (DateTime?)File.GetLastWriteTimeUtc(LocalPath)
            : null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepPurge");
            var json = await http.GetStringAsync(ApiUrl, ct);
            var match = System.Text.RegularExpressions.Regex.Match(json, @"""date""\s*:\s*""([^""]+)""");
            if (match.Success && DateTime.TryParse(match.Groups[1].Value, out var remoteDate))
            {
                var isStale = localDate == null || localDate.Value < remoteDate.ToUniversalTime();
                return (isStale, localDate, remoteDate.ToUniversalTime());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Winapp2 staleness check failed: {ex.Message}");
        }

        return (localDate == null, localDate, null);
    }

    public static async Task<bool> UpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepPurge");
            var content = await http.GetStringAsync(RawUrl, ct);
            if (string.IsNullOrWhiteSpace(content) || content.Length < 1000)
            {
                Log.Warn("Winapp2 download returned suspiciously small content");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
            await File.WriteAllTextAsync(LocalPath, content, ct);
            Log.Info($"Winapp2.ini updated ({content.Length:N0} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Winapp2 update failed: {ex.Message}");
            return false;
        }
    }
}
