using System.Security.AccessControl;
using System.Security.Principal;
using DeepPurge.Core.App;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Schedule;

internal sealed record ScheduledExecutableArtifact(
    string Path,
    string Sha256,
    string OwnerSid,
    string DaclSddl);

/// <summary>
/// Copies the CLI through a no-follow handle into an immutable, administrator-
/// owned ProgramData location before a highest-privilege task can reference it.
/// </summary>
internal sealed class ScheduledExecutableStore
{
    private const long MaximumExecutableBytes = 512L * 1024 * 1024;

    private static readonly SecurityIdentifier AdministratorsSid = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        domainSid: null);
    private static readonly SecurityIdentifier SystemSid = new(
        WellKnownSidType.LocalSystemSid,
        domainSid: null);

    private readonly string _root;
    private readonly bool _requireTrustedAcl;

    internal ScheduledExecutableStore(string root, bool requireTrustedAcl)
    {
        _root = Path.GetFullPath(root);
        _requireTrustedAcl = requireTrustedAcl;
    }

    internal static ScheduledExecutableStore Production =>
        new(DataPaths.ScheduledTaskExecutables, requireTrustedAcl: true);

    internal string Root => _root;

    internal ScheduledExecutableArtifact Install(string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("CLI binary not found.", source);
        if (!Path.GetFileName(source).Equals(
                "DeepPurgeCli.exe",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scheduled actions require a DeepPurge CLI executable.");
        if (File.Exists(Path.ChangeExtension(source, ".dll")) ||
            File.Exists(Path.ChangeExtension(source, ".deps.json")) ||
            File.Exists(Path.ChangeExtension(source, ".runtimeconfig.json")))
        {
            throw new InvalidOperationException(
                "The CLI is a multi-file development build. Publish the single-file DeepPurgeCli.exe before scheduling it.");
        }

        var sourceRoot = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("CLI binary has no parent directory.");
        if (!HandleBoundFileOperations.TryReadFileWithinScope(
                source,
                sourceRoot,
                MaximumExecutableBytes,
                out var bytes,
                out var sourceHash,
                out var readReason))
            throw new InvalidOperationException($"Could not capture the CLI executable: {readReason}");
        if (bytes.Length < 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidOperationException("Scheduled action target is not a Windows executable.");

        EnsureProtectedRoot();
        var destination = Path.Combine(
            _root,
            $"DeepPurgeCli-{sourceHash[..16]}.exe");

        if (File.Exists(destination))
        {
            if (TryValidateArtifact(destination, sourceHash, out var existing, out _))
                return existing!;

            if (!HandleBoundFileOperations.DeleteFileWithinScope(
                    destination,
                    _root,
                    out var deleteReason))
                throw new InvalidOperationException(
                    $"Could not replace an invalid scheduled executable: {deleteReason}");
        }

        try
        {
            using var stream = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            HandleBoundFileOperations.DeleteFileWithinScope(destination, _root, out _);
            throw;
        }

        if (_requireTrustedAcl && !ProtectFile(destination, out var protectReason))
        {
            HandleBoundFileOperations.DeleteFileWithinScope(destination, _root, out _);
            throw new InvalidOperationException(protectReason);
        }

        if (!TryValidateArtifact(destination, sourceHash, out var artifact, out var reason))
        {
            HandleBoundFileOperations.DeleteFileWithinScope(destination, _root, out _);
            throw new InvalidOperationException(
                $"Scheduled executable validation failed: {reason}");
        }

        return artifact!;
    }

    internal bool TryValidatePath(
        string path,
        out ScheduledExecutableArtifact? artifact,
        out string reason)
        => TryValidateArtifact(path, expectedSha256: null, out artifact, out reason);

    private void EnsureProtectedRoot()
    {
        if (!_requireTrustedAcl)
        {
            Directory.CreateDirectory(_root);
            return;
        }
        if (!IsProcessElevated())
            throw new InvalidOperationException(
                "Creating a protected scheduled task requires an elevated DeepPurge process.");

        var parent = Path.GetDirectoryName(_root)
            ?? throw new InvalidOperationException("Protected schedule root has no parent.");
        ProtectDirectory(parent);
        ProtectDirectory(_root);
        var parentValid = TryValidateDirectory(parent, out var parentReason);
        var rootValid = TryValidateDirectory(_root, out var rootReason);
        if (!parentValid || !rootValid)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(parentReason) ? rootReason : parentReason);
    }

    private bool TryValidateArtifact(
        string path,
        string? expectedSha256,
        out ScheduledExecutableArtifact? artifact,
        out string reason)
    {
        artifact = null;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) { reason = ex.Message; return false; }

        if (!SafetyGuard.IsSamePathOrDescendant(fullPath, _root) ||
            string.Equals(fullPath, _root, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Scheduled action target is outside the protected executable store.";
            return false;
        }

        if (!HandleBoundFileOperations.TryReadFileWithinScope(
                fullPath,
                _root,
                MaximumExecutableBytes,
                out _,
                out var sha256,
                out reason))
            return false;
        if (expectedSha256 != null &&
            !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Scheduled executable hash does not match the captured CLI.";
            return false;
        }
        var expectedName = $"DeepPurgeCli-{sha256[..16]}.exe";
        if (!Path.GetFileName(fullPath).Equals(
                expectedName,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "Scheduled executable name is not bound to its SHA-256 content.";
            return false;
        }

        var ownerSid = string.Empty;
        var daclSddl = string.Empty;
        if (_requireTrustedAcl &&
            !TryReadTrustedSecurity(fullPath, out ownerSid, out daclSddl, out reason))
            return false;

        artifact = new ScheduledExecutableArtifact(
            fullPath,
            sha256,
            ownerSid,
            daclSddl);
        reason = string.Empty;
        return true;
    }

    private static void ProtectDirectory(string path)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Protected directory is a reparse point: {path}");

        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Protected directory became a reparse point: {path}");

        if (!HandleBoundFileOperations.TrySetSecurityExact(
                path,
                expectedDirectory: true,
                CreateDirectorySecurity(),
                out var reason))
            throw new InvalidOperationException(reason);
    }

    private static bool ProtectFile(string path, out string reason)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Scheduled executable is a reparse point.");
            return HandleBoundFileOperations.TrySetSecurityExact(
                path,
                expectedDirectory: false,
                CreateFileSecurity(),
                out reason);
        }
        catch (Exception ex)
        {
            reason = $"Could not protect scheduled executable ACL: {ex.Message}";
            return false;
        }
    }

    private static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        var desktopUser = GetDesktopUserSid();
        if (desktopUser != null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                desktopUser,
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }

    private static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        var desktopUser = GetDesktopUserSid();
        if (desktopUser != null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                desktopUser,
                FileSystemRights.ReadAndExecute,
                AccessControlType.Allow));
        }
        return security;
    }

    private static SecurityIdentifier? GetDesktopUserSid()
    {
        try
        {
            var sid = new SecurityIdentifier(UserIdentity.RealUserSid);
            return sid == AdministratorsSid || sid == SystemSid ? null : sid;
        }
        catch { return null; }
    }

    private static bool TryValidateDirectory(string path, out string reason)
    {
        try
        {
            if (!Directory.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"Protected schedule directory is missing or reparse-backed: {path}");
            var security = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);
            if (!security.AreAccessRulesProtected)
            {
                reason = $"Protected schedule directory inherits its DACL: {path}";
                return false;
            }
            return IsTrustedSecurity(security, out _, out _, out reason);
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    private static bool TryReadTrustedSecurity(
        string path,
        out string ownerSid,
        out string daclSddl,
        out string reason)
    {
        ownerSid = string.Empty;
        daclSddl = string.Empty;
        try
        {
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Scheduled executable is missing or reparse-backed.");
            var security = FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);
            if (!security.AreAccessRulesProtected)
            {
                reason = "Scheduled executable inherits its DACL.";
                return false;
            }
            return IsTrustedSecurity(security, out ownerSid, out daclSddl, out reason);
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }

    private static bool IsTrustedSecurity(
        FileSystemSecurity security,
        out string ownerSid,
        out string daclSddl,
        out string reason)
    {
        ownerSid = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value
            ?? string.Empty;
        daclSddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        if (!ownerSid.Equals(AdministratorsSid.Value, StringComparison.Ordinal) &&
            !ownerSid.Equals(SystemSid.Value, StringComparison.Ordinal))
        {
            reason = $"Untrusted scheduled executable owner SID: {ownerSid}";
            return false;
        }

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (sid.Equals(AdministratorsSid.Value, StringComparison.Ordinal) ||
                sid.Equals(SystemSid.Value, StringComparison.Ordinal))
                continue;

            const FileSystemRights mutating =
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.WriteAttributes |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;
            if ((rule.FileSystemRights & mutating) != 0)
            {
                reason = $"Scheduled executable grants mutating rights to untrusted SID {sid}.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }
}
