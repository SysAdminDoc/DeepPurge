using DeepPurge.Core.InstallMonitor;
using Xunit;

namespace DeepPurge.Tests;

public class SysmonReaderTests
{
    [Fact]
    public void IsAvailable_does_not_throw()
    {
        var result = SysmonReader.IsAvailable();
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void ExtractRegistryPaths_filters_software_keys()
    {
        var changes = new List<SysmonRegistryChange>
        {
            new("CreateDelete", @"HKLM\SOFTWARE\TestApp\Settings", "", DateTime.UtcNow),
            new("SetValue", @"HKCU\SOFTWARE\TestApp\Config\ValueName", "data", DateTime.UtcNow),
            new("CreateDelete", @"HKLM\SYSTEM\CurrentControlSet\Services\Test", "", DateTime.UtcNow),
        };

        var paths = SysmonReader.ExtractRegistryPaths(changes);

        Assert.Contains(@"HKLM\SOFTWARE\TestApp\Settings", paths);
        Assert.Contains(@"HKCU\SOFTWARE\TestApp\Config", paths);
        Assert.DoesNotContain(@"HKLM\SYSTEM\CurrentControlSet\Services\Test", paths);
    }

    [Fact]
    public void ExtractRegistryPaths_normalizes_kernel_paths()
    {
        var changes = new List<SysmonRegistryChange>
        {
            new("CreateDelete", @"\REGISTRY\MACHINE\SOFTWARE\NewApp", "", DateTime.UtcNow),
        };

        var paths = SysmonReader.ExtractRegistryPaths(changes);

        Assert.Contains(@"HKLM\SOFTWARE\NewApp", paths);
    }

    [Fact]
    public void ExtractRegistryPaths_deduplicates()
    {
        var changes = new List<SysmonRegistryChange>
        {
            new("CreateDelete", @"HKLM\SOFTWARE\Dup", "", DateTime.UtcNow),
            new("SetValue", @"HKLM\SOFTWARE\Dup\Val", "data", DateTime.UtcNow),
            new("CreateDelete", @"HKLM\SOFTWARE\Dup", "", DateTime.UtcNow),
        };

        var paths = SysmonReader.ExtractRegistryPaths(changes);

        Assert.Equal(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), paths.Count);
    }

    [Fact]
    public void ExtractRegistryPaths_handles_empty_input()
    {
        var paths = SysmonReader.ExtractRegistryPaths(new List<SysmonRegistryChange>());
        Assert.Empty(paths);
    }

    [Fact]
    public void ReadRegistryChangesSince_handles_unavailable_sysmon()
    {
        if (SysmonReader.IsAvailable()) return;

        var changes = SysmonReader.ReadRegistryChangesSince(DateTime.UtcNow.AddHours(-1));
        Assert.Empty(changes);
    }
}
