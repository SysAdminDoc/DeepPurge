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
    BackupFailed,
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
    string? ErrorMessage = null)
{
    public bool Deleted => Status == RegistryDeletionStatus.Deleted;
}

/// <summary>
/// Single choke point for destructive registry key/value cleanup.
/// </summary>
public static class RegistryDeletion
{
    private static readonly BackupManager Backup = new();

    public static RegistryDeletionResult DeleteKeyTree(
        string registryPath,
        string operation,
        bool dryRun = false)
    {
        if (!TryParseKeyPath(registryPath, out var target))
            return Result(RegistryDeletionStatus.SkippedMalformedPath, registryPath);

        if (!SafetyGuard.IsRegistryPathSafeToDelete(target.CanonicalPath))
            return Result(RegistryDeletionStatus.SkippedUnsafePath, target.CanonicalPath);

        try
        {
            using var baseKey = global::Microsoft.Win32.RegistryKey.OpenBaseKey(target.Hive, RegistryView.Default);
            if (!TargetKeyExistsAndIsPlain(baseKey, target.SubKey, target.CanonicalPath, out var status))
                return Result(status, target.CanonicalPath);

            if (dryRun) return Result(RegistryDeletionStatus.DryRun, target.CanonicalPath);

            var backupPath = Backup.BackupRegistryKey(target.CanonicalPath);
            if (string.IsNullOrEmpty(backupPath))
                return Result(RegistryDeletionStatus.BackupFailed, target.CanonicalPath);

            if (!TargetKeyExistsAndIsPlain(baseKey, target.SubKey, target.CanonicalPath, out status))
                return Result(status, target.CanonicalPath, backupPath);

            baseKey.DeleteSubKeyTree(target.SubKey, throwOnMissingSubKey: false);
            DeletionManifest.RecordRegistry(target.CanonicalPath, operation);
            return Result(RegistryDeletionStatus.Deleted, target.CanonicalPath, backupPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry key delete failed '{target.CanonicalPath}': {ex.Message}");
            return Result(RegistryDeletionStatus.Failed, target.CanonicalPath, errorMessage: ex.Message);
        }
    }

    public static RegistryDeletionResult DeleteValue(
        string registryValuePath,
        string operation,
        bool dryRun = false)
    {
        if (!TryParseValuePath(registryValuePath, out var target, out var valueName))
            return Result(RegistryDeletionStatus.SkippedMalformedPath, registryValuePath);

        var canonicalValuePath = $"{target.CanonicalPath}\\{valueName}";
        if (!SafetyGuard.IsRegistryPathSafeToDelete(canonicalValuePath))
            return Result(RegistryDeletionStatus.SkippedUnsafePath, canonicalValuePath);

        try
        {
            using var baseKey = global::Microsoft.Win32.RegistryKey.OpenBaseKey(target.Hive, RegistryView.Default);
            using var readKey = baseKey.OpenSubKey(target.SubKey);
            if (readKey == null) return Result(RegistryDeletionStatus.SkippedMissing, canonicalValuePath);
            if (SafetyGuard.IsRegistrySymlink(readKey))
            {
                Log.Warn($"Skipping registry symlink: {target.CanonicalPath}");
                return Result(RegistryDeletionStatus.SkippedSymlink, canonicalValuePath);
            }
            if (!readKey.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                return Result(RegistryDeletionStatus.SkippedMissing, canonicalValuePath);

            if (dryRun) return Result(RegistryDeletionStatus.DryRun, canonicalValuePath);

            var backupPath = Backup.BackupRegistryKey(target.CanonicalPath);
            if (string.IsNullOrEmpty(backupPath))
                return Result(RegistryDeletionStatus.BackupFailed, canonicalValuePath);

            using var writeKey = baseKey.OpenSubKey(target.SubKey, writable: true);
            if (writeKey == null) return Result(RegistryDeletionStatus.SkippedMissing, canonicalValuePath, backupPath);
            if (SafetyGuard.IsRegistrySymlink(writeKey))
            {
                Log.Warn($"Skipping registry symlink: {target.CanonicalPath}");
                return Result(RegistryDeletionStatus.SkippedSymlink, canonicalValuePath, backupPath);
            }

            writeKey.DeleteValue(valueName, throwOnMissingValue: false);
            DeletionManifest.RecordRegistry(canonicalValuePath, operation);
            return Result(RegistryDeletionStatus.Deleted, canonicalValuePath, backupPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry value delete failed '{canonicalValuePath}': {ex.Message}");
            return Result(RegistryDeletionStatus.Failed, canonicalValuePath, errorMessage: ex.Message);
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

    private static bool TargetKeyExistsAndIsPlain(
        RegistryKey baseKey,
        string subKey,
        string canonicalPath,
        out RegistryDeletionStatus status)
    {
        using var checkKey = baseKey.OpenSubKey(subKey);
        if (checkKey == null)
        {
            status = RegistryDeletionStatus.SkippedMissing;
            return false;
        }

        if (SafetyGuard.IsRegistrySymlink(checkKey))
        {
            Log.Warn($"Skipping registry symlink: {canonicalPath}");
            status = RegistryDeletionStatus.SkippedSymlink;
            return false;
        }

        status = RegistryDeletionStatus.Deleted;
        return true;
    }

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
        string? errorMessage = null)
        => new(status, path, backupPath, errorMessage);
}
