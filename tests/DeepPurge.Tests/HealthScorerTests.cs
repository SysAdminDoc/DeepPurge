using DeepPurge.Core.Diagnostics;
using Xunit;

namespace DeepPurge.Tests;

public class HealthScorerTests
{
    [Fact]
    public void Assess_returns_valid_report()
    {
        var report = HealthScorer.Assess();
        Assert.NotNull(report);
        Assert.InRange(report.OverallScore, 0, 100);
        Assert.Contains(report.Grade, new[] { "A", "B", "C", "D", "F" });
        Assert.Equal(4, report.Categories.Count);
    }

    [Fact]
    public void Assess_includes_command_targets()
    {
        var report = HealthScorer.Assess();
        foreach (var cat in report.Categories)
        {
            Assert.False(string.IsNullOrEmpty(cat.CommandTarget),
                $"Category '{cat.Category}' is missing a CommandTarget");
        }
    }

    [Fact]
    public void Assess_records_trend()
    {
        var report = HealthScorer.Assess();
        Assert.Contains(report.Trend, new[] { HealthTrend.Improved, HealthTrend.Worsened, HealthTrend.Stable, HealthTrend.Unknown });
    }

    [Fact]
    public void HealthHistory_records_and_loads()
    {
        var report = HealthScorer.Assess();
        var history = HealthHistory.LoadRecent(5);
        Assert.True(history.Count > 0);
        Assert.Equal(report.OverallScore, history[0].OverallScore);
    }

    [Theory]
    [InlineData(100, "A")]
    [InlineData(90, "A")]
    [InlineData(89, "B")]
    [InlineData(75, "B")]
    [InlineData(74, "C")]
    [InlineData(60, "C")]
    [InlineData(59, "D")]
    [InlineData(40, "D")]
    [InlineData(39, "F")]
    [InlineData(0, "F")]
    public void Grade_thresholds_are_correct(int score, string expected)
    {
        var grade = score switch
        {
            >= 90 => "A",
            >= 75 => "B",
            >= 60 => "C",
            >= 40 => "D",
            _ => "F",
        };
        Assert.Equal(expected, grade);
    }
}
