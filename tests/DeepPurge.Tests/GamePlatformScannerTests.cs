using DeepPurge.Core.Packages;
using Xunit;

namespace DeepPurge.Tests;

public class GamePlatformScannerTests
{
    [Fact]
    public void ScanAll_returns_list_without_throwing()
    {
        var games = GamePlatformScanner.ScanAll();
        Assert.NotNull(games);
    }

    [Fact]
    public void InjectIntoPrograms_skips_duplicates()
    {
        var programs = new List<DeepPurge.Core.Models.InstalledProgram>
        {
            new() { DisplayName = "TestGame" }
        };
        var games = new List<GameEntry>
        {
            new("TestGame", "Steam", @"C:\Games\TestGame", "1.0"),
            new("NewGame", "Epic", @"C:\Games\NewGame", "2.0"),
        };
        GamePlatformScanner.InjectIntoPrograms(programs, games);
        Assert.Equal(2, programs.Count);
        Assert.Equal("NewGame", programs[1].DisplayName);
    }
}
