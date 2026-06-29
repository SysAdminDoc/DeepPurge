using DeepPurge.Core.Cleaning;
using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

public class CleanerDefinitionTests
{
    [Fact]
    public void LoadAll_returns_list_without_throwing()
    {
        var rules = CleanerDefinitionRunner.LoadAll();
        Assert.NotNull(rules);
    }

    [Fact]
    public void FilterApplicable_excludes_missing_detect_registry()
    {
        var rule = new CleanerRule
        {
            Name = "test",
            Detect = new List<string> { @"HKLM\SOFTWARE\NonExistentVendor_DeepPurge_Test_12345" }
        };
        Assert.False(CleanerDefinitionRunner.IsApplicable(rule));
    }

    [Fact]
    public void FilterApplicable_excludes_missing_detect_file()
    {
        var rule = new CleanerRule
        {
            Name = "test",
            DetectFile = new List<string> { @"C:\NonExistent_DeepPurge_Test_Path_12345\app.exe" }
        };
        Assert.False(CleanerDefinitionRunner.IsApplicable(rule));
    }

    [Fact]
    public void IsApplicable_rejects_path_traversal_in_detect_file()
    {
        var rule = new CleanerRule
        {
            Name = "test",
            DetectFile = new List<string> { @"%TEMP%\..\..\..\Windows\System32\cmd.exe" }
        };
        Assert.False(CleanerDefinitionRunner.IsApplicable(rule));
    }

