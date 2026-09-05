using System.Text.Json;
using Xunit;

namespace DeepPurge.Tests;

public class ReleaseReadinessTests
{
    private const string GuiHash = "7C89D435C42314BC4098E4B92C6D50D385641BAFE359627A8C1BBA994BAE0A5B";
    private const string CliHash = "EDA05DC47249DCE9A60852B1B87D1C66CD2D682058C96E527CF75BC634006A0D";

    [Fact]
    public void Build_script_exposes_release_readiness_validation()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "Build.ps1"));

        Assert.Contains("[switch]$ValidateRelease", script);
        Assert.Contains("[switch]$ValidateReleaseOnly", script);
        Assert.Contains("[switch]$AuditDependenciesOnly", script);
        Assert.Contains("[string]$ReleaseChecksumsPath", script);
        Assert.Contains("function Write-Sha256Sums", script);
        Assert.Contains("function Invoke-ReleaseReadinessValidation", script);
        Assert.Contains("function Invoke-DependencyAuditValidation", script);
        Assert.Contains("function Invoke-DocumentationContractValidation", script);
        Assert.Contains("--list-tests", script);
        Assert.Contains("requestedExecutionLevel", script);
        Assert.Contains("panelHealth", script);
        Assert.Contains("panelSlimming", script);
        Assert.Contains("SHA256SUMS.txt", script);
        Assert.Contains("packaging/scoop/deeppurge.json:architecture.$($arch.Name).hash[$i]", script);
    }

    [Fact]
    public void Build_script_audits_dependencies_project_by_project()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "Build.ps1"));

        Assert.Contains("DeepPurge.Core", script);
        Assert.Contains("DeepPurge.App", script);
        Assert.Contains("DeepPurge.Cli", script);
        Assert.Contains("DeepPurge.Tests", script);
        Assert.Contains("\"list\", $project.Path, \"package\", \"--outdated\", \"--no-restore\"", script);
        Assert.Contains("\"list\", $project.Path, \"package\", \"--vulnerable\", \"--include-transitive\", \"--no-restore\"", script);
        Assert.Contains("if ($AuditDependenciesOnly)", script);
        Assert.Contains("Invoke-DependencyAuditValidation", script);
        Assert.DoesNotContain("\"list\", $SolutionFile, \"package\", \"--outdated\"", script);
        Assert.DoesNotContain("\"list\", $SolutionFile, \"package\", \"--vulnerable\"", script);
    }

    [Fact]
    public void Scoop_manifest_references_only_published_release_assets()
    {
        var root = FindRepoRoot();
        var scoop = File.ReadAllText(Path.Combine(root, "packaging", "scoop", "deeppurge.json"));

        Assert.DoesNotContain("PLACEHOLDER", scoop);
        Assert.DoesNotContain("DeepPurge-arm64", scoop);
        Assert.DoesNotContain("DeepPurgeCli-arm64", scoop);

        using var doc = JsonDocument.Parse(scoop);
        var rootElement = doc.RootElement;
        Assert.Equal("0.9.2", rootElement.GetProperty("version").GetString());
        var x64 = rootElement.GetProperty("architecture").GetProperty("64bit");
        Assert.Equal(GuiHash, x64.GetProperty("hash")[0].GetString());
        Assert.Equal(CliHash, x64.GetProperty("hash")[1].GetString());
        Assert.Equal(
            "https://github.com/SysAdminDoc/DeepPurge/releases/download/v$version/SHA256SUMS.txt",
            rootElement.GetProperty("autoupdate").GetProperty("hash").GetProperty("url").GetString());
    }

    [Fact]
    public void Packaging_docs_point_operators_to_release_validator()
    {
        var root = FindRepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var packagingReadme = File.ReadAllText(Path.Combine(root, "packaging", "README.md"));

        Assert.Contains("Build.ps1 -ValidateReleaseOnly -ReleaseChecksumsPath", readme);
        Assert.Contains("Build.ps1 -ValidateReleaseOnly -ReleaseChecksumsPath", packagingReadme);
        Assert.Contains("Build.ps1 -AuditDependenciesOnly", readme);
        Assert.Contains("Build.ps1 -AuditDependenciesOnly", packagingReadme);
        Assert.Contains("project-level NuGet dependency audit", readme);
        Assert.Contains("project-level NuGet dependency audit", packagingReadme);
        Assert.Contains("build\\SHA256SUMS.txt", readme);
        Assert.Contains("build\\SHA256SUMS.txt", packagingReadme);
    }

    [Fact]
    public void Doc_test_counts_are_consistent()
    {
        var root = FindRepoRoot();
        var pattern = new System.Text.RegularExpressions.Regex(@"\ball\s+(\d+)\s+tests\b|\b(\d+)\s+tests?\s+pass|\b(\d+)\s+cases\b");

        var docs = new[] { "README.md", "CONTRIBUTING.md", "ARCHITECTURE.md" };
        var counts = new Dictionary<string, int>();

        foreach (var doc in docs)
        {
            var content = File.ReadAllText(Path.Combine(root, doc));
            var match = pattern.Match(content);
            if (!match.Success) continue;

            var numStr = match.Groups[1].Success ? match.Groups[1].Value
                       : match.Groups[2].Success ? match.Groups[2].Value
                       : match.Groups[3].Value;
            if (int.TryParse(numStr, out var n))
                counts[doc] = n;
        }

        Assert.True(counts.Count >= 2,
            $"Expected at least 2 docs with test counts but found {counts.Count}: {string.Join(", ", counts.Keys)}");

        var distinct = counts.Values.Distinct().ToList();
        Assert.True(distinct.Count == 1,
            $"Test count drift across docs: {string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"))}. " +
            $"All must agree on the same number.");
        Assert.Equal(459, distinct[0]);
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
