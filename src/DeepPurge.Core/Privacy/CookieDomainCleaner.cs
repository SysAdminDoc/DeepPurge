using Microsoft.Data.Sqlite;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Privacy;

public sealed record CookieCleanResult(
    string BrowserProfile,
    string DatabasePath,
    int TotalCookies,
    int PreservedCookies,
    int DeletedCookies,
    bool DryRun,
    bool Skipped,
    string? SkipReason);

public static class CookieDomainCleaner
{
    public static CookieCleanResult CleanChromium(
        string cookieDbPath,
        IReadOnlyList<string> whitelistedDomains,
        bool dryRun,
        string browserProfile)
    {
        return CleanDatabase(cookieDbPath, whitelistedDomains, dryRun, browserProfile,
            table: "cookies", domainColumn: "host_key");
    }

    public static CookieCleanResult CleanFirefox(
        string cookieDbPath,
        IReadOnlyList<string> whitelistedDomains,
        bool dryRun,
        string browserProfile)
    {
        return CleanDatabase(cookieDbPath, whitelistedDomains, dryRun, browserProfile,
            table: "moz_cookies", domainColumn: "host");
    }

    private static CookieCleanResult CleanDatabase(
        string dbPath,
        IReadOnlyList<string> domains,
        bool dryRun,
        string browserProfile,
        string table,
        string domainColumn)
    {
        if (!File.Exists(dbPath))
            return new(browserProfile, dbPath, 0, 0, 0, dryRun, true, "Database not found");

        string backupPath = "";
        try
        {
            if (!dryRun)
            {
                backupPath = Path.Combine(DataPaths.Backups, Path.GetFileName(dbPath) + ".bak");
                File.Copy(dbPath, backupPath, overwrite: true);
            }

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode={(dryRun ? "ReadOnly" : "ReadWrite")}");
            conn.Open();

            int total = CountRows(conn, table);
            if (total == 0)
                return new(browserProfile, dbPath, 0, 0, 0, dryRun, false, null);

            var preserved = CountPreserved(conn, table, domainColumn, domains);
            var toDelete = total - preserved;

            if (!dryRun && toDelete > 0)
            {
                DeleteNonWhitelisted(conn, table, domainColumn, domains);
            }

            return new(browserProfile, dbPath, total, preserved, toDelete, dryRun, false, null);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
        {
            return new(browserProfile, dbPath, 0, 0, 0, dryRun, true,
                "Database locked — close the browser and retry");
        }
        catch (Exception ex)
        {
            Log.Warn($"CookieDomainCleaner: {browserProfile}: {ex.Message}");
            return new(browserProfile, dbPath, 0, 0, 0, dryRun, true, ex.Message);
        }
    }

    private static int CountRows(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int CountPreserved(
        SqliteConnection conn, string table, string domainColumn,
        IReadOnlyList<string> domains)
    {
        if (domains.Count == 0) return 0;

        var where = BuildDomainWhereClause(domainColumn, domains);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{table}] WHERE {where}";
        AddDomainParameters(cmd, domains);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void DeleteNonWhitelisted(
        SqliteConnection conn, string table, string domainColumn,
        IReadOnlyList<string> domains)
    {
        var where = BuildDomainWhereClause(domainColumn, domains);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM [{table}] WHERE NOT ({where})";
        AddDomainParameters(cmd, domains);
        cmd.ExecuteNonQuery();
    }

    private static string BuildDomainWhereClause(string col, IReadOnlyList<string> domains)
    {
        var clauses = new List<string>();
        for (int i = 0; i < domains.Count; i++)
        {
            clauses.Add($"[{col}] = @d{i} OR [{col}] LIKE @dp{i}");
        }
        return string.Join(" OR ", clauses);
    }

    private static void AddDomainParameters(SqliteCommand cmd, IReadOnlyList<string> domains)
    {
        for (int i = 0; i < domains.Count; i++)
        {
            var domain = domains[i].TrimStart('.');
            cmd.Parameters.AddWithValue($"@d{i}", domain);
            cmd.Parameters.AddWithValue($"@dp{i}", $"%.{domain}");
        }
    }
}
