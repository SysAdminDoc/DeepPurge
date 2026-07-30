using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;
using global::Microsoft.Win32;

namespace DeepPurge.Core.Registry;

public enum RegistryDeletionStatus
{
    Deleted,
    DryRun,
    SkippedMalformedPath,
    SkippedUnsafePath,
    SkippedMissing,
    SkippedSymlink,
    SkippedDrift,
    BackupFailed,
    ManifestFailed,
    Failed,
}

public readonly record struct RegistryPathParts(
    RegistryHive Hive,
    string HiveName,
    string SubKey)
{
    public string CanonicalPath => $"{HiveName}\\{SubKey}";
}

public readonly record struct RegistryDeletionResult(
    RegistryDeletionStatus Status,
    string Path,
    string? BackupPath = null,
    string? ErrorMessage = null,
    string? OperationId = null,
    string? BackupSha256 = null)
{
    public bool Deleted => Status == RegistryDeletionStatus.Deleted;
}

/// <summary>
/// Single choke point for destructive registry key/value cleanup. Paths are
/// resolved once through no-follow handles; backup, identity revalidation, and
/// mutation all operate on those handles.
/// </summary>
public static class RegistryDeletion
{
    public static RegistryDeletionResult DeleteKeyTree(
        string registryPath,
        string operation,
        bool dryRun = false)
        => DeleteKeyTree(
            registryPath,
            operation,
            dryRun,
            RegistryBackupStore.Production);

    internal static RegistryDeletionResult DeleteKeyTree(
        string registryPath,
        string operation,
        bool dryRun,
        RegistryBackupStore backupStore)
    {
        if (!TryParseKeyPath(registryPath, out var target))
            return Result(RegistryDeletionStatus.SkippedMalformedPath, registryPath);

        if (!SafetyGuard.IsRegistryPathSafeToDelete(target.CanonicalPath))
            return Result(RegistryDeletionStatus.SkippedUnsafePath, target.CanonicalPath);

        RegistryBackupArtifact? artifact = null;
        var prepared = false;
        try
        {
            var openStatus = RegistryNative.TryOpenForKeyDeletion(target, out var chain);
            if (openStatus != RegistryOpenStatus.Opened)
                return Result(MapOpenStatus(openStatus), target.CanonicalPath);

            using (chain)
            {
                var before = RegistryNative.CaptureTree(target, chain!.Target);
                if (dryRun)
                    return Result(RegistryDeletionStatus.DryRun, target.CanonicalPath);

                artifact = backupStore.Create(before);
                if (artifact == null ||
                    backupStore.RequiresTrustedAcl && !artifact.BackupAclTrusted)
                    return Result(RegistryDeletionStatus.BackupFailed, target.CanonicalPath);

                var afterBackup = RegistryNative.CaptureTree(target, chain.Target);
                if (!before.ObjectIdentity.Equals(
                        afterBackup.ObjectIdentity,
                        StringComparison.Ordinal))
                {
                    backupStore.Discard(artifact);
                    return Result(
                        RegistryDeletionStatus.SkippedDrift,
                        target.CanonicalPath,
                        errorMessage: "The registry tree changed while its rollback artifact was created.");
                }

                prepared = DeletionManifest.RecordRegistryTransaction(
                    target.CanonicalPath,
                    operation,
                    artifact,
                    "Prepared");
                if (!prepared)
                {
                    backupStore.Discard(artifact);
                    return Result(
                        RegistryDeletionStatus.ManifestFailed,
                        target.CanonicalPath,
                        errorMessage: "The write-ahead deletion record could not be persisted.");
                }

                RegistryNative.DeleteTree(chain.Target);
            }

            DeletionManifest.RecordRegistryTransaction(
                target.CanonicalPath,
                operation,
                artifact,
                "Succeeded");
            return Result(
                RegistryDeletionStatus.Deleted,
                target.CanonicalPath,
                artifact.BackupPath,
                operationId: artifact.OperationId,
                backupSha256: artifact.BackupSha256);
        }
        catch (Exception ex)
        {
            if (artifact != null)
            {
                if (prepared)
                {
                    DeletionManifest.RecordRegistryTransaction(
                        target.CanonicalPath,
                        operation,
                        artifact,
                        "Failed");
                }
                else
                {
                    backupStore.Discard(artifact);
                }
            }

            Log.Warn($"Registry key delete failed '{target.CanonicalPath}': {ex.Message}");
            return Result(
                RegistryDeletionStatus.Failed,
                target.CanonicalPath,
                artifact?.BackupPath,
                ex.Message,
                artifact?.OperationId,
                artifact?.BackupSha256);
        }
    }

    public static RegistryDeletionResult DeleteValue(
        string registryValuePath,
        string operation,
        bool dryRun = false)
        => DeleteValue(
            registryValuePath,
            operation,
            dryRun,
            RegistryBackupStore.Production);

