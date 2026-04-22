using DeepPurge.Core.Models;
using DeepPurge.Core.Registry;

namespace DeepPurge.Core.Uninstall;

/// <summary>
/// Curated silent-switch table, borrowed in concept from PatchMyPC / Ninite.
/// When the publisher hasn't written a proper <c>QuietUninstallString</c>,
/// we still usually know the silent flag from the installer family (NSIS,
/// InnoSetup, MSI, InstallShield, WiX, Nullsoft, Squirrel).
///
/// Keep this table small and high-signal — it's not a competition with
/// Ninite's curated catalog.
/// </summary>
public static class SilentSwitchDatabase
{
    /// <summary>Per-family silent-uninstall flags.</summary>
    private static readonly Dictionary<string, string> FamilyFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSI"]           = "/qn /norestart",
        ["InnoSetup"]     = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        ["NSIS"]          = "/S",
        ["InstallShield"] = "-s -f1\"%TEMP%\\uninst.iss\"",
        ["WiX"]           = "/quiet /norestart",
        ["Squirrel"]      = "--uninstall --silent",
        ["Nullsoft"]      = "/S",
    };

    /// <summary>Known vendor hints that override the family detection.</summary>
    private static readonly (string Match, string Family)[] VendorOverrides =
    {
        ("unins000.exe", "InnoSetup"),
        ("unins001.exe", "InnoSetup"),
        ("au_.exe",      "NSIS"),
        ("update.exe",   "Squirrel"),    // Electron/Squirrel apps (Slack, VS Code, Teams)
        ("uninst.exe",   "NSIS"),
        ("msiexec",      "MSI"),
    };

    /// <summary>
    /// Returns a best-effort silent uninstall command. Falls back to the
    /// registry's QuietUninstallString when present, then to the family
    /// detection table, then to the raw UninstallString unchanged.
    /// </summary>
    public static string ResolveSilentCommand(InstalledProgram program)
    {
        if (!string.IsNullOrWhiteSpace(program.QuietUninstallString))
            return program.QuietUninstallString;

        var raw = program.UninstallString;
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var family = DetectFamily(program);
        if (string.IsNullOrEmpty(family) || !FamilyFlags.TryGetValue(family, out var flags))
            return raw; // nothing better than the original

        return AppendFlags(raw, flags, family);
    }

    /// <summary>
    /// Detects the installer family. Combines the existing InstallerType
    /// heuristic with a vendor-override pass for common unins000/au_/update.exe
    /// patterns we see in the wild.
    /// </summary>
    public static string DetectFamily(InstalledProgram program)
    {
        var uninstall = (program.UninstallString ?? "").ToLowerInvariant();
        foreach (var (marker, family) in VendorOverrides)
        {
            if (uninstall.Contains(marker)) return family;
        }

        // Delegate to the existing registry scanner heuristic for the rest.
        var inferred = InstalledProgramScanner.DetectInstallerType(program);
        return inferred;
    }

    private static string AppendFlags(string command, string flags, string family)
    {
        // MSI needs the /I → /X rewrite and we route through msiexec in the engine.
        if (family.Equals("MSI", StringComparison.OrdinalIgnoreCase))
        {
            var rewritten = command.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase);
            return rewritten + " " + flags;
        }

        // If the flag is already present, leave it alone.
        if (command.Contains(flags.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            return command;

        return command.TrimEnd() + " " + flags;
    }
}
