using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        Assert.Contains("SettingsRetentionLogsText", xaml);
        Assert.Contains("SettingsScrubSensitivePaths", xaml);
        Assert.Contains("AppendToolbarButton(\"Save Settings\"", codeBehind);
        Assert.Contains("AppendToolbarButton(\"Prune Old Data\"", codeBehind);
        Assert.Contains("SaveSettingsEditor", viewModel);
        Assert.Contains("PrunePrivacyData", viewModel);
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

    [Fact]
    public void About_panel_exposes_local_trust_facts()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));
        var viewModelExtensions = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "ViewModels", "MainViewModel.Extensions.cs"));

        Assert.Contains("ExecutablePathDisplay", xaml);
        Assert.Contains("LocalSignatureDisplay", xaml);
        Assert.Contains("LocalSha256Display", xaml);
        Assert.Contains("ReleaseVerificationText", xaml);
        Assert.Contains("AppendToolbarButton(\"Refresh Trust\"", codeBehind);
        Assert.Contains("AppendToolbarButton(\"Copy SHA256\"", codeBehind);
        Assert.Contains("_vm.RefreshAboutTrustFacts();", codeBehind);
        Assert.Contains("SHA256.HashData", viewModelExtensions);
        Assert.Contains("DigitalSignatureInspector.Inspect", viewModelExtensions);
    }

    [Fact]
    public void Selection_and_validation_failures_use_warning_toasts()
    {
        var root = FindRepoRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));

        Assert.Contains("private void WarnStatus(string message)", codeBehind);
        Assert.Contains("ShowToast(message, isWarning: true);", codeBehind);
        Assert.Contains("WarnStatus(\"Select a program to uninstall.\")", codeBehind);
        Assert.Contains("WarnStatus(\"Enter a program name and installer path first.\")", codeBehind);
        Assert.Contains("WarnStatus(\"Select a deletion manifest first.\")", codeBehind);
        Assert.Contains("ShowToast($\"Trace captured for {name}\");", codeBehind);
        Assert.DoesNotContain("_vm.StatusText = \"Select", codeBehind);
        Assert.DoesNotContain("_vm.StatusText = \"Nothing selected", codeBehind);
        Assert.DoesNotContain("_vm.StatusText = \"Enter a program", codeBehind);
    }

    [Fact]
    public void Icon_only_controls_have_accessible_names()
    {
        var root = FindRepoRoot();
        var xaml = LoadXml(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));

        var missingNames = xaml
            .Descendants()
            .Where(e => e.Name.LocalName is "Button" or "RadioButton" or "MenuItem")
            .Where(IsIconOnlyControl)
            .Where(e => string.IsNullOrWhiteSpace(AttributeValue(e, "AutomationProperties.Name")) &&
                        string.IsNullOrWhiteSpace(AttributeValue(e, "AutomationProperties.HelpText")))
            .Select(DescribeElement)
            .ToList();

        Assert.Empty(missingNames);
    }

    [Fact]
    public void Shared_focusable_styles_keep_visible_focus_indicators()
    {
        var root = FindRepoRoot();
        var baseStyles = LoadXml(Path.Combine(root, "src", "DeepPurge.App", "Themes", "BaseStyles.xaml"));
        var xaml = LoadXml(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));

        var required = new[]
        {
            "Button", "TextBox", "CheckBox", "RadioButton", "ComboBox", "DataGrid",
            "DataGridCell", "GridSplitter", "RichTextBox", "DatePicker", "TabItem",
            "ListBox", "ListBoxItem",
        };

        var styleSources = baseStyles.Root!.Elements()
            .Concat(xaml.Descendants().Where(e => e.Name.LocalName == "Style"));
        var missing = required
            .Where(target => !styleSources
                .Where(style => AttributeValue(style, "TargetType").Contains(target, StringComparison.Ordinal))
                .Any(StyleHasFocusTreatment))
            .ToList();

        Assert.Empty(missing);
        Assert.Contains(baseStyles.Descendants(), e =>
            e.Name.LocalName == "Rectangle" &&
            AttributeValue(e, "Stroke").Contains("AccentBrush", StringComparison.Ordinal) &&
            AttributeValue(e, "StrokeThickness") == "2");
    }

    [Fact]
    public void Empty_disabled_error_and_validation_states_have_static_copy_contracts()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "ViewModels", "MainViewModel.cs"));
        var extensions = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "ViewModels", "MainViewModel.Extensions.cs"));
        var baseStyles = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Themes", "BaseStyles.xaml"));

        Assert.Contains("Trigger Property=\"IsEnabled\" Value=\"False\"", baseStyles);
        Assert.Contains("btnDeleteLeftovers", xaml);
        Assert.Contains("IsEnabled=\"False\"", xaml);
        Assert.Contains("No programs match this view", xaml);
        Assert.Contains("No deletion manifests yet", xaml);
        Assert.Contains("No scheduled cleaning jobs are active", xaml);
        Assert.Contains("No repair command output yet", xaml);
        Assert.Contains("Settings import failed", viewModel);
        Assert.Contains("must be a whole number of days", viewModel);
        Assert.Contains("Validation:", File.ReadAllText(Path.Combine(root, "src", "DeepPurge.Cli", "Program.cs")));
        Assert.Contains("failed:", extensions);
        Assert.Contains("ShowToast(message, isWarning: true);", codeBehind);
        Assert.Contains("ShowToast($\"Error: {ex.Message}\", isError: true);", codeBehind);
    }

    [Fact]
    public void Navigation_resource_keys_exist_and_hardcoded_navigation_labels_are_explicitly_allowed()
    {
        var root = FindRepoRoot();
        var xamlText = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var xaml = LoadXml(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var resx = LoadXml(Path.Combine(root, "src", "DeepPurge.App", "Properties", "Resources.resx"));

        var definedKeys = resx.Descendants()
            .Where(e => e.Name.LocalName == "data")
            .Select(e => AttributeValue(e, "name"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.Ordinal);
        var usedKeys = Regex.Matches(xamlText, @"props:Resources\.([A-Za-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missingResourceKeys = usedKeys.Where(k => !definedKeys.Contains(k)).ToList();
        Assert.Empty(missingResourceKeys);

        var allowedHardcodedNavigationLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "Forced Uninstall",
            "Windows Apps",
            "Empty Folders",
            "Disk Analyzer",
            "Browser Extensions",
            "Context Menu",
            "Services",
            "Scheduled Tasks",
            "Registry Hunter",
            "Orphaned Artifacts",
            "Restore Points",
            "Deletion Recovery",
            "Registry Backups",
            "Startup Impact",
            "Broken Shortcuts",
            "Duplicate Files",
            "Community Cleaners",
            "Scheduled Cleaning",
            "Install Monitor",
            "Settings / Privacy",
        };

        var unexpectedHardcoded = xaml.Descendants()
            .Where(e => e.Name.LocalName == "RadioButton" && AttributeValue(e, "GroupName") == "nav")
            .SelectMany(e => e.Descendants().Where(d => d.Name.LocalName == "TextBlock"))
            .Select(e => AttributeValue(e, "Text"))
            .Where(IsLiteralUserText)
            .Where(text => !allowedHardcodedNavigationLabels.Contains(text))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unexpectedHardcoded);
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

    private static XDocument LoadXml(string path) => XDocument.Parse(File.ReadAllText(path), LoadOptions.PreserveWhitespace);

    private static string AttributeValue(XElement element, string name)
        => element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    private static bool StyleHasFocusTreatment(XElement style)
    {
        var xml = style.ToString(SaveOptions.DisableFormatting);
        return xml.Contains("FocusVisualStyle", StringComparison.Ordinal) ||
               xml.Contains("IsKeyboardFocused", StringComparison.Ordinal) ||
               xml.Contains("IsKeyboardFocusWithin", StringComparison.Ordinal);
    }

    private static bool IsIconOnlyControl(XElement element)
    {
        var textValues = new[] { AttributeValue(element, "Content"), AttributeValue(element, "Header") }
            .Concat(element.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => AttributeValue(e, "Text")))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        return textValues.Count > 0 && textValues.All(IsIconGlyph);
    }

    private static bool IsIconGlyph(string value)
        => value.Trim().Length == 1 && value.Trim()[0] >= '\uE000';

    private static bool IsLiteralUserText(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           !value.StartsWith("{Binding", StringComparison.Ordinal) &&
           !value.StartsWith("{x:Static", StringComparison.Ordinal) &&
           !(value.Trim().Length == 1 && value.Trim()[0] >= '\uE000');

    private static string DescribeElement(XElement element)
    {
        var name = AttributeValue(element, "Name");
        var content = AttributeValue(element, "Content");
        var header = AttributeValue(element, "Header");
        var label = !string.IsNullOrWhiteSpace(name) ? $"#{name}" :
            !string.IsNullOrWhiteSpace(content) ? content :
            !string.IsNullOrWhiteSpace(header) ? header :
            element.Name.LocalName;
        return $"{element.Name.LocalName} {label}";
    }
}
