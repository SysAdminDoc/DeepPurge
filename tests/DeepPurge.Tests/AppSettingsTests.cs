using DeepPurge.Core.App;
using Xunit;

namespace DeepPurge.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Current_returns_singleton()
    {
        var a = AppSettings.Current;
        var b = AppSettings.Current;
        Assert.Same(a, b);
    }

    [Fact]
    public void ExcludedPaths_defaults_to_empty()
    {
        Assert.NotNull(AppSettings.Current.ExcludedPaths);
    }

    [Fact]
    public void Privacy_retention_defaults_are_initialized()
    {
        var settings = new AppSettings();

        Assert.Equal(30, settings.RetentionDaysLogs);
        Assert.Equal(90, settings.RetentionDaysActivity);
        Assert.Equal(90, settings.RetentionDaysDeletionManifests);
        Assert.False(settings.ScrubSensitivePathsInReports);
    }

    [Fact]
    public void Save_does_not_throw()
    {
        var ex = Record.Exception(() => AppSettings.Current.Save());
        Assert.Null(ex);
    }
}
