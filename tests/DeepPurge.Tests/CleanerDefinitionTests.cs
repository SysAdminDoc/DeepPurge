using DeepPurge.Core.Cleaning;
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
}
