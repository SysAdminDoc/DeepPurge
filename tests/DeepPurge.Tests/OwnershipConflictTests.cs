using DeepPurge.Core.Models;
using Xunit;

namespace DeepPurge.Tests;

public sealed class OwnershipConflictTests
{
    [Fact]
    public void Candidate_inside_another_install_root_is_review_only()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deeppurge-owner-{Guid.NewGuid():N}");
        var target = Program("Target", Path.Combine(root, "Target"), @"HKCU\Software\Target");
        var other = Program("Other", Path.Combine(root, "Other"), @"HKLM\Software\Other");
        var candidate = Path.Combine(other.InstallLocation, "settings.json");

        var decision = LeftoverOwnershipGate.Evaluate(
            target,
            candidate,
            new[] { other },
            new[] { new LeftoverEvidence("Signature", "fixture", EvidenceStrength.Strong) });

        Assert.False(decision.AutoRemovalEligible);
        var conflict = Assert.Single(decision.Conflicts);
        Assert.Equal("Other", conflict.OwnerDisplayName);
        Assert.Contains("install root", conflict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_path_is_protected_even_with_strong_app_metadata()
    {
        var target = Program("Crafted", Path.Combine(Environment.SystemDirectory, "Crafted"), @"HKCU\Software\Crafted");
        var candidate = Path.Combine(Environment.SystemDirectory, "Crafted", "payload.dll");

        var decision = LeftoverOwnershipGate.Evaluate(
            target,
            candidate,
            Array.Empty<InstalledProgram>(),
            new[] { new LeftoverEvidence("InstallLocation", target.InstallLocation, EvidenceStrength.Strong) });

        Assert.True(decision.ProtectedBySystem);
        Assert.False(decision.AutoRemovalEligible);
    }

    [Fact]
    public void One_supporting_signal_is_not_enough_for_auto_removal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deeppurge-owner-{Guid.NewGuid():N}");
        var target = Program("Target", Path.Combine(root, "Target"), @"HKCU\Software\Target");

        var decision = LeftoverOwnershipGate.Evaluate(
            target,
            Path.Combine(root, "cache", "payload.tmp"),
            Array.Empty<InstalledProgram>(),
            new[] { new LeftoverEvidence("Scanner", "cache", EvidenceStrength.Supporting) });

        Assert.False(decision.AutoRemovalEligible);
        Assert.Contains("weak", decision.ReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_independent_supporting_signals_can_clear_the_gate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"deeppurge-owner-{Guid.NewGuid():N}");
        var target = Program("Target", Path.Combine(root, "Target"), @"HKCU\Software\Target");

        var decision = LeftoverOwnershipGate.Evaluate(
            target,
            Path.Combine(root, "cache", "payload.tmp"),
            Array.Empty<InstalledProgram>(),
            new[]
            {
                new LeftoverEvidence("Name", "Target", EvidenceStrength.Supporting),
                new LeftoverEvidence("ScannerContext", "AppData cache", EvidenceStrength.Supporting),
            });

        Assert.True(decision.AutoRemovalEligible);
        Assert.Empty(decision.Conflicts);
    }

    [Fact]
    public void Registry_branch_claimed_by_another_product_is_review_only()
    {
        var target = Program("Target", "", @"HKCU\Software\Target");
        var other = Program("Other", "", @"HKCU\Software\Other");

        var decision = LeftoverOwnershipGate.Evaluate(
            target,
            @"HKCU\Software\Other\Settings",
            new[] { other },
            new[] { new LeftoverEvidence("RegistryIdentity", "Settings", EvidenceStrength.Strong) });

        Assert.False(decision.AutoRemovalEligible);
        Assert.Single(decision.Conflicts);
    }

    private static InstalledProgram Program(
        string name,
        string installLocation,
        string registryPath)
        => new()
        {
            DisplayName = name,
            InstallLocation = installLocation,
            RegistryPath = registryPath,
            Publisher = $"{name} Publisher",
        };
}
