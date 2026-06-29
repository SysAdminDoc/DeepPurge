using System.Text.RegularExpressions;

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
    {
        if (!IsSafePackageId(packageId))
            throw new ArgumentException("Invalid package id.", nameof(packageId));

        return NormalizeManager(packageManager) switch
        {
            "winget" => CreateWingetUninstallStartInfo(packageId, silent),
            "scoop" => CreateScoopUninstallStartInfo(packageId),
            "chocolatey" => CreateChocolateyUninstallStartInfo(packageId),
            _ => throw new NotSupportedException($"Unsupported package manager: {packageManager}"),
        };
    }

    public static string DescribeNativeUninstallCommand(
        string packageManager,
        string packageId,
        bool silent = false)
    {
        var psi = CreateNativeUninstallStartInfo(packageManager, packageId, silent);
        return string.Join(" ", new[] { psi.FileName }.Concat(psi.ArgumentList.Select(QuoteForDisplay)));
    }

    private static ProcessStartInfo CreateWingetUninstallStartInfo(string packageId, bool silent)
    {
        var psi = CreateHiddenProcess("winget.exe");
        psi.ArgumentList.Add("uninstall");
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add(packageId);
        psi.ArgumentList.Add("--exact");
        psi.ArgumentList.Add("--disable-interactivity");
        psi.ArgumentList.Add("--accept-source-agreements");
        if (silent) psi.ArgumentList.Add("--silent");
        return psi;
    }

    private static ProcessStartInfo CreateScoopUninstallStartInfo(string packageId)
    {
        // Scoop is normally a shim/cmd wrapper on PATH. Invoke it through cmd.exe,
        // but only after strict package-id validation keeps shell metacharacters out.
        var psi = CreateHiddenProcess("cmd.exe");
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("scoop");
        psi.ArgumentList.Add("uninstall");
        psi.ArgumentList.Add(packageId);
        return psi;
    }

    private static ProcessStartInfo CreateChocolateyUninstallStartInfo(string packageId)
    {
        var psi = CreateHiddenProcess("choco.exe");
        psi.ArgumentList.Add("uninstall");
        psi.ArgumentList.Add(packageId);
        psi.ArgumentList.Add("--yes");
        psi.ArgumentList.Add("--no-progress");
        psi.ArgumentList.Add("--no-color");
        psi.ArgumentList.Add("--limit-output");
        return psi;
    }

    private static ProcessStartInfo CreateHiddenProcess(string fileName)
        => new()
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    private static string NormalizeManager(string? packageManager)
        => (packageManager ?? "").Trim().ToLowerInvariant();

    private static string QuoteForDisplay(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
