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
    public void NormalizeRegistryPath_preserves_hku_sid_and_hkcu_hive()
    {
        Assert.Equal(
            @"HKU\S-1-5-21-100\Software\App",
            SysmonReader.NormalizeRegistryPath(
                @"\REGISTRY\USER\S-1-5-21-100\Software\App"));
        Assert.Equal(
            @"HKCU\Software\App",
            SysmonReader.NormalizeRegistryPath(
                @"HKEY_CURRENT_USER\Software\App"));
    }

    [Fact]
    public void CorrelateRegistryChanges_requires_installer_process_tree()
    {
        var start = DateTime.UtcNow.AddMinutes(-1);
        var end = DateTime.UtcNow.AddMinutes(1);
        var rootGuid = "{root}";
        var childGuid = "{child}";
        var events = new List<SysmonEventData>
        {
            new(
                1,
                start,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ProcessId"] = "123",
                    ["ProcessGuid"] = rootGuid,
                    ["Image"] = @"C:\setup.exe",
                }),
            new(
                1,
                start.AddSeconds(1),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ProcessId"] = "456",
                    ["ProcessGuid"] = childGuid,
                    ["ParentProcessGuid"] = rootGuid,
                    ["Image"] = @"C:\child.exe",
                }),
            new(
                13,
                start.AddSeconds(2),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ProcessId"] = "456",
                    ["ProcessGuid"] = childGuid,
                    ["EventType"] = "SetValue",
                    ["TargetObject"] =
                        @"\REGISTRY\USER\S-1-5-21-100\Software\App\Value",
                }),
        };

        var changes = SysmonReader.CorrelateRegistryChanges(
            events,
            123,
            @"C:\setup.exe",
            start,
            end,
            out var correlated);

        Assert.True(correlated);
        var change = Assert.Single(changes);
        Assert.Equal(
            @"HKU\S-1-5-21-100\Software\App",
            Assert.Single(SysmonReader.ExtractRegistryPaths(changes)));
    }

    [Fact]
    public void ReadRegistryChangesSince_handles_unavailable_sysmon()
    {
        if (SysmonReader.IsAvailable()) return;

        var changes = SysmonReader.ReadRegistryChangesSince(DateTime.UtcNow.AddHours(-1));
        Assert.Empty(changes);
    }
}
