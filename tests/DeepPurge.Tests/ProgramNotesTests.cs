using DeepPurge.Core.App;
using Xunit;

namespace DeepPurge.Tests;

public class ProgramNotesTests
{
    [Fact]
    public void ProgramNotes_round_trips_through_settings()
    {
        var settings = new AppSettings();
        settings.ProgramNotes["Test App"] = "keep for compliance";
        settings.ProgramNotes["Old Tool"] = "remove after migration";

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.ProgramNotes.Count);
        Assert.Equal("keep for compliance", loaded.ProgramNotes["Test App"]);
        Assert.Equal("remove after migration", loaded.ProgramNotes["Old Tool"]);
    }

    [Fact]
    public void CookieWhitelist_round_trips_through_settings()
    {
        var settings = new AppSettings();
        settings.CookieWhitelist.Add("github.com");
        settings.CookieWhitelist.Add("google.com");

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.CookieWhitelist.Count);
        Assert.Contains("github.com", loaded.CookieWhitelist);
        Assert.Contains("google.com", loaded.CookieWhitelist);
    }

    [Fact]
    public void Empty_settings_have_empty_collections()
    {
        var settings = new AppSettings();
        Assert.Empty(settings.ProgramNotes);
        Assert.Empty(settings.CookieWhitelist);
        Assert.Empty(settings.ExcludedPaths);
    }
}
