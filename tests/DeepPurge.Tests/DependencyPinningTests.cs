using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace DeepPurge.Tests;

public sealed class DependencyPinningTests
{
    [Fact]
    public void Repository_pins_sdk_restore_mode_and_package_sources()
    {
        var root = FindRepoRoot();
        using var globalJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        var sdk = globalJson.RootElement.GetProperty("sdk");

        Assert.Equal("10.0.302", sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());

        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", props);
        Assert.Contains("<RestoreLockedMode>true</RestoreLockedMode>", props);

        var nuget = XDocument.Load(Path.Combine(root, "NuGet.Config"));
        var mappings = nuget.Descendants("package")
            .Select(package => (string?)package.Attribute("pattern"))
            .Where(pattern => pattern is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[]
        {
            "Microsoft.*", "System.*", "Newtonsoft.*", "SQLite", "SQLitePCLRaw.*",
            "SourceGear.*", "CommunityToolkit.*", "xunit.*", "Verify", "Verify.*"
        })
        {
            Assert.Contains(required, mappings);
        }
    }

    [Fact]
    public void Project_references_pin_maintenance_package_versions()
    {
        var root = FindRepoRoot();
        var core = ReadPackageVersions(Path.Combine(root, "src", "DeepPurge.Core", "DeepPurge.Core.csproj"));
        var tests = ReadPackageVersions(Path.Combine(root, "tests", "DeepPurge.Tests", "DeepPurge.Tests.csproj"));

        Assert.Equal("10.0.10", core["Microsoft.Data.Sqlite"]);
        Assert.Equal("3.0.5", core["SQLitePCLRaw.bundle_e_sqlite3"]);
        Assert.Equal("10.0.10", core["System.Management"]);
        Assert.Equal("10.0.10", core["System.ServiceProcess.ServiceController"]);
        Assert.Equal("10.0.10", core["System.IO.Hashing"]);
        Assert.Equal("10.0.10", tests["Microsoft.Data.Sqlite"]);
    }

    [Fact]
    public void Lockfiles_contain_the_pinned_sqlite_engine_and_no_old_direct_graph()
    {
        var root = FindRepoRoot();
        var lockfiles = new[]
        {
            Path.Combine(root, "src", "DeepPurge.Core", "packages.lock.json"),
            Path.Combine(root, "src", "DeepPurge.App", "packages.lock.json"),
            Path.Combine(root, "src", "DeepPurge.Cli", "packages.lock.json"),
            Path.Combine(root, "tests", "DeepPurge.Tests", "packages.lock.json")
        };

        foreach (var path in lockfiles)
        {
            var lockfile = File.ReadAllText(path);
            Assert.Contains("\"resolved\": \"3.53.4\"", lockfile);
            Assert.Contains("\"resolved\": \"3.0.5\"", lockfile);
            Assert.DoesNotContain("\"resolved\": \"3.0.3\"", lockfile);
            Assert.DoesNotContain("\"resolved\": \"3.50.4.5\"", lockfile);
        }
    }

    [Fact]
    public void Build_script_requires_pinned_restore_and_never_bootstraps_an_sdk()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "Build.ps1"));

        Assert.Contains("10.0.302", script);
        Assert.Contains("--locked-mode", script);
        Assert.Contains("--ignore-failed-sources", script);
        Assert.Contains("--runtime", script);
        Assert.Contains("win-x64", script);
        Assert.Contains("--no-restore", script);
        Assert.Contains("[switch]$SkipTests", script);
        Assert.Contains("-AdvisoryOnly", script);
        Assert.DoesNotContain("dotnet-install.ps1", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadPackageVersions(string path)
    {
        var project = XDocument.Load(path);
        return project.Descendants("PackageReference")
            .Where(reference => reference.Attribute("Include") is not null)
            .ToDictionary(
                reference => (string)reference.Attribute("Include")!,
                reference => (string)reference.Attribute("Version")!);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeepPurge.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DeepPurge.sln from test output directory.");
    }
}
