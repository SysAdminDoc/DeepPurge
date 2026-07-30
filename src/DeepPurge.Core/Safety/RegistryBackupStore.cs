using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Registry;

namespace DeepPurge.Core.Safety;

internal sealed record RegistryBackupArtifact(
    string OperationId,
    string BackupPath,
    string BackupSha256,
    string Hive,
    string SubKey,
    string? ValueName,
    string RegistryView,
    string ObjectIdentity,
    string BackupOwnerSid,
    string BackupDaclSddl,
    bool BackupAclTrusted);

internal sealed record RegistryBackupMetadata(
    int SchemaVersion,
    string OperationId,
    string Hive,
    string SubKey,
    string? ValueName,
    string RegistryView,
    string ObjectIdentity);

/// <summary>
/// Writes handle-captured registry snapshots to a protected system location
/// and validates every field again before a restore can invoke reg.exe.
/// </summary>
internal sealed class RegistryBackupStore
{
    private const int ManifestSchemaVersion = 2;
    private const long MaximumBackupBytes = 64L * 1024 * 1024;
    private const string MetadataPrefix = "; DeepPurge-Metadata: ";

    private static readonly SecurityIdentifier AdministratorsSid = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        domainSid: null);
    private static readonly SecurityIdentifier SystemSid = new(
        WellKnownSidType.LocalSystemSid,
        domainSid: null);

    private readonly string _root;
    private readonly bool _requireTrustedAcl;
    private readonly Action<RegistryBackupArtifact>? _createdHook;

    internal RegistryBackupStore(
        string root,
        bool requireTrustedAcl,
        Action<RegistryBackupArtifact>? createdHook = null)
    {
        _root = Path.GetFullPath(root);
        _requireTrustedAcl = requireTrustedAcl;
        _createdHook = createdHook;
    }

    internal static RegistryBackupStore Production =>
        new(DataPaths.RegistryBackups, requireTrustedAcl: true);

    internal string Root => _root;
    internal bool RequiresTrustedAcl => _requireTrustedAcl;

    internal void Discard(RegistryBackupArtifact artifact)
    {
        if (!string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(artifact.BackupPath)),
                _root,
                StringComparison.OrdinalIgnoreCase))
            return;
        HandleBoundFileOperations.DeleteFileWithinScope(
            artifact.BackupPath,
            _root,
            out _);
    }

    internal RegistryBackupArtifact? Create(RegistryObjectSnapshot snapshot)
    {
        if (!ValidateSnapshotScope(snapshot, out var reason))
        {
            Log.Warn($"Registry backup snapshot rejected: {reason}");
            return null;
        }

        if (!EnsureRoot(out reason))
        {
            Log.Warn($"Registry backup root is unavailable: {reason}");
            return null;
        }

        var operationId = Guid.NewGuid().ToString("N");
        var metadata = new RegistryBackupMetadata(
            ManifestSchemaVersion,
            operationId,
            snapshot.Hive,
            snapshot.SubKey,
            snapshot.ValueName,
            snapshot.RegistryView,
            snapshot.ObjectIdentity);
        var path = Path.Combine(
            _root,
            $"registry-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{operationId}.reg");

        try
        {
            var document = BuildRegistryDocument(snapshot, metadata);
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                stream,
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true)))
            {
                writer.Write(document);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (_requireTrustedAcl && !ProtectFile(path, out reason))
                throw new InvalidOperationException(reason);

            if (!TryReadSecurityEvidence(
                    path,
                    requireTrusted: _requireTrustedAcl,
                    out var ownerSid,
                    out var daclSddl,
                    out var trusted,
                    out reason))
                throw new InvalidOperationException(reason);

            if (!HandleBoundFileOperations.TryReadFileWithinScope(
                    path,
                    _root,
                    MaximumBackupBytes,
                    out var bytes,
                    out var sha256,
                    out reason))
                throw new InvalidOperationException(reason);

            if (!TryValidateRegistryDocument(
                    bytes,
                    snapshot.Hive,
                    snapshot.SubKey,
                    snapshot.ValueName,
                    metadata,
                    out reason))
                throw new InvalidOperationException(reason);

            var artifact = new RegistryBackupArtifact(
                operationId,
                path,
                sha256,
                snapshot.Hive,
                snapshot.SubKey,
                snapshot.ValueName,
                snapshot.RegistryView,
                snapshot.ObjectIdentity,
                ownerSid,
                daclSddl,
                trusted);
            _createdHook?.Invoke(artifact);
            return artifact;
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry backup creation failed: {ex.Message}");
            HandleBoundFileOperations.DeleteFileWithinScope(path, _root, out _);
            return null;
        }
    }

    internal bool TryValidateForRestore(
        DeletionEntry entry,
        out string backupPath,
        out string reason)
    {
        backupPath = string.Empty;
        if (entry.SchemaVersion != ManifestSchemaVersion ||
            string.IsNullOrWhiteSpace(entry.OperationId) ||
            string.IsNullOrWhiteSpace(entry.BackupPath) ||
            string.IsNullOrWhiteSpace(entry.BackupSha256) ||
            string.IsNullOrWhiteSpace(entry.RegistryHive) ||
            string.IsNullOrWhiteSpace(entry.RegistrySubKey) ||
            string.IsNullOrWhiteSpace(entry.RegistryView) ||
            string.IsNullOrWhiteSpace(entry.ObjectIdentity) ||
            string.IsNullOrWhiteSpace(entry.BackupOwnerSid) ||
            string.IsNullOrWhiteSpace(entry.BackupDaclSddl) ||
            !entry.BackupAclTrusted)
        {
            reason = "The registry record is legacy or missing bound recovery fields.";
            return false;
        }

        if (entry.Outcome is not ("Prepared" or "Succeeded" or "Failed"))
        {
            reason = $"The registry operation outcome '{entry.Outcome}' is not recoverable.";
            return false;
        }

        if (!RegistryDeletion.TryParseKeyPath(
                $"{entry.RegistryHive}\\{entry.RegistrySubKey}",
                out var parsed) ||
            !parsed.HiveName.Equals(entry.RegistryHive, StringComparison.OrdinalIgnoreCase) ||
            !parsed.SubKey.Equals(entry.RegistrySubKey, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The manifest hive/subkey fields are malformed.";
            return false;
        }

        var expectedPath = string.IsNullOrEmpty(entry.RegistryValueName)
            ? parsed.CanonicalPath
            : $"{parsed.CanonicalPath}\\{entry.RegistryValueName}";
        if (!expectedPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The manifest path does not match its bound hive/subkey/value fields.";
            return false;
        }

        string candidate;
        try { candidate = Path.GetFullPath(entry.BackupPath); }
        catch (Exception ex)
        {
            reason = $"The bound backup path is invalid: {ex.Message}";
            return false;
        }

        if (!string.Equals(
                Path.GetDirectoryName(candidate),
                _root,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(candidate).Equals(".reg", StringComparison.OrdinalIgnoreCase))
        {
            reason = "The bound backup is outside the protected registry-backup directory.";
            return false;
        }

        if (!ValidateProtectedRoot(out reason))
            return false;

        if (!TryReadSecurityEvidence(
                candidate,
                requireTrusted: true,
                out var ownerSid,
                out var daclSddl,
                out var trusted,
                out reason) ||
            !trusted)
            return false;

        if (!ownerSid.Equals(entry.BackupOwnerSid, StringComparison.Ordinal) ||
            !daclSddl.Equals(entry.BackupDaclSddl, StringComparison.Ordinal))
        {
            reason = "The backup ownership or DACL no longer matches the deletion record.";
            return false;
        }

        if (!HandleBoundFileOperations.TryReadFileWithinScope(
                candidate,
                _root,
                MaximumBackupBytes,
                out var bytes,
                out var sha256,
                out reason))
            return false;

        if (!sha256.Equals(entry.BackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The registry backup SHA-256 does not match the deletion record.";
            return false;
        }

        var metadata = new RegistryBackupMetadata(
            entry.SchemaVersion,
            entry.OperationId,
            entry.RegistryHive,
            entry.RegistrySubKey,
            entry.RegistryValueName,
            entry.RegistryView,
            entry.ObjectIdentity);
        if (!TryValidateRegistryDocument(
                bytes,
                entry.RegistryHive,
                entry.RegistrySubKey,
                entry.RegistryValueName,
                metadata,
                out reason))
            return false;

        if (!TryReadSecurityEvidence(
                candidate,
                requireTrusted: true,
                out var finalOwnerSid,
                out var finalDaclSddl,
                out var finalTrusted,
                out reason) ||
            !finalTrusted ||
            !finalOwnerSid.Equals(ownerSid, StringComparison.Ordinal) ||
            !finalDaclSddl.Equals(daclSddl, StringComparison.Ordinal))
        {
            reason = "The backup security descriptor changed during validation.";
            return false;
        }

        backupPath = candidate;
        reason = string.Empty;
        return true;
    }

    private bool EnsureRoot(out string reason)
    {
        if (!_requireTrustedAcl)
        {
            try
            {
                Directory.CreateDirectory(_root);
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        if (!IsProcessElevated())
        {
            reason = "Creating trusted registry backups requires an elevated process.";
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(_root);
            if (string.IsNullOrWhiteSpace(parent))
                throw new InvalidOperationException("The protected backup root has no parent.");

            ProtectDirectory(parent);
            ProtectDirectory(_root);
            return ValidateProtectedRoot(out reason);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private bool ValidateProtectedRoot(out string reason)
    {
        var parent = Path.GetDirectoryName(_root);
        if (string.IsNullOrWhiteSpace(parent))
        {
            reason = "The protected registry-backup root has no parent.";
            return false;
        }
        if (!TryValidateProtectedDirectory(parent, out reason) ||
            !TryValidateProtectedDirectory(_root, out reason))
            return false;

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
                CreateTrustedDirectorySecurity(),
                out var reason))
            throw new InvalidOperationException(reason);
    }

    private static bool ProtectFile(string path, out string reason)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Registry backup is a reparse point.");
            if (!HandleBoundFileOperations.TrySetSecurityExact(
                    path,
                    expectedDirectory: false,
                    CreateTrustedFileSecurity(),
                    out reason))
                return false;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Could not protect registry backup ACL: {ex.Message}";
            return false;
        }
    }

    private static DirectorySecurity CreateTrustedDirectorySecurity()
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

    private static FileSecurity CreateTrustedFileSecurity()
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
        var sid = UserIdentity.RealUserSid;
        if (string.IsNullOrWhiteSpace(sid)) return null;
        try
        {
            var identifier = new SecurityIdentifier(sid);
            return identifier == AdministratorsSid || identifier == SystemSid
                ? null
                : identifier;
        }
        catch { return null; }
    }

    private static bool TryValidateProtectedDirectory(string path, out string reason)
    {
        try
        {
            if (!Directory.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"Protected registry-backup directory is missing or reparse-backed: {path}");

            var security = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);
            if (!IsTrustedSecurityDescriptor(security, out _, out _, out reason))
                return false;
            if (!security.AreAccessRulesProtected)
            {
                reason = $"Protected registry-backup directory inherits its DACL: {path}";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool TryReadSecurityEvidence(
        string path,
        bool requireTrusted,
        out string ownerSid,
        out string daclSddl,
        out bool trusted,
        out string reason)
    {
        ownerSid = string.Empty;
        daclSddl = string.Empty;
        trusted = false;
        try
        {
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Registry backup is missing or is a reparse point.");

            var security = FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);
            trusted = IsTrustedSecurityDescriptor(
                security,
                out ownerSid,
                out daclSddl,
                out reason);
            if (requireTrusted && (!trusted || !security.AreAccessRulesProtected))
            {
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Registry backup does not have a protected trusted DACL.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool IsTrustedSecurityDescriptor(
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
            reason = $"Untrusted registry-backup owner SID: {ownerSid}";
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
                reason = $"Registry backup grants mutating rights to untrusted SID {sid}.";
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

    private static bool ValidateSnapshotScope(
        RegistryObjectSnapshot snapshot,
        out string reason)
    {
        if (snapshot.Keys.Count == 0 ||
            snapshot.Keys.Any(key =>
                key.RelativePath.Contains(']') ||
                key.RelativePath.Contains('\r') ||
                key.RelativePath.Contains('\n')) ||
            snapshot.Keys.SelectMany(key => key.Values).Any(value =>
                value.Name.Contains('\r') || value.Name.Contains('\n')))
        {
            reason = "Registry names cannot be represented safely in a .reg artifact.";
            return false;
        }

        if (snapshot.ValueName != null &&
            (snapshot.Keys.Count != 1 ||
             snapshot.Keys[0].RelativePath.Length != 0 ||
             snapshot.Keys[0].Values.Count != 1 ||
             !snapshot.Keys[0].Values[0].Name.Equals(
                 snapshot.ValueName,
                 StringComparison.OrdinalIgnoreCase)))
        {
            reason = "A value rollback artifact must contain exactly the requested value.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string BuildRegistryDocument(
        RegistryObjectSnapshot snapshot,
        RegistryBackupMetadata metadata)
    {
        var metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadata);
        var builder = new StringBuilder();
        builder.AppendLine("Windows Registry Editor Version 5.00");
        builder.Append(MetadataPrefix);
        builder.AppendLine(Convert.ToBase64String(metadataJson));
        builder.AppendLine();

        foreach (var key in snapshot.Keys)
        {
            var fullPath = ExpandHive(snapshot.Hive) + "\\" + snapshot.SubKey;
            if (!string.IsNullOrEmpty(key.RelativePath))
                fullPath += "\\" + key.RelativePath;
            builder.Append('[').Append(fullPath).AppendLine("]");
            foreach (var value in key.Values)
                builder.AppendLine(FormatValue(value));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatValue(RegistryValueSnapshot value)
    {
        var name = value.Name.Length == 0
            ? "@"
            : $"\"{value.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        if (value.Type == 4 && value.Data.Length == 4)
        {
            var number = BitConverter.ToUInt32(value.Data, 0);
            return $"{name}=dword:{number:x8}";
        }

        var typePrefix = value.Type switch
        {
            3 => "hex:",
            1 => "hex(1):",
            2 => "hex(2):",
            7 => "hex(7):",
            11 => "hex(b):",
            _ => $"hex({value.Type:x}):",
        };
        return name + "=" + FormatHex(typePrefix, value.Data);
    }

    private static string FormatHex(string prefix, byte[] data)
    {
        if (data.Length == 0) return prefix;
        const int bytesPerLine = 24;
        var builder = new StringBuilder(prefix);
        for (var offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            if (offset > 0) builder.Append("  ");
            var count = Math.Min(bytesPerLine, data.Length - offset);
            for (var index = 0; index < count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append(data[offset + index].ToString("x2"));
            }
            if (offset + count < data.Length)
                builder.AppendLine(",\\");
        }
        return builder.ToString();
    }

    internal static bool TryValidateRegistryDocument(
        byte[] bytes,
        string hive,
        string subKey,
        string? valueName,
        RegistryBackupMetadata expectedMetadata,
        out string reason)
    {
        try
        {
            var text = DecodeRegistryDocument(bytes);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0 ||
                !lines[0].Equals(
                    "Windows Registry Editor Version 5.00",
                    StringComparison.Ordinal))
                throw new InvalidDataException("Registry backup header is invalid.");

            var metadataLine = lines.SingleOrDefault(line =>
                line.StartsWith(MetadataPrefix, StringComparison.Ordinal));
            if (metadataLine == null)
                throw new InvalidDataException("Registry backup metadata is missing or duplicated.");
            var encoded = metadataLine[MetadataPrefix.Length..].Trim();
            var actualMetadata = JsonSerializer.Deserialize<RegistryBackupMetadata>(
                Convert.FromBase64String(encoded));
            if (actualMetadata != expectedMetadata)
                throw new InvalidDataException(
                    "Protected backup metadata does not match the deletion record.");

            var expectedRoot = $"{ExpandHive(hive)}\\{subKey}";
            var keyHeaders = lines
                .Where(line => line.StartsWith("[", StringComparison.Ordinal) &&
                               line.EndsWith("]", StringComparison.Ordinal))
                .Select(line => line[1..^1])
                .ToList();
            if (keyHeaders.Count == 0)
                throw new InvalidDataException("Registry backup contains no key headers.");
            if (keyHeaders.Any(header =>
                    !header.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase) &&
                    !header.StartsWith(expectedRoot + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "Registry backup contains a key outside its bound scope.");

            if (valueName != null)
            {
                if (keyHeaders.Count != 1 ||
                    !keyHeaders[0].Equals(expectedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Value backup contains keys outside its exact parent.");

                var assignmentLines = lines
                    .Where(line =>
                        !string.IsNullOrWhiteSpace(line) &&
                        !line.StartsWith(";", StringComparison.Ordinal) &&
                        !line.StartsWith("[", StringComparison.Ordinal) &&
                        !line.StartsWith("Windows Registry Editor", StringComparison.Ordinal) &&
                        !line.StartsWith("  ", StringComparison.Ordinal))
                    .ToList();
                var expectedName = valueName.Length == 0
                    ? "@="
                    : $"\"{valueName.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"=";
                if (assignmentLines.Count != 1 ||
                    !assignmentLines[0].StartsWith(
                        expectedName,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Value backup does not contain exactly the bound value.");
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static string DecodeRegistryDocument(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0xff || bytes[1] != 0xfe)
            throw new InvalidDataException("Registry backup must be UTF-16 LE with a BOM.");
        return new UnicodeEncoding(
            bigEndian: false,
            byteOrderMark: true,
            throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2);
    }

    private static string ExpandHive(string hive)
        => hive.ToUpperInvariant() switch
        {
            "HKCU" => "HKEY_CURRENT_USER",
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCR" => "HKEY_CLASSES_ROOT",
            "HKU" => "HKEY_USERS",
            _ => throw new InvalidDataException($"Unsupported registry hive: {hive}"),
        };
}
