using DeepPurge.Core.App;
using Xunit;

namespace DeepPurge.Tests;

public class DataPathsTests
{
    [Fact]
    public void Root_is_non_empty_and_exists()
    {
        Assert.False(string.IsNullOrWhiteSpace(DataPaths.Root));
        Assert.True(Directory.Exists(DataPaths.Root));
    }

    [Fact]
    public void Subdirectories_are_auto_created()
    {
        // Simple behavioural check — exercising the property should ensure
        // the directory exists on disk.
        _ = DataPaths.Logs;
        _ = DataPaths.Backups;
        _ = DataPaths.Snapshots;
        _ = DataPaths.Cleaners;
        _ = DataPaths.Config;

        Assert.True(Directory.Exists(DataPaths.Logs));
        Assert.True(Directory.Exists(DataPaths.Backups));
        Assert.True(Directory.Exists(DataPaths.Snapshots));
        Assert.True(Directory.Exists(DataPaths.Cleaners));
        Assert.True(Directory.Exists(DataPaths.Config));
    }

    [Fact]
    public void Settings_and_theme_files_are_paths_under_Config()
    {
        Assert.StartsWith(DataPaths.Config, DataPaths.ThemeFile);
        Assert.StartsWith(DataPaths.Config, DataPaths.SettingsFile);
    }
}
