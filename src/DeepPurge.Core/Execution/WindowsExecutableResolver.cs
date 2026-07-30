namespace DeepPurge.Core.Execution;

/// <summary>
/// Resolves Windows-owned helper binaries without consulting the working
/// directory or PATH. DeepPurge's desktop process is elevated, so allowing
/// CreateProcess to search for a bare executable name would turn either
/// location into an elevation boundary.
/// </summary>
public static class WindowsExecutableResolver
{
    private static readonly IReadOnlyDictionary<string, Func<string>> KnownHelpers =
        new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["chkdsk.exe"] = () => InSystemDirectory("chkdsk.exe"),
            ["cmd.exe"] = () => InSystemDirectory("cmd.exe"),
            ["dism.exe"] = () => InSystemDirectory("dism.exe"),
            ["ipconfig.exe"] = () => InSystemDirectory("ipconfig.exe"),
            ["msiexec.exe"] = () => InSystemDirectory("msiexec.exe"),
            ["net.exe"] = () => InSystemDirectory("net.exe"),
            ["pnputil.exe"] = () => InSystemDirectory("pnputil.exe"),
            ["powershell.exe"] = () => Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            ["reg.exe"] = () => InSystemDirectory("reg.exe"),
            ["regedit.exe"] = () => InWindowsDirectory("regedit.exe"),
            ["sc.exe"] = () => InSystemDirectory("sc.exe"),
            ["schtasks.exe"] = () => InSystemDirectory("schtasks.exe"),
            ["sfc.exe"] = () => InSystemDirectory("sfc.exe"),
            ["taskkill.exe"] = () => InSystemDirectory("taskkill.exe"),
            ["vssadmin.exe"] = () => InSystemDirectory("vssadmin.exe"),
            ["explorer.exe"] = () => InWindowsDirectory("explorer.exe"),
        };

    private static readonly ISet<string> KnownManagementConsoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "services.msc",
            "taskschd.msc",
        };

    public static string ResolveForLaunch(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Executable path is empty.", nameof(fileName));

        if (Path.IsPathFullyQualified(fileName))
            return Path.GetFullPath(fileName);

        if (fileName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            throw new InvalidOperationException(
                $"Relative executable paths are not allowed: '{fileName}'.");

        if (!KnownHelpers.TryGetValue(fileName, out var resolver))
            throw new InvalidOperationException(
                $"Unqualified executable '{fileName}' is not a recognized Windows helper. " +
                "Resolve it to an absolute path before launching it.");

        return ValidateProtectedHelper(fileName, resolver());
    }

    public static string ResolveSystemHelper(string fileName)
    {
        if (!KnownHelpers.TryGetValue(fileName, out var resolver))
            throw new ArgumentException(
                $"'{fileName}' is not a recognized Windows helper.",
                nameof(fileName));

        return ValidateProtectedHelper(fileName, resolver());
    }

    public static string ResolveShellTarget(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
            return Path.GetFullPath(fileName);

        if (KnownManagementConsoles.Contains(fileName))
        {
            var path = InSystemDirectory(fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Windows management console '{fileName}' was not found in the system directory.",
                    path);
            return path;
        }

        return ResolveSystemHelper(fileName);
    }

    public static bool IsKnownSystemHelper(string fileName)
        => KnownHelpers.ContainsKey(Path.GetFileName(fileName));

    private static string ValidateProtectedHelper(string helperName, string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Windows helper '{helperName}' was not found at its protected system path.",
                path);

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                $"Windows helper '{helperName}' resolves through a reparse point and was rejected.");

        return path;
    }

    private static string InSystemDirectory(string fileName)
        => Path.Combine(Environment.SystemDirectory, fileName);

    private static string InWindowsDirectory(string fileName)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            fileName);
}
