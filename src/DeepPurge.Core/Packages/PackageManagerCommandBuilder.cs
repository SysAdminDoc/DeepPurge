using System.Text.RegularExpressions;

namespace DeepPurge.Core.Packages;

public static class PackageManagerCommandBuilder
{
    private static readonly Regex SafeWingetPackageId = new(
        @"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSafeWingetPackageId(string? packageId)
        => !string.IsNullOrWhiteSpace(packageId) &&
           packageId == packageId.Trim() &&
           SafeWingetPackageId.IsMatch(packageId);

    public static ProcessStartInfo CreateWingetUpgradeStartInfo(string packageId)
    {
        if (!IsSafeWingetPackageId(packageId))
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
}
