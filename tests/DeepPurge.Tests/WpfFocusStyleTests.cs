using Xunit;

namespace DeepPurge.Tests;

public class WpfFocusStyleTests
{
    [Fact]
    public void Shared_styles_do_not_remove_keyboard_focus_visuals()
    {
        var root = FindRepoRoot();
        var baseStyles = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Themes", "BaseStyles.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var combined = baseStyles + Environment.NewLine + mainWindow;

        Assert.DoesNotContain("FocusVisualStyle\" Value=\"{x:Null}\"", combined);
        Assert.Contains("DeepPurgeFocusVisual", baseStyles);
        Assert.Contains("IsKeyboardFocusWithin", baseStyles);
        Assert.Contains("IsKeyboardFocused", combined);
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
