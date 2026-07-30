using DeepPurge.Core.App;
using DeepPurge.Core.Execution;
using Microsoft.Win32;

namespace DeepPurge.Core.Packages;

public sealed record PackageManagerLocation(
    string Manager,
    string ExecutablePath,
    string PackageRoot,
    bool Exists,
    IReadOnlyList<string> LauncherArguments)
{
    public string ExecutionContext => "original interactive user (as-invoker)";
}

/// <summary>
/// Resolves package managers from their documented installation roots.
/// PATH and the current directory are intentionally excluded: these programs
/// may be user-writable, but they must never be selected by an elevated search.
/// </summary>
public static class PackageManagerExecutableResolver
{
    public static PackageManagerLocation Resolve(string packageManager)
        => Normalize(packageManager) switch
        {
            "winget" => ResolveWinget(),
            "scoop" => ResolveScoop(),
            "chocolatey" => ResolveChocolatey(),
            _ => throw new NotSupportedException(
                $"Unsupported package manager: {packageManager}"),
        };

    public static ExternalProcessCommand CreateCommand(
        string packageManager,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        bool createNoWindow = true)
    {
        var location = Resolve(packageManager);
        var launcherArguments = location.LauncherArguments
            .Concat(arguments)
            .ToArray();
        var executable = location.Manager == "scoop"
            ? WindowsExecutableResolver.ResolveSystemHelper("powershell.exe")
            : location.ExecutablePath;

        return new ExternalProcessCommand(executable)
        {
            Arguments = launcherArguments,
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
            CreateNoWindow = createNoWindow,
            WorkingDirectory = Environment.SystemDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ExecutionContext = ExternalProcessExecutionContext.OriginalInteractiveUser,
        };
    }

    private static PackageManagerLocation ResolveWinget()
    {
        var root = Path.Combine(
            UserIdentity.RealLocalAppData,
            "Microsoft",
            "WindowsApps");
        var candidates = new[]
        {
            Path.Combine(root, "winget.exe"),
        };
        return Select("winget", root, candidates, Array.Empty<string>());
    }

    private static PackageManagerLocation ResolveScoop()
    {
        var root = Path.Combine(UserIdentity.RealProfilePath, "scoop");
        var candidates = new[]
        {
            Path.Combine(root, "apps", "scoop", "current", "bin", "scoop.ps1"),
            Path.Combine(root, "shims", "scoop.ps1"),
        };
        var selected = Select("scoop", root, candidates, Array.Empty<string>());
        return selected with
        {
            LauncherArguments = new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                selected.ExecutablePath,
            },
        };
    }

    private static PackageManagerLocation ResolveChocolatey()
    {
        var roots = new List<string>();
        AddRoot(roots, ReadChocolateyInstall(EnvironmentVariableTarget.Machine));
        AddRoot(roots, ReadOriginalUserChocolateyInstall());
        AddRoot(roots, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "chocolatey"));

        var candidates = roots
            .Select(root => Path.Combine(root, "bin", "choco.exe"))
            .ToArray();
        var packageRoot = roots.FirstOrDefault() ??
                          Path.Combine(
                              Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                              "chocolatey");
        return Select(
            "chocolatey",
            packageRoot,
            candidates,
            Array.Empty<string>());
    }

    private static PackageManagerLocation Select(
        string manager,
        string packageRoot,
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> launcherArguments)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No known installation path is available for {manager}.");

        var selected = candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists) ??
                       Path.GetFullPath(candidates[0]);
        return new(
            manager,
            selected,
            packageRoot,
            File.Exists(selected),
            launcherArguments);
    }

    private static string? ReadChocolateyInstall(
        EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable("ChocolateyInstall", target);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadOriginalUserChocolateyInstall()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.Users.OpenSubKey(
                $@"{UserIdentity.RealUserSid}\Environment");
            return key?.GetValue(
                "ChocolateyInstall",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void AddRoot(ICollection<string> roots, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!Path.IsPathFullyQualified(expanded)) return;
            var path = Path.GetFullPath(expanded);
            if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }
        catch
        {
            // Invalid user-controlled environment entries are ignored.
        }
    }

    private static string Normalize(string? packageManager)
        => (packageManager ?? "").Trim().ToLowerInvariant();
}
