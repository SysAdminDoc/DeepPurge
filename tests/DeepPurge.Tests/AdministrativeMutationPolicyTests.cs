using DeepPurge.Core.Firewall;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Services;
using DeepPurge.Core.Shell;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Tasks;

namespace DeepPurge.Tests;

public sealed class AdministrativeMutationPolicyTests
{
    [Fact]
    public void Ledger_records_exact_result_and_preserves_recent_order()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DeepPurge_AdminLedger_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "mutations.jsonl");
        Directory.CreateDirectory(root);
        try
        {
            using (AdministrativeMutationLedger.UsePathForTests(path))
            {
                var first = AdministrativeMutationPolicy.Changed(
                    "test-change",
                    "HKCU\\Software\\Test",
                    "Before",
                    "After",
                    "restore-value");
                var second = AdministrativeMutationPolicy.Skipped(
                    "test-skip",
                    "HKLM\\SYSTEM",
                    "Present",
                    "Protected Windows state.");

                Assert.True(first.Succeeded);
                Assert.False(second.Succeeded);
                var recent = AdministrativeMutationLedger.LoadRecent();
                Assert.Equal(2, recent.Count);
                Assert.Contains(recent, item => item.OperationId == second.OperationId);
                Assert.Contains("restore-value", File.ReadAllText(path));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Protected_firewall_rule_is_skipped_before_native_command()
    {
        var result = FirewallRuleScanner.DeleteRuleDetailed(new FirewallRuleEntry
        {
            Name = "protected-rule",
            DisplayName = "Windows Defender Firewall rule",
            IsSelected = true,
        });

        Assert.Equal(AdministrativeMutationOutcome.Skipped, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Contains("Protected", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Protected_path_entry_is_skipped_before_registry_write()
    {
        var result = PathCleaner.RemoveOrphanedEntriesDetailed(new[]
        {
            new PathEntry
            {
                Directory = Environment.SystemDirectory,
                Source = "User",
                IsOrphaned = true,
                IsSelected = true,
            },
        });

        var mutation = Assert.Single(result);
        Assert.Equal(AdministrativeMutationOutcome.Skipped, mutation.Outcome);
        Assert.Contains("Protected", mutation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Protected_task_and_service_are_not_mutable()
    {
        var task = ScheduledTaskScanner.DeleteTaskDetailed(new ScheduledTaskInfo
        {
            Name = "Task",
            Path = @"\Microsoft\Windows\",
        });
        var service = ServiceScanner.DisableServiceDetailed(new ServiceEntry
        {
            Name = "wuauserv",
        });

        Assert.Equal(AdministrativeMutationOutcome.Skipped, task.Outcome);
        Assert.Equal(AdministrativeMutationOutcome.Skipped, service.Outcome);
    }

    [Fact]
    public void Scheduled_task_autorun_is_explicitly_unsupported()
    {
        var result = AutorunScanner.DeleteAutorunDetailed(new AutorunEntry
        {
            Name = "Task",
            Command = "example.exe",
            Type = AutorunType.ScheduledTask,
        });

        Assert.Equal(AdministrativeMutationOutcome.Unsupported, result.Outcome);
        Assert.False(result.Succeeded);
    }
}
