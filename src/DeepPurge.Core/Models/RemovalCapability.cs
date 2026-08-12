using DeepPurge.Core.Execution;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Security;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Uninstall;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeepPurge.Core.Models;

/// <summary>What kind of source-backed removal action a program exposes.</summary>
public enum RemovalCapability
{
    NativeUninstaller,
    PackageManager,
    PortableFolder,
    GameLauncher,
    Unsupported,
}

public enum UninstallerRisk
{
    Unknown,
    Low,
    Review,
    High,
    Blocked,
}

/// <summary>
/// Trust facts shown before a registered command crosses into an elevated
/// process. These are facts, not a blanket claim that a third-party vendor is
/// trusted.
/// </summary>
public sealed record UninstallerTrustFacts(
    string ExecutablePath,
    string Arguments,
    string Owner,
    string Publisher,
    SignatureStatus Signature,
    UninstallerRisk Risk,
    bool IsActionAvailable,
    string Reason)
{
    public static UninstallerTrustFacts Empty { get; } = new(
        "",
        "",
        "",
        "",
        SignatureStatus.Unknown,
        UninstallerRisk.Unknown,
        false,
        "No removal command has been resolved.");

    public string SignatureDisplay => Signature switch
    {
        SignatureStatus.Signed => string.IsNullOrWhiteSpace(Publisher) ? "Signed" : Publisher,
        SignatureStatus.Unsigned => "Unsigned",
        SignatureStatus.ChainInvalid => "Untrusted signature",
        SignatureStatus.Invalid => "Invalid signature",
        SignatureStatus.Revoked => "Revoked signature",
        SignatureStatus.Missing => "Executable missing",
        _ => "Unknown signature",
    };

    public string RiskDisplay => Risk.ToString();

    public string ReviewDisplay =>
        $"{SignatureDisplay}; owner={DisplayOrUnknown(Owner)}; risk={RiskDisplay}";

    private static string DisplayOrUnknown(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}

/// <summary>
/// Resolves and re-evaluates the capability shown by an InstalledProgram.
/// Call this after source enrichment and immediately before execution.
/// </summary>
public static class RemovalCapabilityInspector
{
    public static void Populate(InstalledProgram program, bool silent = false)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));

        if (PackageManagerCommandBuilder.IsSupportedNativeUninstallManager(program.PackageManager) &&
            PackageManagerCommandBuilder.IsSafePackageId(program.PackageId))
        {
            ApplyPackageManager(program, silent);
            return;
        }

        if (program.Source == RegistrySource.Portable &&
            program.PackageManager.Equals("portable", StringComparison.OrdinalIgnoreCase))
        {
            ApplyPortableFolder(program);
            return;
        }

        if (program.Source == RegistrySource.Portable &&
            program.PackageManager is "steam" or "epic" or "gog")
        {
            ApplyGameLauncher(program);
            return;
        }

        if (program.HasUninstaller)
        {
            ApplyNativeUninstaller(program, silent);
            return;
        }

        ApplyUnsupported(
            program,
            RemovalCapability.Unsupported,
            program.RegistryPath,
            UninstallerTrustFacts.Empty with
            {
                Reason = "No supported package-manager, native, portable-folder, or game-launcher action was discovered.",
            });
    }

    private static void ApplyPackageManager(InstalledProgram program, bool silent)
    {
        var identity = $"{Normalize(program.PackageManager)}:{program.PackageId}";
        try
        {
            var command = PackageManagerCommandBuilder.CreateNativeUninstallCommand(
                program.PackageManager,
                program.PackageId,
                silent);
            var location = PackageManagerExecutableResolver.Resolve(program.PackageManager);
            var available = location.Exists;
            var signature = DigitalSignatureInspector.Inspect(command.FileName);
            var reason = available
                ? "Source-native package-manager command is available and uses the original interactive-user context."
                : "Package identity is valid, but the source executable is not installed on this machine.";
            var facts = new UninstallerTrustFacts(
                command.FileName,
                string.Join(' ', command.Arguments),
                TryGetOwner(command.FileName),
                signature.Subject ?? "",
                signature.Status,
                available ? UninstallerRisk.Low : UninstallerRisk.Blocked,
                IsSafeManagerCommand(command),
                reason);

            Apply(
                program,
                RemovalCapability.PackageManager,
                identity,
                facts);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ApplyUnsupported(
                program,
                RemovalCapability.PackageManager,
                identity,
                UninstallerTrustFacts.Empty with
                {
                    Risk = UninstallerRisk.Blocked,
                    Reason = $"Package-manager action is unavailable: {ex.Message}",
                });
        }
    }

    private static void ApplyPortableFolder(InstalledProgram program)
    {
        var path = program.InstallLocation;
        var available = !string.IsNullOrWhiteSpace(path) &&
                        Directory.Exists(path) &&
                        SafetyGuard.IsPathSafeToDelete(path);
        var reason = available
            ? "The portable install folder can be moved to the Recycle Bin as one recoverable action."
            : "The portable install folder is missing, malformed, or protected.";
        Apply(
            program,
            RemovalCapability.PortableFolder,
            path,
            UninstallerTrustFacts.Empty with
            {
                ExecutablePath = program.DisplayIconPath,
                Owner = TryGetOwner(path),
                Publisher = program.Publisher,
                Risk = available ? UninstallerRisk.Review : UninstallerRisk.Blocked,
                IsActionAvailable = available,
                Reason = reason,
            });
    }

    private static void ApplyGameLauncher(InstalledProgram program)
    {
        ApplyUnsupported(
            program,
            RemovalCapability.GameLauncher,
            $"{Normalize(program.PackageManager)}:{program.InstallLocation}",
            UninstallerTrustFacts.Empty with
            {
                Publisher = program.Publisher,
                Risk = UninstallerRisk.Blocked,
                Reason = "Game-launcher discovery is review-only until a source-native uninstall command is implemented.",
            });
    }

    private static void ApplyNativeUninstaller(InstalledProgram program, bool silent)
    {
        var command = silent
            ? SilentSwitchDatabase.ResolveSilentCommand(program)
            : program.UninstallString;
        if (string.IsNullOrWhiteSpace(command))
            command = program.UninstallString;
        try
        {
            var startInfo = UninstallEngine.BuildUninstallerStartInfo(command, silent: false);
            var executable = Path.GetFullPath(startInfo.FileName);
            var exists = File.Exists(executable);
            var reparse = exists && (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0;
            var signature = exists
                ? DigitalSignatureInspector.Inspect(executable)
                : SignatureInfo.Missing;
            var owner = exists ? TryGetOwner(executable) : "";
            var risk = !exists || reparse
                ? UninstallerRisk.Blocked
                : signature.Status switch
                {
                    SignatureStatus.Signed => UninstallerRisk.Low,
                    SignatureStatus.Unsigned or SignatureStatus.Unknown => UninstallerRisk.High,
                    _ => UninstallerRisk.High,
                };
            var reason = !exists
                ? "The registered uninstaller executable is missing."
                : reparse
                    ? "The registered uninstaller resolves through a reparse point."
                    : "The registered executable and arguments were parsed without shell interpolation; review trust facts before elevation.";

            Apply(
                program,
                RemovalCapability.NativeUninstaller,
                program.RegistryPath,
                new UninstallerTrustFacts(
                    executable,
                    startInfo.Arguments,
                    owner,
                    signature.Subject ?? program.Publisher,
                    signature.Status,
                    risk,
                    exists && !reparse,
                    reason));
        }
        catch (Exception ex)
        {
            ApplyUnsupported(
                program,
                RemovalCapability.NativeUninstaller,
                program.RegistryPath,
                UninstallerTrustFacts.Empty with
                {
                    Risk = UninstallerRisk.Blocked,
                    Reason = $"Registered uninstaller was rejected: {ex.Message}",
                });
        }
    }

    private static bool IsSafeManagerCommand(
        ExternalProcessCommand command)
        =>
           Path.IsPathFullyQualified(command.FileName) &&
           command.ExecutionContext == ExternalProcessExecutionContext.OriginalInteractiveUser;

    private static void ApplyUnsupported(
        InstalledProgram program,
        RemovalCapability capability,
        string identity,
        UninstallerTrustFacts facts)
        => Apply(program, capability, identity, facts with { IsActionAvailable = false });

    private static void Apply(
        InstalledProgram program,
        RemovalCapability capability,
        string identity,
        UninstallerTrustFacts facts)
    {
        program.RemovalCapability = capability;
        program.RemovalSourceIdentity = identity ?? "";
        program.UninstallerTrust = facts;
        program.UninstallerExecutablePath = facts.ExecutablePath;
        program.UninstallerArguments = facts.Arguments;
        program.UninstallerOwner = facts.Owner;
        program.UninstallerPublisher = facts.Publisher;
        program.UninstallerRisk = facts.RiskDisplay;
        program.SignatureDisplay = facts.SignatureDisplay;
        program.OnRemovalFactsChanged();
    }

    private static string TryGetOwner(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (!File.Exists(path) && !Directory.Exists(path)) ||
            !OperatingSystem.IsWindows())
            return "";
        try
        {
            FileSystemSecurity security = File.Exists(path)
                ? FileSystemAclExtensions.GetAccessControl(
                    new FileInfo(path),
                    AccessControlSections.Owner)
                : FileSystemAclExtensions.GetAccessControl(
                    new DirectoryInfo(path),
                    AccessControlSections.Owner);
            return security.GetOwner(typeof(NTAccount))?.Value ?? "";
        }
        catch { return ""; }
    }

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();
}
