using Xunit;

namespace DeepPurge.Tests;

public class WpfPolishContractTests
{
    [Fact]
    public void Shared_empty_states_are_available_to_user_facing_panels()
    {
        var root = FindRepoRoot();
        var baseStyles = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Themes", "BaseStyles.xaml"));
        var converters = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Converters", "SafeListConverters.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));

        Assert.Contains("x:Key=\"EmptyStateCard\"", baseStyles);
        Assert.Contains("PanelEmptyStateVisibilityConverter", converters);
        Assert.Contains("No programs match this view", xaml);
        Assert.Contains("No deletion manifests yet", xaml);
        Assert.Contains("No scheduled cleaning jobs are active", xaml);
        Assert.Contains("No activity recorded yet", xaml);
    }

    [Fact]
    public void Generated_toolbar_actions_have_accessible_metadata()
    {
        var root = FindRepoRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));

        Assert.Contains("AppendToolbarButton(\"Scan System Drive\"", codeBehind);
        Assert.DoesNotContain("AppendToolbarButton(\"Scan C:\\\\\"", codeBehind);
        Assert.Contains("AutomationProperties.SetName(btn, text);", codeBehind);
        Assert.Contains("AutomationProperties.SetHelpText(btn, BuildToolbarHelpText(text));", codeBehind);
        Assert.Contains("AutomationProperties.Name=\"Panel actions\"", xaml);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeepPurge.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DeepPurge.sln from test output directory.");
    }
}
