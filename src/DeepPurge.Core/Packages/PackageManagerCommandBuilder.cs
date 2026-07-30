using System.Text.RegularExpressions;
using DeepPurge.Core.Execution;

namespace DeepPurge.Core.Packages;

public static class PackageManagerCommandBuilder
{
    private static readonly Regex SafePackageId = new(
        @"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSafeWingetPackageId(string? packageId)
        => IsSafePackageId(packageId);

    public static bool IsSafePackageId(string? packageId)
        => !string.IsNullOrWhiteSpace(packageId) &&
           packageId == packageId.Trim() &&
           SafePackageId.IsMatch(packageId);

    public static bool IsSupportedNativeUninstallManager(string? packageManager)
        => NormalizeManager(packageManager) is "winget" or "scoop" or "chocolatey";

    public static ExternalProcessCommand CreateWingetUpgradeCommand(string packageId)
    {
        if (!IsSafePackageId(packageId))
            throw new ArgumentException("Invalid winget package id.", nameof(packageId));

        return PackageManagerExecutableResolver.CreateCommand(
            "winget",
            new[]
            {
                "upgrade",
                "--id",
                packageId,
                "--exact",
                "--accept-source-agreements",
                "--accept-package-agreements",
            },
            timeout: TimeSpan.FromHours(2),
            createNoWindow: false);
    }

    public static ExternalProcessCommand CreateNativeUninstallCommand(
        string packageManager,
        string packageId,
        bool silent = false)
    {
        if (!IsSafePackageId(packageId))
            throw new ArgumentException("Invalid package id.", nameof(packageId));

        return NormalizeManager(packageManager) switch
        {
            "winget" => CreateWingetUninstallCommand(packageId, silent),
            "scoop" => CreateScoopUninstallCommand(packageId),
            "chocolatey" => CreateChocolateyUninstallCommand(packageId),
            _ => throw new NotSupportedException($"Unsupported package manager: {packageManager}"),
        };
    }

    public static string DescribeNativeUninstallCommand(
        string packageManager,
        string packageId,
        bool silent = false)
    {
        var command = CreateNativeUninstallCommand(packageManager, packageId, silent);
        var manager = NormalizeManager(packageManager);
        var launcherArgumentCount =
            PackageManagerExecutableResolver.Resolve(manager).LauncherArguments.Count;
        return $"{manager} {string.Join(" ", command.Arguments.Skip(launcherArgumentCount))}";
    }

    private static ExternalProcessCommand CreateWingetUninstallCommand(string packageId, bool silent)
    {
        var args = new List<string>
        {
            "uninstall",
            "--id",
            packageId,
            "--exact",
            "--disable-interactivity",
            "--accept-source-agreements",
        };
        if (silent) args.Add("--silent");
        return PackageManagerExecutableResolver.CreateCommand(
            "winget",
            args,
            timeout: TimeSpan.FromHours(2));
    }

    private static ExternalProcessCommand CreateScoopUninstallCommand(string packageId)
        => PackageManagerExecutableResolver.CreateCommand(
            "scoop",
            new[] { "uninstall", packageId },
            timeout: TimeSpan.FromHours(2));

    private static ExternalProcessCommand CreateChocolateyUninstallCommand(string packageId)
        => PackageManagerExecutableResolver.CreateCommand(
            "chocolatey",
            new[]
            {
                "uninstall",
                packageId,
                "--yes",
                "--no-progress",
                "--no-color",
                "--limit-output",
            },
            timeout: TimeSpan.FromHours(2));

    private static string NormalizeManager(string? packageManager)
        => (packageManager ?? "").Trim().ToLowerInvariant();
}
