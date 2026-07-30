using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.App;
using System.Text.Json;
using Xunit;

namespace DeepPurge.Tests;

public class DeletionManifestTests
{
    [Fact]
    public void Record_creates_valid_jsonl_entry()
    {
        var entry = new DeletionEntry(
            @"C:\Test\file.txt", "file", 1024, DateTime.UtcNow, "delete");

        Assert.Equal(@"C:\Test\file.txt", entry.Path);
        Assert.Equal("file", entry.Type);
        Assert.Equal(1024, entry.SizeBytes);
        Assert.Equal("delete", entry.Operation);
    }

    [Fact]
    public void DeletionEntry_supports_registry_type()
    {
        var entry = new DeletionEntry(
            @"HKLM\SOFTWARE\TestApp", "registry", 0, DateTime.UtcNow, "uninstall-leftover");

        Assert.Equal("registry", entry.Type);
        Assert.StartsWith("HKLM", entry.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestSummary_has_expected_fields()
    {
        var summary = new ManifestSummary(
            @"C:\Logs\deletions-2026-06-26.jsonl",
            new DateTime(2026, 6, 26),
            42,
            1024 * 1024);

        Assert.Equal(42, summary.EntryCount);
        Assert.Equal(1024 * 1024, summary.TotalBytes);
        Assert.Equal(new DateTime(2026, 6, 26), summary.Date);
    }

    [Fact]
    public void RestoreResult_reports_counts()
    {
        var result = new RestoreResult(
            RegistryRestored: 3,
            FilesRecoverable: 5,
            Unrecoverable: 2,
            Details: new List<string> { "item1", "item2" });

        Assert.Equal(3, result.RegistryRestored);
        Assert.Equal(5, result.FilesRecoverable);
        Assert.Equal(2, result.Unrecoverable);
        Assert.Equal(2, result.Details.Count);
    }

    [Fact]
    public void RestoreFromManifest_returns_empty_for_nonexistent_date()
    {
        var result = DeletionManifest.RestoreFromManifest(new DateTime(1999, 1, 1));
        Assert.Equal(0, result.RegistryRestored);
        Assert.Equal(0, result.FilesRecoverable);
        Assert.Equal(0, result.Unrecoverable);
    }

    [Fact]
    public void LoadManifest_returns_empty_for_nonexistent_date()
    {
        var entries = DeletionManifest.LoadManifest(new DateTime(1999, 1, 1));
        Assert.Empty(entries);
    }

    [Fact]
    public void Malformed_manifest_lines_are_ignored_without_hiding_valid_entries()
    {
        var date = new DateTime(2099, 12, 30);
        var path = Path.Combine(DataPaths.Logs, $"deletions-{date:yyyy-MM-dd}.jsonl");
        var previous = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            var entry = new DeletionEntry(
                @"C:\Temp\recoverable.tmp",
                "file",
                256,
                date,
                "delete");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, new[]
            {
                "{not valid json",
                JsonSerializer.Serialize(entry),
            });

            var entries = DeletionManifest.LoadManifest(date);
            var manifests = DeletionManifest.ListManifests();

            Assert.Single(entries);
            Assert.Equal(entry.Path, entries[0].Path);
            Assert.Contains(manifests, m => m.Date.Date == date.Date && m.EntryCount == 1);
        }
        finally
        {
            if (previous == null)
            {
                try { File.Delete(path); } catch { }
            }
            else
            {
                File.WriteAllText(path, previous);
            }
        }
    }

    [Fact]
    public void Legacy_registry_record_cannot_time_match_and_import_a_decoy_backup()
    {
        var date = new DateTime(2099, 12, 29);
        var manifestPath = Path.Combine(
            DataPaths.Logs,
            $"deletions-{date:yyyy-MM-dd}.jsonl");
        var decoyPath = Path.Combine(
            DataPaths.Backups,
            $"legacy-decoy-{Guid.NewGuid():N}.reg");
        var previousManifest = File.Exists(manifestPath)
            ? File.ReadAllText(manifestPath)
            : null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(
                decoyPath,
                "Windows Registry Editor Version 5.00\r\n\r\n" +
                "[HKEY_CURRENT_USER\\Software\\DeepPurgeDecoy]\r\n");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new DeletionEntry(
                    @"HKCU\Software\DeepPurgeTests\Legacy",
                    "registry",
                    0,
                    date,
                    "legacy-delete")) + Environment.NewLine);

            var result = DeletionManifest.RestoreFromManifest(date, dryRun: true);

            Assert.Equal(0, result.RegistryRestored);
            Assert.Equal(1, result.Unrecoverable);
            Assert.Contains(
                result.Details,
                detail => detail.Contains(
                    "legacy or missing bound recovery fields",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (previousManifest == null)
            {
                try { File.Delete(manifestPath); } catch { }
            }
            else
            {
                File.WriteAllText(manifestPath, previousManifest);
            }
            try { File.Delete(decoyPath); } catch { }
        }
    }

    [Fact]
    public void Manifest_loading_collapses_registry_write_ahead_states_by_operation_id()
    {
        var date = new DateTime(2099, 12, 28);
        var path = Path.Combine(DataPaths.Logs, $"deletions-{date:yyyy-MM-dd}.jsonl");
        var previous = File.Exists(path) ? File.ReadAllText(path) : null;
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            var prepared = new DeletionEntry(
                @"HKCU\Software\DeepPurgeTests\Wal",
                "registry",
                0,
                date,
                "test",
                SchemaVersion: 2,
                OperationId: operationId,
                Outcome: "Prepared");
            var succeeded = prepared with
            {
                TimestampUtc = date.AddSeconds(1),
                Outcome = "Succeeded",
            };
            File.WriteAllLines(path, new[]
            {
                JsonSerializer.Serialize(prepared),
                JsonSerializer.Serialize(succeeded),
            });

            var entry = Assert.Single(DeletionManifest.LoadManifest(date));
            Assert.Equal("Succeeded", entry.Outcome);
            Assert.Equal(operationId, entry.OperationId);
        }
        finally
        {
            if (previous == null)
            {
                try { File.Delete(path); } catch { }
            }
            else
            {
                File.WriteAllText(path, previous);
            }
        }
    }
}
