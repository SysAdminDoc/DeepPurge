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

    public static ProcessStartInfo CreateWingetUpgradeStartInfo(string packageId)
    {
        if (!IsSafePackageId(packageId))
            throw new ArgumentException("Invalid winget package id.", nameof(packageId));

        var psi = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = true,
        };
        psi.ArgumentList.Add("upgrade");
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add(packageId);
        psi.ArgumentList.Add("--exact");
        psi.ArgumentList.Add("--accept-source-agreements");
        psi.ArgumentList.Add("--accept-package-agreements");
        return psi;
    }

    public static ProcessStartInfo CreateNativeUninstallStartInfo(
        string packageManager,
        string packageId,
        bool silent = false)
        => CreateNativeUninstallCommand(packageManager, packageId, silent).ToStartInfo();

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
        return command.ToRedactedCommandLine();
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
        return new ExternalProcessCommand("winget.exe") { Arguments = args };
    }

    private static ExternalProcessCommand CreateScoopUninstallCommand(string packageId)
    {
        // Scoop is normally a shim/cmd wrapper on PATH. Invoke it through cmd.exe,
        // but only after strict package-id validation keeps shell metacharacters out.
        return new ExternalProcessCommand("cmd.exe")
        {
            Arguments = new[] { "/d", "/c", "scoop", "uninstall", packageId },
        };
    }

    private static ExternalProcessCommand CreateChocolateyUninstallCommand(string packageId)
        => new("choco.exe")
        {
            Arguments = new[]
            {
                "uninstall",
                packageId,
                "--yes",
                "--no-progress",
                "--no-color",
                "--limit-output",
            },
        };

    private static string NormalizeManager(string? packageManager)
        => (packageManager ?? "").Trim().ToLowerInvariant();
}
