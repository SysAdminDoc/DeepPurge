using Microsoft.Data.Sqlite;
using DeepPurge.Core.Privacy;
using Xunit;

namespace DeepPurge.Tests;

public class CookieWhitelistTests
{
    [Theory]
    [InlineData("Cookies", true)]
    [InlineData("Cookies-journal", true)]
    [InlineData("cookies.sqlite", true)]
    [InlineData("cookies.sqlite-wal", true)]
    [InlineData("cookies.sqlite-shm", true)]
    [InlineData("History", false)]
    [InlineData("Cache", false)]
    [InlineData("", false)]
    [InlineData("cookie.db", false)]
    public void IsCookiePath_detects_cookie_files(string filename, bool expected)
    {
        var path = Path.Combine(@"C:\Users\test\AppData\Local\Google\Chrome\User Data\Default", filename);
        Assert.Equal(expected, EvidenceRemover.IsCookiePath(path));
    }

    [Fact]
    public void IsCookiePath_handles_full_path()
    {
        Assert.True(EvidenceRemover.IsCookiePath(@"C:\deep\nested\path\to\Cookies"));
        Assert.True(EvidenceRemover.IsCookiePath(@"C:\Users\me\AppData\Roaming\Mozilla\Firefox\Profiles\abc123\cookies.sqlite"));
        Assert.False(EvidenceRemover.IsCookiePath(@"C:\Users\me\AppData\Roaming\Mozilla\Firefox\Profiles\abc123\places.sqlite"));
    }

    [Fact]
    public void DomainCleaner_preserves_whitelisted_and_deletes_rest()
    {
        var dbPath = CreateChromiumCookieDb(new[]
        {
            ("github.com", "session"),
            (".google.com", "NID"),
            ("example.com", "tracking"),
            (".ads.example.net", "pixel"),
        });

        try
        {
            var result = CookieDomainCleaner.CleanChromium(
                dbPath, new[] { "github.com", "google.com" }, dryRun: false, "Test");

            Assert.False(result.Skipped);
            Assert.Equal(4, result.TotalCookies);
            Assert.Equal(2, result.PreservedCookies);
            Assert.Equal(2, result.DeletedCookies);

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT host_key FROM cookies ORDER BY host_key";
            var remaining = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) remaining.Add(reader.GetString(0));

            Assert.Contains(".google.com", remaining);
            Assert.Contains("github.com", remaining);
            Assert.DoesNotContain("example.com", remaining);
            Assert.DoesNotContain(".ads.example.net", remaining);
        }
        finally { TryDeleteFile(dbPath); }
    }

    [Fact]
    public void DomainCleaner_dry_run_does_not_modify_database()
    {
        var dbPath = CreateChromiumCookieDb(new[]
        {
            ("github.com", "session"),
            ("example.com", "tracking"),
        });

        try
        {
            var result = CookieDomainCleaner.CleanChromium(
                dbPath, new[] { "github.com" }, dryRun: true, "Test");

            Assert.False(result.Skipped);
            Assert.True(result.DryRun);
            Assert.Equal(2, result.TotalCookies);
            Assert.Equal(1, result.PreservedCookies);
            Assert.Equal(1, result.DeletedCookies);

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cookies";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }
        finally { TryDeleteFile(dbPath); }
    }

    [Fact]
    public void DomainCleaner_returns_skipped_for_missing_database()
    {
        var result = CookieDomainCleaner.CleanChromium(
            @"C:\nonexistent\path\Cookies", new[] { "github.com" }, dryRun: false, "Test");
        Assert.True(result.Skipped);
    }

    private static string CreateChromiumCookieDb((string host, string name)[] cookies)
    {
        var path = Path.Combine(Path.GetTempPath(), $"DeepPurgeCookieTest-{Guid.NewGuid():N}");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE cookies (
                host_key TEXT NOT NULL,
                name TEXT NOT NULL,
                value TEXT DEFAULT '',
                path TEXT DEFAULT '/',
                expires_utc INTEGER DEFAULT 0,
                is_secure INTEGER DEFAULT 0,
                is_httponly INTEGER DEFAULT 0
            )
            """;
        cmd.ExecuteNonQuery();

        foreach (var (host, name) in cookies)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO cookies (host_key, name) VALUES (@h, @n)";
            insert.Parameters.AddWithValue("@h", host);
            insert.Parameters.AddWithValue("@n", name);
            insert.ExecuteNonQuery();
        }
        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
