using DeepPurge.Core.Drivers;
using Xunit;

namespace DeepPurge.Tests;

public sealed class DriverRollbackTests
{
    [Fact]
    public async Task Export_failure_blocks_delete_and_leaves_no_rollback_artifact()
    {
        using var temp = new TemporaryDirectory();
        var tool = new FakeDriverTool
        {
            Handler = (_, _) => Task.FromResult(new DriverToolResult(
                Started: true,
                ExitCode: 1,
                Output: "export failed")),
        };
        var scanner = CreateScanner(temp.Path, tool);

        var result = await scanner.DeleteAsync(Package());

        Assert.Equal(DriverMutationOutcome.Failed, result.Outcome);
        Assert.Null(result.Artifact);
        Assert.Single(tool.Calls);
        Assert.Equal("/export-driver", tool.Calls[0][0]);
        Assert.DoesNotContain(tool.Calls, call => call[0] == "/delete-driver");
        Assert.Equal(DriverMutationOutcome.Failed, scanner.OperationLedger.LoadLatest(result.OperationId)!.Outcome);
    }

    [Fact]
    public async Task Successful_delete_records_hashes_and_rollback_reinstalls_inf()
    {
        using var temp = new TemporaryDirectory();
        var tool = new FakeDriverTool
        {
            Handler = (args, _) =>
            {
                if (args[0] == "/export-driver")
                {
                    Directory.CreateDirectory(args[2]);
                    File.WriteAllText(Path.Combine(args[2], "acme.inf"), "[Version]\nSignature=\"$Windows NT$\"\n");
                    File.WriteAllText(Path.Combine(args[2], "acme.sys"), "driver payload");
                    return Task.FromResult(Success("Driver package exported."));
                }
                return Task.FromResult(Success("Driver package installed."));
            },
        };
        var scanner = CreateScanner(temp.Path, tool);

        var deleted = await scanner.DeleteAsync(Package());
        var deletedEntry = scanner.OperationLedger.LoadLatest(deleted.OperationId);
        var restored = await scanner.RollbackAsync(deleted.OperationId);

        Assert.Equal(DriverMutationOutcome.Deleted, deleted.Outcome);
        Assert.NotNull(deleted.Artifact);
        Assert.NotEmpty(deleted.PackageSha256);
        Assert.Equal(2, deleted.Artifact!.Files.Count);
        Assert.Equal(DriverMutationOutcome.Deleted, deletedEntry!.Outcome);
        Assert.Equal(DriverMutationOutcome.Restored, restored.Outcome);
        Assert.Contains(tool.Calls, call => call[0] == "/add-driver");
        var add = tool.Calls.Last(call => call[0] == "/add-driver");
        Assert.EndsWith("acme.inf", add[1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/install", add[2]);
    }

    [Fact]
    public async Task Changed_exported_file_blocks_rollback_before_pnputil()
    {
        using var temp = new TemporaryDirectory();
        var tool = new FakeDriverTool
        {
            Handler = (args, _) =>
            {
                if (args[0] == "/export-driver")
                {
                    Directory.CreateDirectory(args[2]);
                    File.WriteAllText(Path.Combine(args[2], "acme.inf"), "original");
                    return Task.FromResult(Success());
                }
                return Task.FromResult(Success("deleted"));
            },
        };
        var scanner = CreateScanner(temp.Path, tool);

        var deleted = await scanner.DeleteAsync(Package());
        File.WriteAllText(Path.Combine(deleted.Artifact!.BackupDirectory, "acme.inf"), "tampered");
        var restored = await scanner.RollbackAsync(deleted.OperationId);

        Assert.Equal(DriverMutationOutcome.Deleted, deleted.Outcome);
        Assert.Equal(DriverMutationOutcome.Failed, restored.Outcome);
        Assert.Contains("changed", restored.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tool.Calls, call => call[0] == "/add-driver");
    }

    [Fact]
    public async Task Protected_and_excluded_packages_are_pinned_before_export()
    {
        using var temp = new TemporaryDirectory();
        var tool = new FakeDriverTool { Handler = (_, _) => Task.FromResult(Success()) };
        var scanner = CreateScanner(temp.Path, tool);

        var protectedPackage = Package();
        protectedPackage.IsProtected = true;
        protectedPackage.SafetyReason = "Firmware is pinned.";
        var excludedPackage = Package();
        excludedPackage.IsExcluded = true;

        var protectedResult = await scanner.DeleteAsync(protectedPackage);
        var excludedResult = await scanner.DeleteAsync(excludedPackage);

        Assert.Equal(DriverMutationOutcome.Skipped, protectedResult.Outcome);
        Assert.Contains("pinned", protectedResult.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DriverMutationOutcome.Skipped, excludedResult.Outcome);
        Assert.Contains("excluded", excludedResult.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(tool.Calls);
    }

    private static DriverStoreScanner CreateScanner(string root, FakeDriverTool tool)
    {
        var ledger = new DriverOperationLedger(Path.Combine(root, "operations.jsonl"));
        return new DriverStoreScanner(
            tool,
            new DriverRollbackStore(root, ledger, requireTrustedAcl: false));
    }

    private static DriverPackage Package() => new()
    {
        PublishedName = "oem42.inf",
        OriginalName = "acme.inf",
        ProviderName = "Acme",
        ClassName = "Display",
    };

    private static DriverToolResult Success(string output = "")
        => new(Started: true, ExitCode: 0, Output: output);

    private sealed class FakeDriverTool : IDriverPackageTool
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Func<IReadOnlyList<string>, CancellationToken, Task<DriverToolResult>> Handler { get; init; } =
            (_, _) => Task.FromResult(Success());

        public Task<DriverToolResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            return Handler(arguments, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DeepPurge-DriverTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* test cleanup is best effort */ }
        }
    }
}
