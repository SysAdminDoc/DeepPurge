using System.Security.Cryptography;
using System.Text;
using DeepPurge.Core.Cleaning;
using Xunit;

namespace DeepPurge.Tests;

public class Winapp2UpdaterTests
{
    [Fact]
    public async Task CommitDownloadedDatabaseAsync_writes_metadata_and_previous_file_backup()
    {
        var root = CreateTempRoot();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var localPath = Path.Combine(root, "winapp2.ini");
            var metadataPath = Path.Combine(root, "winapp2.metadata.json");
            var backupDirectory = Path.Combine(root, "Backups");
            var oldContent = Winapp2Bytes("old");
            var newContent = Winapp2Bytes("new");
            await File.WriteAllBytesAsync(localPath, oldContent, ct);

            var remote = Remote("abcdef1234567890", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
            var result = await Winapp2Updater.CommitDownloadedDatabaseAsync(
                newContent,
                remote,
                localPath,
                metadataPath,
                backupDirectory,
                minimumBytes: 100,
                ct: ct);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(newContent, await File.ReadAllBytesAsync(localPath, ct));
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(oldContent, await File.ReadAllBytesAsync(result.BackupPath!, ct));

            var metadata = Winapp2Updater.ReadMetadata(metadataPath);
            Assert.NotNull(metadata);
            Assert.Equal(remote.CommitSha, metadata!.CommitSha);
            Assert.Equal(remote.CommitDateUtc, metadata.CommitDateUtc);
            Assert.Equal(remote.CommitUrl, metadata.CommitUrl);
            Assert.Equal(newContent.Length, metadata.ByteCount);
            Assert.Equal(Sha256(newContent), metadata.Sha256);
            Assert.Equal(Sha256(oldContent), metadata.PreviousSha256);
            Assert.Equal(oldContent.Length, metadata.PreviousByteCount);
            Assert.Equal(result.BackupPath, metadata.BackupPath);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CommitDownloadedDatabaseAsync_rejects_suspicious_content_without_overwriting()
    {
        var root = CreateTempRoot();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var localPath = Path.Combine(root, "winapp2.ini");
            var metadataPath = Path.Combine(root, "winapp2.metadata.json");
            var backupDirectory = Path.Combine(root, "Backups");
            var oldContent = Winapp2Bytes("old");
            await File.WriteAllBytesAsync(localPath, oldContent, ct);

            var result = await Winapp2Updater.CommitDownloadedDatabaseAsync(
                Encoding.UTF8.GetBytes("too small"),
                Remote("feedface12345678", DateTime.UtcNow),
                localPath,
                metadataPath,
                backupDirectory,
                ct: ct);

            Assert.False(result.Success);
            Assert.Contains("suspiciously small", result.ErrorMessage);
            Assert.Equal(oldContent, await File.ReadAllBytesAsync(localPath, ct));
            Assert.False(File.Exists(metadataPath));
            Assert.False(Directory.Exists(backupDirectory));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Provenance_marks_local_metadata_stale_against_newer_remote_commit()
    {
        var metadata = new Winapp2Metadata(
            "https://raw.example/winapp2.ini",
            "1111111111111111",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
            new string('a', 64),
            1234,
            null,
            null,
            null,
            "https://example/commit/1111");

        var stale = new Winapp2Provenance(
            "winapp2.ini",
            LocalExists: true,
            LocalWriteTimeUtc: metadata.DownloadedAtUtc,
            LocalByteCount: metadata.ByteCount,
            LocalSha256: metadata.Sha256,
            LocalMetadata: metadata,
            Remote: Remote("2222222222222222", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            RemoteError: null);

        var current = stale with
        {
            Remote = Remote(metadata.CommitSha, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        Assert.True(stale.IsStale);
        Assert.False(current.IsStale);
    }

    private static Winapp2RemoteInfo Remote(string sha, DateTime dateUtc)
        => new(sha, dateUtc, "https://raw.example/winapp2.ini", $"https://example/commit/{sha}");

    private static byte[] Winapp2Bytes(string marker)
        => Encoding.UTF8.GetBytes("[DeepPurge Test *]\nLangSecRef=3021\nFileKey1=%TEMP%|*.tmp\n" +
                                  new string('x', 1100) + marker);

    private static string Sha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DeepPurge-Winapp2UpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }
}