    [Fact]
    public void Preview_returns_zero_for_empty_rule()
    {
        var rule = new CleanerRule { Name = "empty" };
        var (size, count) = CleanerDefinitionRunner.Preview(rule);
        Assert.Equal(0, size);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ValidateFile_reports_unknown_fields_as_blocking_schema_errors()
    {
        var file = WriteCleanerJson("""
[
  {
    "Name": "Bad schema",
    "UnknownField": true,
    "Files": [
      { "Path": "%TEMP%", "Pattern": "*.tmp", "Unexpected": "x" }
    ]
  }
]
""");

        try
        {
            var report = CleanerDefinitionRunner.ValidateFile(file);

            Assert.False(report.IsValid);
            Assert.Equal(CleanerRiskLevel.Blocked, report.RiskLevel);
            Assert.Contains(report.Issues, i => i.Field == "UnknownField" && i.Severity == CleanerValidationSeverity.Error);
            Assert.Contains(report.Issues, i => i.Field == "Files[0].Unexpected" && i.Severity == CleanerValidationSeverity.Error);
        }
        finally { TryDelete(file); }
    }

    [Fact]
    public void ValidateFile_accepts_versioned_document_with_provenance()
    {
        var file = WriteCleanerJson("""
{
  "$schema": "https://sysadmindoc.github.io/deeppurge/schemas/cleaner-definition.v1.json",
  "SchemaVersion": 1,
  "Provenance": "unit-test",
  "Rules": [
    {
      "Name": "Versioned",
      "Files": [
        { "Path": "%TEMP%\\DeepPurgeCleanerValidation", "Pattern": "*.tmp", "Recurse": false, "RemoveSelf": false }
      ]
    }
  ]
}
""");

        try
        {
            var report = CleanerDefinitionRunner.ValidateFile(file);

            Assert.True(report.IsValid);
            Assert.Equal(1, report.SchemaVersion);
            Assert.Equal("unit-test", report.Provenance);
            Assert.Equal("v1", report.SchemaDisplay);
        }
        finally { TryDelete(file); }
    }

    [Fact]
    public void ValidateFile_warns_for_legacy_root_array()
    {
        var file = WriteCleanerJson("""
[
  { "Name": "Legacy", "Files": [] }
]
""");

        try
        {
            var report = CleanerDefinitionRunner.ValidateFile(file);

            Assert.True(report.IsValid);
            Assert.Equal(0, report.SchemaVersion);
            Assert.Equal("legacy array", report.SchemaDisplay);
            Assert.Contains(report.Issues, i =>
                i.Severity == CleanerValidationSeverity.Warning &&
                i.Field == "SchemaVersion");
        }
        finally { TryDelete(file); }
    }

    [Fact]
    public void ValidateFile_blocks_unknown_future_schema_version()
    {
        var file = WriteCleanerJson("""
{
  "SchemaVersion": 99,
  "Rules": [
    { "Name": "Future", "Files": [] }
  ]
}
""");

        try
        {
            var report = CleanerDefinitionRunner.ValidateFile(file);

            Assert.False(report.IsValid);
            Assert.Equal(CleanerRiskLevel.Blocked, report.RiskLevel);
            Assert.Contains(report.Issues, i =>
                i.Severity == CleanerValidationSeverity.Error &&
                i.Field == "SchemaVersion" &&
                i.Message.Contains("Unsupported future schema version"));
        }
        finally { TryDelete(file); }
    }

    [Fact]
    public void Embedded_schema_asset_describes_current_document_version()
    {
        var schema = CleanerDefinitionRunner.GetSchemaJson();

        Assert.Contains("\"SchemaVersion\"", schema);
        Assert.Contains("\"const\": 1", schema);
        Assert.Contains(CleanerDefinitionRunner.SchemaId, schema);
    }

    [Fact]
    public void ValidateFile_labels_high_risk_registry_and_remove_self_rules()
    {
        var file = WriteCleanerJson("""
[
  {
    "Name": "High risk but valid",
    "Files": [
      { "Path": "%TEMP%\\DeepPurgeCleanerValidation", "Pattern": "*", "Recurse": true, "RemoveSelf": true }
    ],
    "Registry": [ "HKLM\\SOFTWARE\\DeepPurgeValidation" ]
  }
]
""");

        try
        {
            var report = CleanerDefinitionRunner.ValidateFile(file);

            Assert.True(report.IsValid);
            Assert.Equal(CleanerRiskLevel.High, report.RiskLevel);
            Assert.Contains("HKLM", report.RegistryScopesDisplay);
            Assert.Contains(report.Issues, i => i.Severity == CleanerValidationSeverity.Warning && i.Field == "Registry");
            Assert.Contains(report.Issues, i => i.Severity == CleanerValidationSeverity.Warning && i.Field == "Files.RemoveSelf");
        }
        finally { TryDelete(file); }
    }

    [Fact]
    public void ValidateFile_reports_expanded_targets_and_estimates()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dp_cleaner_validate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.tmp"), "12345");
        var escaped = dir.Replace("\\", "\\\\");
        var file = WriteCleanerJson($$"""
[
  {
    "Name": "Estimate",
    "Files": [
      { "Path": "{{escaped}}", "Pattern": "*.tmp", "Recurse": false, "RemoveSelf": false }
    ]
  }
]
""");

        try
        {
            var report = CleanerDefinitionRunner.ValidateFile(file);

            Assert.True(report.IsValid);
            Assert.Equal(CleanerRiskLevel.Medium, report.RiskLevel);
            Assert.Equal(1, report.EstimatedItems);
            Assert.True(report.EstimatedBytes >= 5);
            Assert.Contains(dir, report.ExpandedTargetsDisplay);
        }
        finally
        {
            TryDelete(file);
            try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
        }
    }

    [Fact]
    public void Execute_blocks_invalid_rules_before_deletion()
    {
        var rule = new CleanerRule
        {
            Name = "Blocked",
            Files = new List<CleanerFileRule>
            {
                new() { Path = @"C:\Windows", Pattern = "*", Recurse = false, RemoveSelf = false }
            }
        };

        var result = CleanerDefinitionRunner.Execute(rule, new DeleteOptions(DryRun: false, SecureDelete: false, UseRecycleBin: false));

        Assert.Equal(0, result.ItemsDeleted);
        Assert.Equal(1, result.ItemsSkipped);
    }

    private static string WriteCleanerJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp_cleaner_{Guid.NewGuid():N}.cleaner.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* test cleanup */ }
    }
}
