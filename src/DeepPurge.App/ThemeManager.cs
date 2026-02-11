using System.Windows;

namespace DeepPurge.App;

public static class ThemeManager
{
    public record ThemeInfo(string Name, string FileName, string Category);

    private static readonly ThemeInfo[] Themes =
    {
        new("Arctic",    "Arctic.xaml",    "Light"),
        new("Obsidian",  "Obsidian.xaml",  "Dark"),
        new("Matrix",    "Matrix.xaml",    "Tech"),
    };

    private static int _currentIndex;

    public static IReadOnlyList<ThemeInfo> AvailableThemes => Themes;
    public static IReadOnlyList<string> ThemeNames => Themes.Select(t => t.Name).ToArray();
    public static string CurrentThemeName => Themes[_currentIndex].Name;
    public static int CurrentThemeIndex => _currentIndex;
    public static bool IsDarkTheme => Themes[_currentIndex].Category != "Light";

    public static void ApplyTheme(int index)
    {
        if (index < 0 || index >= Themes.Length) return;
        _currentIndex = index;
        var dicts = Application.Current.Resources.MergedDictionaries;
        if (dicts.Count < 1) return;
        dicts[0] = new ResourceDictionary
        {
            Source = new Uri($"/Themes/Colors/{Themes[index].FileName}", UriKind.Relative)
        };
    }

    public static void ApplyTheme(string name)
    {
        var idx = Array.FindIndex(Themes, t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) ApplyTheme(idx);
    }

    /// <summary>Toggle between Arctic (light) and Obsidian (dark)</summary>
    public static void ToggleLightDark()
    {
        ApplyTheme(IsDarkTheme ? 0 : 1); // 0=Arctic, 1=Obsidian
    }
}
