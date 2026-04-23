using DeepPurge.Core.Startup;
using Xunit;

namespace DeepPurge.Tests;

public class StartupImpactTests
{
    [Theory]
    [InlineData(0L, 0L, StartupImpact.None)]
    [InlineData(50_000L, 100L, StartupImpact.Low)]
    [InlineData(400_000L, 100L, StartupImpact.Medium)]    // disk > 300 KB
    [InlineData(50_000L, 400L, StartupImpact.Medium)]     // cpu  > 300 ms
    [InlineData(5_000_000L, 100L, StartupImpact.High)]    // disk > 3 MB
    [InlineData(50_000L, 1500L, StartupImpact.High)]      // cpu  > 1000 ms
    [InlineData(10_000_000L, 5000L, StartupImpact.High)]  // well over both
    public void Classify_matches_MS_thresholds(long disk, long cpu, StartupImpact expected)
    {
        Assert.Equal(expected, StartupImpactCalculator.Classify(disk, cpu));
    }
}
