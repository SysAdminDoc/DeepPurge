using DeepPurge.Core.Cleaning;
using DeepPurge.Core.Models;
using Xunit;

namespace DeepPurge.Tests;

public sealed class InstalledProgramNotificationTests
{
    [Fact]
    public void Enrichment_properties_notify_direct_and_dependent_bindings()
    {
        var program = new InstalledProgram { Source = RegistrySource.HKLM_Uninstall };
        var changed = new List<string?>();
        program.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        program.PackageManager = "winget";
        program.UpgradeAvailable = "2.0";
        program.LastUsedDate = new DateTime(2026, 8, 12);
        program.ActualSizeBytes = 4096;
        program.SignatureDisplay = "Signed: Example";
        program.IsSuspectedBundleware = true;
        program.OemBloatScore = 75;
        program.OemBloatReason = "OEM trial";

        Assert.Contains(nameof(InstalledProgram.PackageManager), changed);
        Assert.Contains(nameof(InstalledProgram.UpgradeAvailable), changed);
        Assert.Contains(nameof(InstalledProgram.SourceDisplay), changed);
        Assert.Contains(nameof(InstalledProgram.LastUsedDate), changed);
        Assert.Contains(nameof(InstalledProgram.LastUsedDisplay), changed);
        Assert.Contains(nameof(InstalledProgram.ActualSizeBytes), changed);
        Assert.Contains(nameof(InstalledProgram.EstimatedSizeDisplay), changed);
        Assert.Contains(nameof(InstalledProgram.SignatureDisplay), changed);
        Assert.Contains(nameof(InstalledProgram.RemovalFactsDisplay), changed);
        Assert.Contains(nameof(InstalledProgram.IsSuspectedBundleware), changed);
        Assert.Contains(nameof(InstalledProgram.OemBloatScore), changed);
        Assert.Contains(nameof(InstalledProgram.IsOemBloatCandidate), changed);
        Assert.Contains(nameof(InstalledProgram.FlagsDisplay), changed);
        Assert.Contains(nameof(InstalledProgram.OemBloatReason), changed);
    }

    [Fact]
    public void Display_version_enrichment_notifies_the_program_row()
    {
        var program = new InstalledProgram();
        var changed = new List<string?>();
        program.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        program.DisplayVersion = "4.2.1";

        Assert.Contains(nameof(InstalledProgram.DisplayVersion), changed);
    }

    [Fact]
    public void Slimming_selection_is_mutable_for_the_data_grid()
    {
        var component = new SlimmableComponent("Logs", "old logs", "System", @"C:\Logs", 12, false);
        var changed = new List<string?>();
        component.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        component.IsSelected = true;

        Assert.True(component.IsSelected);
        Assert.Contains(nameof(SlimmableComponent.IsSelected), changed);
    }
}
