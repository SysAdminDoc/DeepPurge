using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

public sealed class DeletionExecutorTests
{
    [Fact]
    public void Preview_does_not_mutate_or_write_manifest()
    {
        var root = CreateTempDirectory();
        var file = Path.Combine(root, "preview.txt");
        var manifest = Path.Combine(root, "manifest.jsonl");
        File.WriteAllText(file, "preview");

        try
        {
            using var scope = DeletionManifest.UseManifestPathForTests(manifest);
            var result = new DeletionExecutor().Execute(
                new DeletionRequest(file, ExpectedSizeBytes: 7, Operation: "test-preview"),
                DeleteOptions.Preview);

            Assert.Equal(DeletionOutcomeKind.Preview, result.Outcome);
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(manifest));

            var summary = new DeletionBatchResult(new[] { result }, DryRun: true).Summary;
            Assert.Equal(1, summary.ItemsDeleted);
            Assert.Equal(0, summary.ItemsConfirmed);
            Assert.Equal(7, summary.BytesPlanned);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Permanent_success_is_confirmed_and_not_recoverable()
    {
        var root = CreateTempDirectory();
        var file = Path.Combine(root, "permanent.txt");
        var manifest = Path.Combine(root, "manifest.jsonl");
        File.WriteAllText(file, "permanent");

        try
        {
            using var scope = DeletionManifest.UseManifestPathForTests(manifest);
            var result = new DeletionExecutor().Execute(
                new DeletionRequest(file, Operation: "test-permanent"),
                new DeleteOptions(UseRecycleBin: false));

            Assert.Equal(DeletionOutcomeKind.PermanentlyDeleted, result.Outcome);
            Assert.True(result.IsConfirmed);
            Assert.False(result.Recoverable);
            Assert.False(File.Exists(file));

            var entries = DeletionManifest.LoadManifest(DateTime.UtcNow);
            var entry = Assert.Single(entries);
            Assert.Equal("test-permanent", entry.Operation);
            Assert.Equal("PermanentlyDeleted", entry.Outcome);
            Assert.False(entry.Recoverable);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Missing_and_cancelled_items_are_not_counted_as_deleted()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"deeppurge-missing-{Guid.NewGuid():N}.tmp");
        var request = new DeletionRequest(missing, Operation: "test-missing");
        var executor = new DeletionExecutor();

        var skipped = executor.Execute(request, new DeleteOptions(UseRecycleBin: false));
        var cancelled = executor.Execute(
            request,
            new DeleteOptions(UseRecycleBin: false),
            new CancellationToken(canceled: true));

        Assert.Equal(DeletionOutcomeKind.Skipped, skipped.Outcome);
        Assert.Equal(DeletionOutcomeKind.Cancelled, cancelled.Outcome);
        var summary = DeleteSummary.FromResults(new[] { skipped, cancelled }, dryRun: false);
        Assert.Equal(0, summary.ItemsDeleted);
        Assert.Equal(2, summary.ItemsSkipped);
        Assert.True(summary.HasFailures);
    }

    [Fact]
    public void Queued_is_available_but_is_not_a_success()
    {
        var queued = DeletionResult.Queued(
            @"C:\Temp\queued.tmp",
            operation: "test-queue");

        Assert.Equal(DeletionOutcomeKind.Queued, queued.Outcome);
        Assert.False(queued.IsConfirmed);
        Assert.False(queued.IsPreview);
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deeppurge-executor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* test cleanup is best effort */ }
    }
}
