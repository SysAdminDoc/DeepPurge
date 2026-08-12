using DeepPurge.Core.App;
using Xunit;

namespace DeepPurge.Tests;

public sealed class CapabilityContractTests
{
    [Fact]
    public void Advertised_capabilities_generate_a_reachable_surface_matrix()
    {
        var root = FindRepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.App", "Views", "MainWindow.xaml.cs"));
        var viewModel = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(root, "src", "DeepPurge.App"), "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var cli = File.ReadAllText(Path.Combine(root, "src", "DeepPurge.Cli", "Program.cs"));
        var core = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(root, "src", "DeepPurge.Core"), "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var failures = new List<string>();

        foreach (var capability in CapabilityCatalog.Capabilities)
        {
            if (!readme.Contains(capability.ReadmeClaim, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{capability.Id}: README claim missing: {capability.ReadmeClaim}");

            if (!capability.HasReachableSurface && string.IsNullOrWhiteSpace(capability.UnsupportedReason))
                failures.Add($"{capability.Id}: no GUI, CLI, source, or explicit unsupported state");

            if (!string.IsNullOrWhiteSpace(capability.GuiTag))
            {
                if (!xaml.Contains($"Tag=\"{capability.GuiTag}\"", StringComparison.Ordinal))
                    failures.Add($"{capability.Id}: GUI tag is not present: {capability.GuiTag}");
                if (!codeBehind.Contains($"case \"{capability.GuiTag}\"", StringComparison.Ordinal))
                    failures.Add($"{capability.Id}: GUI navigation case is not present: {capability.GuiTag}");
            }

            if (!string.IsNullOrWhiteSpace(capability.GuiElement) &&
                !xaml.Contains($"x:Name=\"{capability.GuiElement}\"", StringComparison.Ordinal))
                failures.Add($"{capability.Id}: GUI element is not named in XAML: {capability.GuiElement}");

            if (!string.IsNullOrWhiteSpace(capability.CliCommand))
            {
                if (!cli.Contains($"\"{capability.CliCommand}\"", StringComparison.Ordinal))
                    failures.Add($"{capability.Id}: CLI route is not present: {capability.CliCommand}");
                if (!cli.Contains($"  {capability.CliCommand}", StringComparison.Ordinal))
                    failures.Add($"{capability.Id}: CLI help route is not present: {capability.CliCommand}");
            }

            if (!string.IsNullOrWhiteSpace(capability.SourceMarker) &&
                !($"{core}\n{viewModel}").Contains(capability.SourceMarker, StringComparison.Ordinal))
                failures.Add($"{capability.Id}: source marker is not present: {capability.SourceMarker}");

            if (capability.RequiresExpertMode &&
                !xaml.Contains("ExpertMode", StringComparison.Ordinal) &&
                !viewModel.Contains("ExpertMode", StringComparison.Ordinal))
                failures.Add($"{capability.Id}: expert-mode gate is not represented in the app");
        }

        foreach (var setting in CapabilityCatalog.Settings)
        {
            if (!readme.Contains(setting.ReadmeClaim, StringComparison.OrdinalIgnoreCase))
                failures.Add($"setting {setting.Id}: README claim missing: {setting.ReadmeClaim}");
            if (!File.ReadAllText(Path.Combine(root, "src", "DeepPurge.Core", "App", "AppSettings.cs"))
                    .Contains($"{setting.ModelProperty}", StringComparison.Ordinal))
                failures.Add($"setting {setting.Id}: model property is not present: {setting.ModelProperty}");
            if (!setting.HasReachableSurface && string.IsNullOrWhiteSpace(setting.UnsupportedReason))
                failures.Add($"setting {setting.Id}: no GUI, CLI, or explicit unsupported state");
            if (!string.IsNullOrWhiteSpace(setting.GuiBinding) &&
                !xaml.Contains(setting.GuiBinding, StringComparison.Ordinal))
                failures.Add($"setting {setting.Id}: GUI binding is not present: {setting.GuiBinding}");
            if (!string.IsNullOrWhiteSpace(setting.CliCommand) &&
                !cli.Contains($"\"{setting.CliCommand}", StringComparison.Ordinal))
                failures.Add($"setting {setting.Id}: CLI route is not present: {setting.CliCommand}");
        }

        Assert.True(
            failures.Count == 0,
            string.Join(Environment.NewLine, failures) + Environment.NewLine +
            "Generated capability matrix:" + Environment.NewLine + CapabilityCatalog.RenderMatrix());
    }

    [Fact]
    public void Capability_and_setting_identifiers_are_unique_and_versioned()
    {
        Assert.Equal("1", CapabilityCatalog.ContractVersion);
        Assert.Equal(
            CapabilityCatalog.Capabilities.Count,
            CapabilityCatalog.Capabilities.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            CapabilityCatalog.Settings.Count,
            CapabilityCatalog.Settings.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
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
