using System.Text.Json;
using DeepPurge.Core.Browsers;
using Xunit;

namespace DeepPurge.Tests;

public sealed class BrowserExtensionRemovalTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"DeepPurge_Firefox_{Guid.NewGuid():N}");

    [Fact]
    public void Missing_Firefox_Package_Is_Nonremovable_And_Never_Falls_Back_To_Profile()
    {
        var profile = CreateProfile();
        WriteAddon(profile, "stale-addon@example.test");

        var extension = Assert.Single(
            BrowserExtensionScanner.ScanFirefoxProfile(profile));

        Assert.False(extension.IsRemovable);
        Assert.Empty(extension.Path);
        Assert.Contains("stale", extension.RemovalReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(BrowserExtensionScanner.TryRemoveExtension(extension, out _));
        AssertProfileSentinels(profile);
    }

    [Fact]
    public void Exact_Firefox_Directory_Package_Is_Removed_Without_Profile_Data_Loss()
    {
        var profile = CreateProfile();
        const string id = "directory-addon@example.test";
        var package = Path.Combine(profile, "extensions", id);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
        WriteAddon(profile, id);

        var extension = Assert.Single(
            BrowserExtensionScanner.ScanFirefoxProfile(profile));

        Assert.True(extension.IsRemovable, extension.RemovalReason);
        Assert.Equal(ExtensionPackageKind.Directory, extension.PackageKind);
        Assert.True(BrowserExtensionScanner.TryRemoveExtension(extension, out var reason), reason);
        Assert.False(Directory.Exists(package));
        AssertProfileSentinels(profile);
    }

    [Fact]
    public void Exact_Firefox_Xpi_Package_Is_Removed_Without_Profile_Data_Loss()
    {
        var profile = CreateProfile();
        const string id = "{2c39a8de-90d4-4d6a-a675-d1a2e4ec2ac0}";
        var package = Path.Combine(profile, "extensions", id + ".xpi");
        File.WriteAllText(package, "xpi fixture");
        WriteAddon(profile, id);

        var extension = Assert.Single(
            BrowserExtensionScanner.ScanFirefoxProfile(profile));

        Assert.True(extension.IsRemovable, extension.RemovalReason);
        Assert.Equal(ExtensionPackageKind.Xpi, extension.PackageKind);
        Assert.True(BrowserExtensionScanner.TryRemoveExtension(extension, out var reason), reason);
        Assert.False(File.Exists(package));
        AssertProfileSentinels(profile);
    }

    [Theory]
    [InlineData(@"..\..\outside")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"nested/addon")]
    [InlineData("addon.")]
    public void Rooted_Traversal_And_Aliased_Ids_Are_Nonremovable(string id)
    {
        var profile = CreateProfile();
        WriteAddon(profile, id);

        var extension = Assert.Single(
            BrowserExtensionScanner.ScanFirefoxProfile(profile));

        Assert.False(extension.IsRemovable);
        Assert.Empty(extension.Path);
        Assert.False(BrowserExtensionScanner.TryRemoveExtension(extension, out _));
        AssertProfileSentinels(profile);
    }

    [Fact]
    public void BuiltIn_And_System_Extensions_Remain_Protected_Even_If_A_Profile_Package_Exists()
    {
        var profile = CreateProfile();
        const string id = "system-addon@example.test";
        var package = Path.Combine(profile, "extensions", id + ".xpi");
        File.WriteAllText(package, "system fixture");
        WriteAddon(
            profile,
            id,
            isSystem: true,
            location: "app-system-addons");

        var extension = Assert.Single(
            BrowserExtensionScanner.ScanFirefoxProfile(profile));

        Assert.False(extension.IsRemovable);
        Assert.Contains("system", extension.RemovalReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(BrowserExtensionScanner.TryRemoveExtension(extension, out _));
        Assert.True(File.Exists(package));
        AssertProfileSentinels(profile);
    }

    [Fact]
    public void Stored_Package_Path_Drift_Aborts_Removal()
    {
        var profile = CreateProfile();
        const string id = "drift-addon@example.test";
        var package = Path.Combine(profile, "extensions", id + ".xpi");
        File.WriteAllText(package, "drift fixture");
        WriteAddon(profile, id);
        var extension = Assert.Single(
            BrowserExtensionScanner.ScanFirefoxProfile(profile));
        extension.Path = profile;

        Assert.False(
            BrowserExtensionScanner.TryRemoveExtension(
                extension,
                out var reason));
        Assert.Contains("changed", reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(package));
        AssertProfileSentinels(profile);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch { }
    }

    private string CreateProfile()
    {
        var profile = Path.Combine(_testRoot, Guid.NewGuid().ToString("N") + ".default");
        Directory.CreateDirectory(Path.Combine(profile, "extensions"));
        File.WriteAllText(Path.Combine(profile, "places.sqlite"), "bookmarks");
        File.WriteAllText(Path.Combine(profile, "logins.json"), "password metadata");
        File.WriteAllText(Path.Combine(profile, "key4.db"), "password key");
        File.WriteAllText(Path.Combine(profile, "cookies.sqlite"), "cookies");
        return profile;
    }

    private static void WriteAddon(
        string profile,
        string id,
        bool isSystem = false,
        string location = "")
    {
        var addon = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = "Fixture extension",
            ["type"] = "extension",
            ["version"] = "1.0.0",
            ["active"] = true,
            ["isSystem"] = isSystem,
            ["location"] = location,
        };
        var payload = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["addons"] = new[] { addon },
            });
        File.WriteAllText(Path.Combine(profile, "addons.json"), payload);
    }

    private static void AssertProfileSentinels(string profile)
    {
        Assert.Equal("bookmarks", File.ReadAllText(Path.Combine(profile, "places.sqlite")));
        Assert.Equal("password metadata", File.ReadAllText(Path.Combine(profile, "logins.json")));
        Assert.Equal("password key", File.ReadAllText(Path.Combine(profile, "key4.db")));
        Assert.Equal("cookies", File.ReadAllText(Path.Combine(profile, "cookies.sqlite")));
    }
}
