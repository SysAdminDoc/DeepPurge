using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Privacy;
using DeepPurge.Core.Safety;
using Xunit;

namespace DeepPurge.Tests;

public class CleanupFailureReasonTests
{
    [Fact]
    public void Junk_dry_run_reports_missing_files_with_redacted_paths()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "DeepPurgeMissingTests",
            Guid.NewGuid().ToString("N"),
            "missing.tmp");
        var category = new JunkCategory
        {
            Name = "test",
            Files = { new JunkFile { Path = missingPath, Size = 42 } },
        };

        var summary = JunkFilesCleaner.DeleteJunkSafe(
            new[] { category },
            DeleteOptions.Preview,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.True(summary.DryRun);
        Assert.Equal(0, summary.ItemsDeleted);
        Assert.Equal(1, summary.ItemsSkipped);
        var reason = Assert.Single(summary.SkippedReasons);
        Assert.Contains("Missing:", reason);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Junk_cleanup_reports_denied_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeepPurgeDeniedTests", Guid.NewGuid().ToString("N"));
        var deniedPath = Path.Combine(dir, "denied.tmp");
        Directory.CreateDirectory(dir);
        File.WriteAllText(deniedPath, "locked");
        File.SetAttributes(deniedPath, FileAttributes.ReadOnly);

        try
        {
            var category = new JunkCategory
            {
                Name = "test",
                Files = { new JunkFile { Path = deniedPath, Size = 6 } },
            };

            var summary = JunkFilesCleaner.DeleteJunkSafe(
                new[] { category },
                DeleteOptions.Default,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, summary.ItemsDeleted);
            Assert.Equal(1, summary.ItemsSkipped);
            var reason = Assert.Single(summary.SkippedReasons);
            Assert.Contains("Delete failed:", reason);
            Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\'), reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (File.Exists(deniedPath))
                    File.SetAttributes(deniedPath, FileAttributes.Normal);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Evidence_cleanup_reports_command_failures()
    {
        var category = new TraceCategory
        {
            Name = "test",
            Items =
            {
                new TraceItem
                {
                    IsCommand = true,
                    Command = "deeppurge_missing_command_for_tests.exe",
                },
            },
        };

        var summary = EvidenceRemover.CleanTracesSafe(
            new[] { category },
            DeleteOptions.Default,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, summary.ItemsDeleted);
        Assert.Equal(1, summary.ItemsSkipped);
        var reason = Assert.Single(summary.SkippedReasons);
        Assert.Contains("Command failed", reason);
        Assert.Contains("deeppurge_missing_command_for_tests.exe", reason);
    }
}
