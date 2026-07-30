using System.Security.Cryptography;
using System.Text;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;
using global::Microsoft.Win32;
using Xunit;

namespace DeepPurge.Tests;

public sealed class RegistryBackupStoreTests : IDisposable
{
    private readonly string _backupRoot = Path.Combine(
        Path.GetTempPath(),
        "DeepPurgeRegistryBackupTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _subKey =
        $@"Software\DeepPurgeTests\backup_{Guid.NewGuid():N}";

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                _subKey,
                throwOnMissingSubKey: false);
        }
        catch { }
        try
        {
            if (Directory.Exists(_backupRoot))
                Directory.Delete(_backupRoot, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Bound_document_rejects_metadata_and_scope_tampering()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            Assert.NotNull(key);
            key.SetValue("RootValue", "root");
            using var child = key.CreateSubKey("Child");
            child.SetValue("ChildValue", new byte[] { 1, 2, 3 });
        }

        Assert.True(RegistryDeletion.TryParseKeyPath(
            $@"HKCU\{_subKey}",
            out var target));
        Assert.Equal(
            RegistryOpenStatus.Opened,
            RegistryNative.TryOpenForKeyDeletion(target, out var chain));

        RegistryObjectSnapshot snapshot;
        using (chain)
            snapshot = RegistryNative.CaptureTree(target, chain!.Target);

        var store = new RegistryBackupStore(
            _backupRoot,
            requireTrustedAcl: false);
        var artifact = store.Create(snapshot);
        Assert.NotNull(artifact);

        var bytes = File.ReadAllBytes(artifact!.BackupPath);
        Assert.Equal(
            artifact.BackupSha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        var metadata = new RegistryBackupMetadata(
            2,
            artifact.OperationId,
            artifact.Hive,
            artifact.SubKey,
            artifact.ValueName,
            artifact.RegistryView,
            artifact.ObjectIdentity);
        Assert.True(
            RegistryBackupStore.TryValidateRegistryDocument(
                bytes,
                artifact.Hive,
                artifact.SubKey,
                artifact.ValueName,
                metadata,
                out var validReason),
            validReason);

        var wrongMetadata = metadata with { OperationId = Guid.NewGuid().ToString("N") };
        Assert.False(
            RegistryBackupStore.TryValidateRegistryDocument(
                bytes,
                artifact.Hive,
                artifact.SubKey,
                artifact.ValueName,
                wrongMetadata,
                out var metadataReason));
        Assert.Contains("metadata", metadataReason, StringComparison.OrdinalIgnoreCase);

        var document = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        var expectedHeader = $"[HKEY_CURRENT_USER\\{_subKey}]";
        var tamperedDocument = document.Replace(
            expectedHeader,
            "[HKEY_CURRENT_USER\\Software\\OutsideDeepPurgeScope]",
            StringComparison.Ordinal);
        Assert.NotEqual(document, tamperedDocument);
        var preamble = Encoding.Unicode.GetPreamble();
        var body = Encoding.Unicode.GetBytes(tamperedDocument);
        var tamperedBytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, tamperedBytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, tamperedBytes, preamble.Length, body.Length);

        Assert.False(
            RegistryBackupStore.TryValidateRegistryDocument(
                tamperedBytes,
                artifact.Hive,
                artifact.SubKey,
                artifact.ValueName,
                metadata,
                out var scopeReason));
        Assert.Contains("scope", scopeReason, StringComparison.OrdinalIgnoreCase);
    }
}
