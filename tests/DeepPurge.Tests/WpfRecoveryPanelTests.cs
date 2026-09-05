using Xunit;

namespace DeepPurge.Tests;

public class WpfRecoveryPanelTests
{
    [Fact]
    public void Deletion_recovery_panel_is_reachable_from_navigation()
    {
        var repoRoot = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(repoRoot, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("Tag=\"DeletionRecovery\"", xaml);
        Assert.Contains("x:Name=\"panelDeletionRecovery\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding DeletionManifests}\"", xaml);
        Assert.Contains("Visibility=\"{Binding DeletionManifests.Count, Converter={StaticResource EmptyCollectionVisConverter}}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding DeletionManifestEntries}\"", xaml);
        Assert.Contains("Visibility=\"{Binding DeletionRestoreDetails.Count, Converter={StaticResource EmptyCollectionVisConverter}}\"", xaml);
        Assert.Contains("panelDeletionRecovery.Visibility = Visibility.Visible", codeBehind);
        Assert.Contains("DryRunRestoreDeletionManifestCommand", codeBehind);
        Assert.Contains("RestoreSelectedDeletionManifestCommand", codeBehind);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DeepPurge.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate DeepPurge.sln from test output directory.");
    }
}
