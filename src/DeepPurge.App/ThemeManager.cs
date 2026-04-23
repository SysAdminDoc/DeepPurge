using System.IO;
using System.Windows;
using DeepPurge.Core.App;

namespace DeepPurge.App;

/// <summary>
/// Runtime theme swapper. The first merged dictionary in Application.Resources
/// is always the active color theme; BaseStyles.xaml follows with DynamicResource
/// references so every control updates instantly on swap.
/// </summary>
public static class ThemeManager
{
    public record ThemeInfo(string Name, string FileName, string Category);

    // Five themes, dark-first (per global CLAUDE.md preference).
    private static readonly ThemeInfo[] Themes =
    {
        new("Catppuccin Mocha", "CatppuccinMocha.xaml", "Dark"),
        new("OLED Black",       "OledBlack.xaml",       "Dark"),
        new("Dracula",          "Dracula.xaml",         "Dark"),
        new("Nord Polar",       "NordPolar.xaml",       "Dark"),
        new("GitHub Dark",      "GitHubDark.xaml",      "Dark"),
        new("Arctic",           "Arctic.xaml",          "Light"),
        new("Obsidian",         "Obsidian.xaml",        "Dark"),
        new("Matrix",           "Matrix.xaml",          "Dark"),
    };

    // Routed through DataPaths — picks up portable-mode redirection automatically.
    private static readonly string SettingsFile = DataPaths.ThemeFile;

    private static int _currentIndex;
    private static int _lastDarkIndex;

    public static IReadOnlyList<ThemeInfo> AvailableThemes => Themes;
    public static IReadOnlyList<string> ThemeNames => Themes.Select(t => t.Name).ToArray();
    public static string CurrentThemeName => Themes[_currentIndex].Name;
    public static int CurrentThemeIndex => _currentIndex;
    public static bool IsDarkTheme => !Themes[_currentIndex].Category.Equals("Light", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies the saved theme (or the default dark theme on first run).
    /// Safe to call early in App.OnStartup before the main window loads.
    /// </summary>
    public static void ApplySavedOrDefault()
    {
        var saved = TryReadSavedTheme();
        var idx = saved >= 0 ? saved : 0; // 0 = Catppuccin Mocha (dark default)
        ApplyTheme(idx, persist: false);
    }

    public static void ApplyTheme(int index) => ApplyTheme(index, persist: true);

    public static void ApplyTheme(string name)
    {
        var idx = Array.FindIndex(Themes, t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) ApplyTheme(idx, persist: true);
    }

    private static void ApplyTheme(int index, bool persist)
    {
        if (index < 0 || index >= Themes.Length) return;

        var app = Application.Current;
        if (app == null) return;

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/Colors/{Themes[index].FileName}", UriKind.Absolute)
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        _currentIndex = index;
        if (IsDarkTheme) _lastDarkIndex = index;

        if (persist) TryWriteSavedTheme(Themes[index].Name);
    }

    /// <summary>Toggle between the most recent dark theme and Arctic (light).</summary>
    public static void ToggleLightDark()
    {
        if (IsDarkTheme)
        {
            var arcticIdx = Array.FindIndex(Themes, t => t.Category.Equals("Light", StringComparison.OrdinalIgnoreCase));
            if (arcticIdx >= 0) ApplyTheme(arcticIdx, persist: true);
        }
        else
        {
            ApplyTheme(_lastDarkIndex, persist: true);
        }
    }

    // ── persistence ───────────────────────────────────────────────

    private static int TryReadSavedTheme()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return -1;
            var name = File.ReadAllText(SettingsFile).Trim();
            return Array.FindIndex(Themes, t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return -1; }
    }

    private static void TryWriteSavedTheme(string name)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, name);
        }
        catch { /* non-fatal */ }
    }
}
