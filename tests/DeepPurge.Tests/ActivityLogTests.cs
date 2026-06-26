using DeepPurge.Core.Diagnostics;
using Xunit;

namespace DeepPurge.Tests;

public class ActivityLogTests
{
    [Fact]
    public void Prune_DoesNotThrow_WhenFileDoesNotExist()
    {
        var ex = Record.Exception(() => ActivityLog.Prune());
        Assert.Null(ex);
    }

    [Fact]
    public void Record_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            ActivityLog.Record("test", "unit test entry", bytesFreed: 0, itemCount: 0, dryRun: true));
        Assert.Null(ex);
    }

    [Fact]
    public void LoadRecent_ReturnsEmptyList_WhenNoEntries()
    {
        var entries = ActivityLog.LoadRecent(10);
        Assert.NotNull(entries);
    }

    [Fact]
    public async Task ConcurrentRecordAndPrune_DoNotDeadlock()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(() =>
                ActivityLog.Record("concurrent", $"entry {idx}", dryRun: true)));
        }
        tasks.Add(Task.Run(() => ActivityLog.Prune()));

        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        var all = Task.WhenAll(tasks);
        var winner = await Task.WhenAny(all, timeout);
        Assert.True(winner == all, "Concurrent Record + Prune deadlocked");
    }
}
