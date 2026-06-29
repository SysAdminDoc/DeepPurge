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
    public void Settings_privacy_panel_is_reachable_and_saveable()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("Tag=\"Settings\"", xaml);
        Assert.Contains("x:Name=\"panelSettings\"", xaml);
        Assert.Contains("SettingsCookieWhitelistText", xaml);
        Assert.Contains("SettingsExcludedPathsText", xaml);
        Assert.Contains("AppendToolbarButton(\"Save Settings\"", codeBehind);
        Assert.Contains("SaveSettingsEditor", viewModel);
        Assert.Contains("ImportSettingsFrom", viewModel);
        Assert.Contains("ExportSettingsTo", viewModel);
    }

    [Fact]
    public void Scheduled_cleaning_has_gui_creation_controls()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));
        var viewModelExtensions = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "ViewModels", "MainViewModel.Extensions.cs"));

        Assert.Contains("x:Name=\"txtScheduleName\"", xaml);
        Assert.Contains("x:Name=\"cmbSchedulePreset\"", xaml);
        Assert.Contains("Create constrained job", xaml);
        Assert.Contains("AppendToolbarButton(\"Create Job\"", codeBehind);
        Assert.Contains("AppendToolbarButton(\"Remove Job\"", codeBehind);
        Assert.Contains("TryParseScheduleTime", codeBehind);
        Assert.Contains("DeleteScheduledJob", viewModelExtensions);
        Assert.DoesNotContain("Use the CLI", xaml);
    }

    [Fact]
    public void Checkbox_template_preserves_label_content()
    {
        var root = FindRepoRoot();
        var baseStyles = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Themes", "BaseStyles.xaml"));

        Assert.Contains("<ContentPresenter x:Name=\"content\"", baseStyles);
        Assert.Contains("<Trigger Property=\"HasContent\" Value=\"False\">", baseStyles);
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
