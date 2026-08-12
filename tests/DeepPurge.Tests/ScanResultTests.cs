using DeepPurge.Core.Diagnostics;
using Xunit;

namespace DeepPurge.Tests;

public sealed class ScanResultTests
{
    [Fact]
    public void Partial_result_retains_items_and_source_diagnostics()
    {
        var result = ScanResult<string>.Create(
            "test-scan",
            new[] { "successful item" },
            new[] { new ScanIssue("secondary-source", "The source was unavailable.") },
            new[] { "A non-fatal warning." },
            TimeSpan.FromMilliseconds(12));

        Assert.Equal(ScanCompletionStatus.Partial, result.Status);
        Assert.True(result.IsDegraded);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "successful item" }, result.Items);
        Assert.Single(result.FailedSources);
        Assert.Single(result.Warnings);
        Assert.Equal("Partial", result.StatusDisplay);
    }

    [Theory]
    [InlineData(ScanCompletionStatus.Clean, "Clean")]
    [InlineData(ScanCompletionStatus.Partial, "Partial")]
    [InlineData(ScanCompletionStatus.Failed, "Failed")]
    [InlineData(ScanCompletionStatus.TimedOut, "Timed out")]
    [InlineData(ScanCompletionStatus.Cancelled, "Cancelled")]
    public void Status_display_is_explicit(
        ScanCompletionStatus status,
        string expected)
    {
        var result = ScanResult<int>.Create(
            "test-scan",
            Array.Empty<int>(),
            Array.Empty<ScanIssue>(),
            Array.Empty<string>(),
            TimeSpan.Zero,
            status,
            status == ScanCompletionStatus.Cancelled);

        Assert.Equal(expected, result.StatusDisplay);
    }
}
