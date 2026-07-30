using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.App;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;
using global::Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DeepPurge.Tests;

public class RegistryDeletionTests : IDisposable
{
    private const string TestRoot = @"Software\DeepPurgeTests";
    private readonly string _backupRoot = Path.Combine(
        Path.GetTempPath(),
        "DeepPurgeTests",
        Guid.NewGuid().ToString("N"));
    private readonly RegistryBackupStore _backupStore;
    private readonly IDisposable _manifestScope;

    public RegistryDeletionTests()
    {
        _backupStore = new RegistryBackupStore(
            _backupRoot,
            requireTrustedAcl: false);
        _manifestScope = DeletionManifest.UseManifestPathForTests(
            Path.Combine(_backupRoot, "deletions-test.jsonl"));
    }

    public void Dispose()
    {
        _manifestScope.Dispose();
        try
        {
            if (Directory.Exists(_backupRoot))
                Directory.Delete(_backupRoot, recursive: true);
        }
        catch { }
    }

    [Theory]
    [InlineData(@"HKCU\Software\Vendor\App", RegistryHive.CurrentUser, "HKCU", @"Software\Vendor\App")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Vendor\App", RegistryHive.CurrentUser, "HKCU", @"Software\Vendor\App")]
    [InlineData(@"HKLM\SOFTWARE\Vendor\App", RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\Vendor\App")]
    [InlineData(@"HKCR\*\shell\Vendor", RegistryHive.ClassesRoot, "HKCR", @"*\shell\Vendor")]
    [InlineData(@"HKU\S-1-5-18\Software\Vendor", RegistryHive.Users, "HKU", @"S-1-5-18\Software\Vendor")]
    public void TryParseKeyPath_handles_supported_hives(
        string path,
        RegistryHive hive,
        string hiveName,
        string subKey)
    {
        Assert.True(RegistryDeletion.TryParseKeyPath(path, out var target));
        Assert.Equal(hive, target.Hive);
        Assert.Equal(hiveName, target.HiveName);
        Assert.Equal(subKey, target.SubKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HKCU")]
    [InlineData(@"HKCU\")]
    [InlineData(@"HKXX\Software\App")]
    [InlineData(@"HKCU\\Software\App")]
    [InlineData(@"HKCU\Software\..\Bad")]
    [InlineData("HKCU\\Software\\Bad\r\nNext")]
    [InlineData("HKCU\\Software\\Bad|Pipe")]
    public void TryParseKeyPath_rejects_malformed_paths(string path)
    {
        Assert.False(RegistryDeletion.TryParseKeyPath(path, out _));
    }

    [Fact]
    public void TryParseValuePath_splits_parent_key_and_value_name()
    {
        Assert.True(RegistryDeletion.TryParseValuePath(
            @"HKLM\SOFTWARE\Vendor\App\Setting",
            out var target,
            out var valueName));

        Assert.Equal(RegistryHive.LocalMachine, target.Hive);
        Assert.Equal(@"SOFTWARE\Vendor\App", target.SubKey);
        Assert.Equal("Setting", valueName);
    }

    [Fact]
    public void DeleteKeyTree_rejects_safetyguard_blocked_paths()
    {
        var result = RegistryDeletion.DeleteKeyTree(
            @"HKLM\SYSTEM\CurrentControlSet\Control",
            "test-blocked");

        Assert.Equal(RegistryDeletionStatus.SkippedUnsafePath, result.Status);
    }

    [Fact]
    public void DeleteKeyTree_backs_up_deletes_and_records_hkcu_key()
    {
        var subKey = $@"{TestRoot}\key_{Guid.NewGuid():N}";
        var fullPath = $@"HKCU\{subKey}";
        using (var key = Registry.CurrentUser.CreateSubKey(subKey))
        {
            Assert.NotNull(key);
            key.SetValue("Value", "data");
        }

        try
        {
            var result = RegistryDeletion.DeleteKeyTree(
                fullPath,
                "test-regkey-delete",
                dryRun: false,
                _backupStore);

            Assert.Equal(RegistryDeletionStatus.Deleted, result.Status);
            Assert.False(KeyExists(subKey));
            AssertBackupExists(result);
            AssertManifestContainsBoundTransaction(fullPath, "test-regkey-delete", result);
        }
        finally
        {
            TryDeleteTestKey(subKey);
        }
    }

    [Fact]
    public void DeleteValue_backs_up_parent_deletes_value_and_records_hkcu_value()
    {
        var subKey = $@"{TestRoot}\value_{Guid.NewGuid():N}";
        var fullValuePath = $@"HKCU\{subKey}\DeleteMe";
        using (var key = Registry.CurrentUser.CreateSubKey(subKey))
        {
            Assert.NotNull(key);
            key.SetValue("DeleteMe", "data");
            key.SetValue("KeepMe", "data");
        }

        try
        {
            var result = RegistryDeletion.DeleteValue(
                fullValuePath,
                "test-regvalue-delete",
                dryRun: false,
                _backupStore);

            Assert.Equal(RegistryDeletionStatus.Deleted, result.Status);
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            Assert.NotNull(key);
            Assert.Null(key.GetValue("DeleteMe"));
            Assert.Equal("data", key.GetValue("KeepMe"));
            AssertBackupExists(result);
            AssertManifestContainsBoundTransaction(
                fullValuePath,
                "test-regvalue-delete",
                result);
            var backupText = File.ReadAllText(result.BackupPath!);
            Assert.Contains("\"DeleteMe\"=", backupText);
            Assert.DoesNotContain("\"KeepMe\"=", backupText);
        }
        finally
        {
            TryDeleteTestKey(subKey);
        }
    }

    [Fact]
    public void Link_aware_open_does_not_mistake_an_ordinary_key_class_for_a_link()
    {
        var subKey = $@"{TestRoot}\class_{Guid.NewGuid():N}";
        var result = RegCreateKeyExW(
            HKeyCurrentUser,
            subKey,
            0,
            "DeepPurge ordinary class",
            0,
            KeyAllAccess,
            IntPtr.Zero,
            out var created,
            out _);
        Assert.Equal(0, result);

        try
        {
            created.Dispose();
            Assert.True(RegistryDeletion.TryParseKeyPath(
                $@"HKCU\{subKey}",
                out var target));
            Assert.Equal(
                RegistryOpenStatus.Opened,
                RegistryNative.TryOpenForKeyDeletion(target, out var chain));
            using (chain)
                Assert.False(RegistryNative.IsSymbolicLink(chain!.Target));
        }
        finally
        {
            created.Dispose();
            TryDeleteTestKey(subKey);
        }
    }

    [Fact]
    public void Link_aware_delete_refuses_a_registry_symbolic_link()
    {
        var targetSubKey = $@"{TestRoot}\link_target_{Guid.NewGuid():N}";
        var linkSubKey = $@"{TestRoot}\link_{Guid.NewGuid():N}";
        using (var target = Registry.CurrentUser.CreateSubKey(targetSubKey))
        {
            Assert.NotNull(target);
            target.SetValue("Keep", "safe");
        }

        SafeRegistryHandle? link = null;
        try
        {
            var create = RegCreateKeyExW(
                HKeyCurrentUser,
                linkSubKey,
                0,
                null,
                RegOptionCreateLink,
                KeyAllAccess | KeyCreateLink,
                IntPtr.Zero,
                out link,
                out _);
            Assert.Equal(0, create);

            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var nativeTarget = $@"\Registry\User\{sid}\{targetSubKey}";
            var data = Encoding.Unicode.GetBytes(nativeTarget + "\0");
            Assert.Equal(
                0,
                RegSetValueExW(
                    link.DangerousGetHandle(),
                    "SymbolicLinkValue",
                    0,
                    RegLink,
                    data,
                    data.Length));
            link.Dispose();
            link = null;

            var deletion = RegistryDeletion.DeleteKeyTree(
                $@"HKCU\{linkSubKey}",
                "test-registry-link",
                dryRun: false,
                _backupStore);

            Assert.Equal(RegistryDeletionStatus.SkippedSymlink, deletion.Status);
            using var target = Registry.CurrentUser.OpenSubKey(targetSubKey);
            Assert.Equal("safe", target?.GetValue("Keep"));
        }
        finally
        {
            link?.Dispose();
            DeleteLinkKey(linkSubKey);
            TryDeleteTestKey(targetSubKey);
        }
    }

    [Fact]
    public void DeleteKeyTree_aborts_when_the_handle_bound_tree_drifts_after_backup()
    {
        var subKey = $@"{TestRoot}\drift_{Guid.NewGuid():N}";
        var fullPath = $@"HKCU\{subKey}";
        using (var key = Registry.CurrentUser.CreateSubKey(subKey))
        {
            Assert.NotNull(key);
            key.SetValue("Original", "one");
        }

        var store = new RegistryBackupStore(
            _backupRoot,
            requireTrustedAcl: false,
            createdHook: _ =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                key!.SetValue("AddedDuringBackup", "two");
            });

        try
        {
            var result = RegistryDeletion.DeleteKeyTree(
                fullPath,
                "test-registry-drift",
                dryRun: false,
                store);

            Assert.Equal(RegistryDeletionStatus.SkippedDrift, result.Status);
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            Assert.Equal("one", key?.GetValue("Original"));
            Assert.Equal("two", key?.GetValue("AddedDuringBackup"));
            Assert.Empty(Directory.GetFiles(_backupRoot, "*.reg"));
        }
        finally
        {
            TryDeleteTestKey(subKey);
        }
    }

    [Fact]
    public void Elevated_production_store_round_trips_only_its_bound_artifact()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DEEPPURGE_RUN_ELEVATED_TESTS"),
                "1",
                StringComparison.Ordinal))
            return;

        using (var identity = WindowsIdentity.GetCurrent())
        {
            Assert.True(
                new WindowsPrincipal(identity).IsInRole(
                    WindowsBuiltInRole.Administrator),
                "The production ACL test must run elevated.");
        }

        var subKey = $@"{TestRoot}\production_{Guid.NewGuid():N}";
        var valuePath = $@"HKCU\{subKey}\RestoreMe";
        var currentManifest = DeletionManifest.CurrentManifestPath;
        var restoreDate = new DateTime(2099, 12, 27);
        string? backupPath = null;

        using (var key = Registry.CurrentUser.CreateSubKey(subKey))
        {
            Assert.NotNull(key);
            key.SetValue("RestoreMe", "bound-data");
        }

        try
        {
            var deletion = RegistryDeletion.DeleteValue(
                valuePath,
                "test-production-registry-transaction");
            Assert.Equal(RegistryDeletionStatus.Deleted, deletion.Status);
            backupPath = deletion.BackupPath;

            var entry = Assert.Single(
                DeletionManifest.LoadManifest(DateTime.UtcNow),
                candidate => candidate.OperationId == deletion.OperationId);
            Assert.True(entry.BackupAclTrusted);
            Assert.Equal("S-1-5-32-544", entry.BackupOwnerSid);
            Assert.True(
                RegistryBackupStore.Production.TryValidateForRestore(
                    entry,
                    out var validatedPath,
                    out var validationReason),
                validationReason);
            Assert.Equal(backupPath, validatedPath);

            var restored = DeletionManifest.RestoreFromManifest(
                restoreDate,
                dryRun: false);
            Assert.Equal(1, restored.RegistryRestored);
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
                Assert.Equal("bound-data", key?.GetValue("RestoreMe"));

            var tampered = entry with
            {
                BackupSha256 = new string('0', 64),
            };
            File.WriteAllText(
                currentManifest,
                JsonSerializer.Serialize(tampered) + Environment.NewLine);
            var rejected = DeletionManifest.RestoreFromManifest(
                restoreDate,
                dryRun: true);
            Assert.Equal(0, rejected.RegistryRestored);
            Assert.Equal(1, rejected.Unrecoverable);
            Assert.Contains(
                rejected.Details,
                detail => detail.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteTestKey(subKey);
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                HandleBoundFileOperations.DeleteFileWithinScope(
                    backupPath,
                    DataPaths.RegistryBackups,
                    out _);
            }
        }
    }

    private static bool KeyExists(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key != null;
    }

    private static void TryDeleteTestKey(string subKey)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch { /* test cleanup best-effort */ }
    }

    private static void AssertBackupExists(RegistryDeletionResult result)
    {
        Assert.False(string.IsNullOrWhiteSpace(result.BackupPath));
        Assert.True(File.Exists(result.BackupPath), result.BackupPath);
        var firstLine = File.ReadLines(result.BackupPath!).FirstOrDefault();
        Assert.NotNull(firstLine);
        Assert.Contains("Windows Registry Editor", firstLine);
    }

    private static void AssertManifestContainsBoundTransaction(
        string path,
        string operation,
        RegistryDeletionResult result)
    {
        Assert.True(File.Exists(DeletionManifest.CurrentManifestPath));
        var entries = DeletionManifest.LoadManifest(DateTime.UtcNow);
        var entry = Assert.Single(entries, e =>
            e.Path != null &&
            e.Operation != null &&
            e.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            e.Operation.Equals(operation, StringComparison.Ordinal));
        Assert.Equal(2, entry.SchemaVersion);
        Assert.Equal("Succeeded", entry.Outcome);
        Assert.Equal(result.OperationId, entry.OperationId);
        Assert.Equal(result.BackupPath, entry.BackupPath);
        Assert.Equal(result.BackupSha256, entry.BackupSha256);
        Assert.False(string.IsNullOrWhiteSpace(entry.ObjectIdentity));
        Assert.False(string.IsNullOrWhiteSpace(entry.BackupOwnerSid));
        Assert.False(string.IsNullOrWhiteSpace(entry.BackupDaclSddl));
    }

    private static void DeleteLinkKey(string subKey)
    {
        try
        {
            var open = RegOpenKeyExW(
                HKeyCurrentUser,
                subKey,
                RegOptionOpenLink,
                DeleteAccess,
                out var link);
            if (open != 0) return;
            using (link)
                _ = NtDeleteKey(link.DangerousGetHandle());
        }
        catch { }
    }

    private static readonly IntPtr HKeyCurrentUser =
        new(unchecked((int)0x80000001));
    private const uint RegOptionCreateLink = 0x00000002;
    private const uint RegOptionOpenLink = 0x00000008;
    private const uint RegLink = 6;
    private const uint KeyCreateLink = 0x0020;
    private const uint DeleteAccess = 0x00010000;
    private const uint KeyAllAccess = 0x000F003F;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(
        IntPtr hKey,
        string lpSubKey,
        uint reserved,
        string? lpClass,
        uint options,
        uint samDesired,
        IntPtr securityAttributes,
        out SafeRegistryHandle result,
        out uint disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(
        IntPtr hKey,
        string lpValueName,
        uint reserved,
        uint type,
        byte[] data,
        int dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(
        IntPtr hKey,
        string lpSubKey,
        uint options,
        uint samDesired,
        out SafeRegistryHandle result);

    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(IntPtr keyHandle);
}
