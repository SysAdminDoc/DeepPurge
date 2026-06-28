using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Registry;
using global::Microsoft.Win32;
using Xunit;

namespace DeepPurge.Tests;

public class RegistryDeletionTests
{
    private const string TestRoot = @"Software\DeepPurgeTests";

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
            var result = RegistryDeletion.DeleteKeyTree(fullPath, "test-regkey-delete");

            Assert.Equal(RegistryDeletionStatus.Deleted, result.Status);
            Assert.False(KeyExists(subKey));
            AssertBackupExists(result);
            AssertManifestContains(fullPath, "test-regkey-delete");
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
            var result = RegistryDeletion.DeleteValue(fullValuePath, "test-regvalue-delete");

            Assert.Equal(RegistryDeletionStatus.Deleted, result.Status);
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            Assert.NotNull(key);
            Assert.Null(key.GetValue("DeleteMe"));
            Assert.Equal("data", key.GetValue("KeepMe"));
            AssertBackupExists(result);
            AssertManifestContains(fullValuePath, "test-regvalue-delete");
        }
        finally
        {
            TryDeleteTestKey(subKey);
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

    private static void AssertManifestContains(string path, string operation)
    {
        Assert.True(File.Exists(DeletionManifest.CurrentManifestPath));
        var entries = DeletionManifest.LoadManifest(DateTime.UtcNow);
        Assert.Contains(entries, e =>
            e.Path != null &&
            e.Operation != null &&
            e.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            e.Operation.Equals(operation, StringComparison.Ordinal));
    }
}