    internal static RegistryDeletionResult DeleteValue(
        string registryValuePath,
        string operation,
        bool dryRun,
        RegistryBackupStore backupStore)
    {
        if (!TryParseValuePath(registryValuePath, out var target, out var valueName))
            return Result(RegistryDeletionStatus.SkippedMalformedPath, registryValuePath);

        var canonicalValuePath = $"{target.CanonicalPath}\\{valueName}";
        if (!SafetyGuard.IsRegistryPathSafeToDelete(canonicalValuePath))
            return Result(RegistryDeletionStatus.SkippedUnsafePath, canonicalValuePath);

        RegistryBackupArtifact? artifact = null;
        var prepared = false;
        try
        {
            var openStatus = RegistryNative.TryOpenForValueDeletion(target, out var chain);
            if (openStatus != RegistryOpenStatus.Opened)
                return Result(MapOpenStatus(openStatus), canonicalValuePath);

            using (chain)
            {
                var before = RegistryNative.CaptureValue(target, valueName, chain!.Target);
                if (before == null)
                    return Result(RegistryDeletionStatus.SkippedMissing, canonicalValuePath);
                if (dryRun)
                    return Result(RegistryDeletionStatus.DryRun, canonicalValuePath);

                artifact = backupStore.Create(before);
                if (artifact == null ||
                    backupStore.RequiresTrustedAcl && !artifact.BackupAclTrusted)
                    return Result(RegistryDeletionStatus.BackupFailed, canonicalValuePath);

                var afterBackup = RegistryNative.CaptureValue(target, valueName, chain.Target);
                if (afterBackup == null ||
                    !before.ObjectIdentity.Equals(
                        afterBackup.ObjectIdentity,
                        StringComparison.Ordinal))
                {
                    backupStore.Discard(artifact);
                    return Result(
                        RegistryDeletionStatus.SkippedDrift,
                        canonicalValuePath,
                        errorMessage: "The registry value changed while its rollback artifact was created.");
                }

                prepared = DeletionManifest.RecordRegistryTransaction(
                    canonicalValuePath,
                    operation,
                    artifact,
                    "Prepared");
                if (!prepared)
                {
                    backupStore.Discard(artifact);
                    return Result(
                        RegistryDeletionStatus.ManifestFailed,
                        canonicalValuePath,
                        errorMessage: "The write-ahead deletion record could not be persisted.");
                }

                if (!RegistryNative.DeleteValue(chain.Target, valueName))
                    throw new InvalidOperationException(
                        "The registry value disappeared immediately before deletion.");
            }

            DeletionManifest.RecordRegistryTransaction(
                canonicalValuePath,
                operation,
                artifact,
                "Succeeded");
            return Result(
                RegistryDeletionStatus.Deleted,
                canonicalValuePath,
                artifact.BackupPath,
                operationId: artifact.OperationId,
                backupSha256: artifact.BackupSha256);
        }
        catch (Exception ex)
        {
            if (artifact != null)
            {
                if (prepared)
                {
                    DeletionManifest.RecordRegistryTransaction(
                        canonicalValuePath,
                        operation,
                        artifact,
                        "Failed");
                }
                else
                {
                    backupStore.Discard(artifact);
                }
            }

            Log.Warn($"Registry value delete failed '{canonicalValuePath}': {ex.Message}");
            return Result(
                RegistryDeletionStatus.Failed,
                canonicalValuePath,
                artifact?.BackupPath,
                ex.Message,
                artifact?.OperationId,
                artifact?.BackupSha256);
        }
    }

    public static bool TryParseKeyPath(string registryPath, out RegistryPathParts target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(registryPath)) return false;

        var trimmed = registryPath.Trim().TrimEnd('\\');
        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Any(c => c is '"' or '<' or '>' or '|' or '\r' or '\n'))
            return false;

        var split = trimmed.IndexOf('\\');
        if (split <= 0 || split == trimmed.Length - 1) return false;

        var hiveToken = trimmed[..split];
        var subKey = trimmed[(split + 1)..];
        if (subKey.Length == 0 || subKey.Split('\\').Any(string.IsNullOrWhiteSpace))
            return false;

        if (!TryResolveHive(hiveToken, out var hive, out var hiveName))
            return false;

        target = new RegistryPathParts(hive, hiveName, subKey);
        return true;
    }

    public static bool TryParseValuePath(
        string registryValuePath,
        out RegistryPathParts keyTarget,
        out string valueName)
    {
        keyTarget = default;
        valueName = string.Empty;
        if (!TryParseKeyPath(registryValuePath, out var fullTarget)) return false;

        var lastSlash = fullTarget.SubKey.LastIndexOf('\\');
        if (lastSlash <= 0 || lastSlash == fullTarget.SubKey.Length - 1) return false;

        valueName = fullTarget.SubKey[(lastSlash + 1)..];
        if (string.IsNullOrWhiteSpace(valueName)) return false;

        keyTarget = new RegistryPathParts(
            fullTarget.Hive,
            fullTarget.HiveName,
            fullTarget.SubKey[..lastSlash]);
        return true;
    }

    private static RegistryDeletionStatus MapOpenStatus(RegistryOpenStatus status)
        => status switch
        {
            RegistryOpenStatus.Missing => RegistryDeletionStatus.SkippedMissing,
            RegistryOpenStatus.SymbolicLink => RegistryDeletionStatus.SkippedSymlink,
            _ => RegistryDeletionStatus.Failed,
        };

    private static bool TryResolveHive(string hiveToken, out RegistryHive hive, out string hiveName)
    {
        switch (hiveToken.ToUpperInvariant())
        {
            case "HKCU":
            case "HKEY_CURRENT_USER":
                hive = RegistryHive.CurrentUser;
                hiveName = "HKCU";
                return true;
            case "HKLM":
            case "HKEY_LOCAL_MACHINE":
                hive = RegistryHive.LocalMachine;
                hiveName = "HKLM";
                return true;
            case "HKCR":
            case "HKEY_CLASSES_ROOT":
                hive = RegistryHive.ClassesRoot;
                hiveName = "HKCR";
                return true;
            case "HKU":
            case "HKEY_USERS":
                hive = RegistryHive.Users;
                hiveName = "HKU";
                return true;
            default:
                hive = default;
                hiveName = string.Empty;
                return false;
        }
    }

    private static RegistryDeletionResult Result(
        RegistryDeletionStatus status,
        string path,
        string? backupPath = null,
        string? errorMessage = null,
        string? operationId = null,
        string? backupSha256 = null)
        => new(status, path, backupPath, errorMessage, operationId, backupSha256);
}
