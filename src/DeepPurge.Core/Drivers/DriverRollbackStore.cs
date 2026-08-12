using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DeepPurge.Core.App;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Drivers;

/// <summary>
/// Owns the export-first driver mutation contract. Every removal has a
/// package artifact, an exact file/hash manifest, and an append-only ledger
/// entry before pnputil is allowed to delete anything.
/// </summary>
public sealed class DriverRollbackStore
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly string _root;
    private readonly DriverOperationLedger _ledger;
    private readonly bool _requireTrustedAcl;

    public DriverRollbackStore(
        string? rootDirectory = null,
        DriverOperationLedger? ledger = null,
        bool requireTrustedAcl = true)
    {
        _root = Path.GetFullPath(rootDirectory ?? DataPaths.DriverBackups);
        _ledger = ledger ?? new DriverOperationLedger();
        _requireTrustedAcl = requireTrustedAcl;
    }

    public string RootDirectory => _root;
    public DriverOperationLedger Ledger => _ledger;

    public async Task<DriverMutationResult> DeleteAsync(
        DriverPackage package,
        IDriverPackageTool tool,
        bool force = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N");
        if (!TryValidatePackage(package, out var reason))
            return RecordAndReturn(new(
                operationId,
                package.PublishedName,
                DriverMutationOutcome.Skipped,
                null,
                "",
                reason));

        if (dryRun)
        {
            var preview = new DriverMutationResult(
                operationId,
                package.PublishedName,
                DriverMutationOutcome.Preview,
                null,
                "",
                "Preview only; the package will be exported before a real removal.");
            TryRecord(preview, null);
            return preview;
        }

        DriverRollbackArtifact? artifact;
        try
        {
            artifact = await ExportAsync(
                package,
                tool,
                operationId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var canceled = new DriverMutationResult(
                operationId,
                package.PublishedName,
                DriverMutationOutcome.Cancelled,
                null,
                "",
                "Cancellation requested before driver removal.");
            TryRecord(canceled, null);
            return canceled;
        }

        if (artifact is null)
        {
            var failure = new DriverMutationResult(
                operationId,
                package.PublishedName,
                DriverMutationOutcome.Failed,
                null,
                "",
                "Driver export failed; removal was not attempted.");
            return RecordAndReturn(failure);
        }

        var args = new List<string> { "/delete-driver", package.PublishedName, "/uninstall" };
        if (force) args.Add("/force");

        DriverToolResult command;
        try
        {
            command = await tool.RunAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var canceled = new DriverMutationResult(
                operationId,
                package.PublishedName,
                DriverMutationOutcome.Cancelled,
                artifact,
                "",
                "Cancellation requested after export; the rollback artifact was retained.");
            TryRecord(canceled, artifact);
            return canceled;
        }

        var output = command.CombinedOutput;
        var outcome = command.Canceled
            ? DriverMutationOutcome.Cancelled
            : command.Succeeded
                ? DriverMutationOutcome.Deleted
                : DriverMutationOutcome.Failed;
        var result = new DriverMutationResult(
            operationId,
            package.PublishedName,
            outcome,
            artifact,
            output,
            outcome == DriverMutationOutcome.Deleted
                ? null
                : "pnputil did not confirm driver package removal; the rollback artifact was retained.");
        TryRecord(result, artifact);
        return result;
    }

    public async Task<DriverMutationResult> RollbackAsync(
        string operationId,
        IDriverPackageTool tool,
        CancellationToken cancellationToken = default)
    {
        var entry = _ledger.LoadLatest(operationId);
        if (entry is null)
            return new(
                operationId,
                "",
                DriverMutationOutcome.Skipped,
                null,
                "",
                "No driver operation with that identity was found.");

        if (entry.Artifact is null || entry.Outcome is not (
            DriverMutationOutcome.Deleted or DriverMutationOutcome.Failed or DriverMutationOutcome.Cancelled))
        {
            return new(
                operationId,
                entry.PublishedName,
                DriverMutationOutcome.Skipped,
                entry.Artifact,
                "",
                "The operation has no retained package eligible for rollback.");
        }

        if (!TryValidateArtifact(entry.Artifact, out var reason))
        {
            var invalid = new DriverMutationResult(
                operationId,
                entry.PublishedName,
                DriverMutationOutcome.Failed,
                entry.Artifact,
                "",
                $"Rollback artifact validation failed: {reason}");
            TryRecord(invalid, entry.Artifact);
            return invalid;
        }

        DriverToolResult command;
        try
        {
            command = await tool.RunAsync(
                new[] { "/add-driver", entry.Artifact.InfPath, "/install" },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var canceled = new DriverMutationResult(
                operationId,
                entry.PublishedName,
                DriverMutationOutcome.Cancelled,
                entry.Artifact,
                "",
                "Cancellation requested; the rollback artifact was not changed.");
            TryRecord(canceled, entry.Artifact);
            return canceled;
        }

        var output = command.CombinedOutput;
        var outcome = command.Canceled
            ? DriverMutationOutcome.Cancelled
            : command.Succeeded
                ? DriverMutationOutcome.Restored
                : DriverMutationOutcome.Failed;
        var result = new DriverMutationResult(
            operationId,
            entry.PublishedName,
            outcome,
            entry.Artifact,
            output,
            outcome == DriverMutationOutcome.Restored
                ? null
                : "pnputil did not confirm driver package restoration; the artifact remains available for retry.");
        TryRecord(result, entry.Artifact);
        return result;
    }

    private async Task<DriverRollbackArtifact?> ExportAsync(
        DriverPackage package,
        IDriverPackageTool tool,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (!TryPrepareRoot(out var rootReason))
            return null;

        var backupDirectory = Path.Combine(_root, operationId);
        try
        {
            Directory.CreateDirectory(backupDirectory);
            if (!IsSafeDirectory(backupDirectory) || !IsWithinRoot(backupDirectory, _root))
                return null;

            var command = await tool.RunAsync(
                new[] { "/export-driver", package.PublishedName, backupDirectory },
                cancellationToken).ConfigureAwait(false);
            if (!command.Succeeded)
            {
                CleanupDirectory(backupDirectory);
                return null;
            }

            if (!TryBuildArtifact(
                    package,
                    operationId,
                    backupDirectory,
                    out var artifact,
                    out var reason))
            {
                CleanupDirectory(backupDirectory);
                return null;
            }

            var exported = new DriverMutationResult(
                operationId,
                package.PublishedName,
                DriverMutationOutcome.Exported,
                artifact,
                command.CombinedOutput,
                null);
            if (!TryRecord(exported, artifact, out _))
            {
                CleanupDirectory(backupDirectory);
                return null;
            }

            return artifact;
        }
        catch (OperationCanceledException)
        {
            CleanupDirectory(backupDirectory);
            throw;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Driver export failed: {ex.Message}");
            CleanupDirectory(backupDirectory);
            return null;
        }
    }

    private bool TryBuildArtifact(
        DriverPackage package,
        string operationId,
        string backupDirectory,
        out DriverRollbackArtifact? artifact,
        out string reason)
    {
        artifact = null;
        var files = new List<DriverFileHash>();
        var pending = new Stack<string>();
        pending.Push(backupDirectory);

        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (!IsSafeDirectory(directory))
                {
                    reason = $"Exported directory is missing or reparse-backed: {directory}";
                    return false;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsWithinRoot(file, backupDirectory) || SafetyGuard.IsReparsePoint(file))
                    {
                        reason = $"Exported file is outside the protected package scope: {file}";
                        return false;
                    }

                    if (!TryHashFile(file, out var size, out var hash, out reason))
                        return false;
                    files.Add(new DriverFileHash(
                        Path.GetRelativePath(backupDirectory, file),
                        size,
                        hash));
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsWithinRoot(child, backupDirectory) || !IsSafeDirectory(child))
                    {
                        reason = $"Exported directory is outside the protected package scope: {child}";
                        return false;
                    }
                    pending.Push(child);
                }
            }
        }
        catch (Exception ex)
        {
            reason = $"Could not inspect exported driver files: {ex.Message}";
            return false;
        }

        if (files.Count == 0)
        {
            reason = "pnputil reported success but exported no files.";
            return false;
        }

        files = files.OrderBy(f => f.RelativePath, PathComparer).ToList();
        var inf = files.FirstOrDefault(f => string.Equals(
            Path.GetFileName(f.RelativePath),
            Path.GetFileName(package.OriginalName),
            StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => string.Equals(
                Path.GetExtension(f.RelativePath), ".inf", StringComparison.OrdinalIgnoreCase));
        if (inf is null)
        {
            reason = "The exported package contains no INF file for rollback.";
            return false;
        }

        artifact = new DriverRollbackArtifact(
            operationId,
            package.PublishedName,
            package.OriginalName,
            backupDirectory,
            inf.RelativePath,
            inf.Sha256,
            ComputePackageHash(files),
            files,
            DateTimeOffset.UtcNow);
        reason = "";
        return true;
    }

    private bool TryValidateArtifact(DriverRollbackArtifact artifact, out string reason)
    {
        reason = "";
        if (!IsWithinRoot(artifact.BackupDirectory, _root) ||
            !IsSafeDirectory(artifact.BackupDirectory))
        {
            reason = "The backup directory is outside protected driver storage or is a reparse point.";
            return false;
        }

        if (!RegexValidPublishedName(artifact.PublishedName))
        {
            reason = "The retained package identity is not a valid oem*.inf name.";
            return false;
        }

        var actual = new List<DriverFileHash>();
        var pending = new Stack<string>();
        pending.Push(artifact.BackupDirectory);
        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (!IsSafeDirectory(directory))
                {
                    reason = "The rollback package contains a reparse-backed directory.";
                    return false;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsWithinRoot(file, artifact.BackupDirectory) || SafetyGuard.IsReparsePoint(file) ||
                        !TryHashFile(file, out var size, out var hash, out reason))
                        return false;
                    actual.Add(new DriverFileHash(
                        Path.GetRelativePath(artifact.BackupDirectory, file), size, hash));
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsWithinRoot(child, artifact.BackupDirectory) || !IsSafeDirectory(child))
                    {
                        reason = "The rollback package contains an unsafe directory.";
                        return false;
                    }
                    pending.Push(child);
                }
            }
        }
        catch (Exception ex)
        {
            reason = $"Could not inspect rollback package: {ex.Message}";
            return false;
        }

        actual = actual.OrderBy(f => f.RelativePath, PathComparer).ToList();
        var expected = artifact.Files.OrderBy(f => f.RelativePath, PathComparer).ToList();
        if (actual.Count != expected.Count ||
            actual.Zip(expected).Any(pair =>
                !string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                pair.First.SizeBytes != pair.Second.SizeBytes ||
                !string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "A rollback file is missing, changed, or was added after export.";
            return false;
        }

        if (!string.Equals(ComputePackageHash(actual), artifact.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The rollback package hash does not match the recorded export.";
            return false;
        }

        var inf = actual.FirstOrDefault(f => string.Equals(
            f.RelativePath,
            artifact.InfRelativePath,
            StringComparison.OrdinalIgnoreCase));
        if (inf is null || !string.Equals(inf.Sha256, artifact.InfSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The recorded INF is missing or changed.";
            return false;
        }

        return true;
    }

    private DriverMutationResult RecordAndReturn(DriverMutationResult result)
    {
        TryRecord(result, result.Artifact);
        return result;
    }

    private bool TryRecord(DriverMutationResult result, DriverRollbackArtifact? artifact)
        => TryRecord(result, artifact, out _);

    private bool TryRecord(
        DriverMutationResult result,
        DriverRollbackArtifact? artifact,
        out string reason)
    {
        return _ledger.TryRecord(
            new DriverOperationEntry(
                SchemaVersion: 1,
                result.OperationId,
                result.PublishedName,
                artifact?.OriginalName ?? "",
                result.Outcome,
                artifact,
                result.Output,
                result.Reason,
                DateTimeOffset.UtcNow),
            out reason);
    }

    private bool TryPrepareRoot(out string reason)
    {
        try
        {
            if (_requireTrustedAcl && !UserIdentity.IsProcessElevated)
            {
                reason = "Driver rollback storage requires an elevated process.";
                return false;
            }

            Directory.CreateDirectory(_root);
            if (!IsSafeDirectory(_root))
            {
                reason = "Driver backup storage is missing or reparse-backed.";
                return false;
            }

            if (_requireTrustedAcl &&
                !HandleBoundFileOperations.TrySetSecurityExact(
                    _root,
                    expectedDirectory: true,
                    CreateTrustedDirectorySecurity(),
                    out reason))
                return false;

            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Could not prepare driver backup storage: {ex.Message}";
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

        var desktopUser = UserIdentity.RealUserSid;
        if (!string.IsNullOrWhiteSpace(desktopUser))
        {
            try
            {
                var sid = new SecurityIdentifier(desktopUser);
                if (sid != AdministratorsSid && sid != SystemSid)
                    security.AddAccessRule(new FileSystemAccessRule(
                        sid,
                        FileSystemRights.ReadAndExecute,
                        inheritance,
                        PropagationFlags.None,
                        AccessControlType.Allow));
            }
            catch { /* owner/system rules still protect the store */ }
        }

        return security;
    }

    private static readonly SecurityIdentifier AdministratorsSid = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        domainSid: null);
    private static readonly SecurityIdentifier SystemSid = new(
        WellKnownSidType.LocalSystemSid,
        domainSid: null);

    private static bool TryValidatePackage(DriverPackage package, out string reason)
    {
        if (package is null)
        {
            reason = "No driver package was selected.";
            return false;
        }
        if (!RegexValidPublishedName(package.PublishedName))
        {
            reason = "The package identity is not a valid oem*.inf name.";
            return false;
        }
        if (package.IsProtected)
        {
            reason = string.IsNullOrWhiteSpace(package.SafetyReason)
                ? "This driver package is protected and cannot be removed."
                : package.SafetyReason;
            return false;
        }
        if (package.IsExcluded)
        {
            reason = "This driver package is excluded by policy and cannot be removed.";
            return false;
        }
        reason = "";
        return true;
    }

    private static bool RegexValidPublishedName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           System.Text.RegularExpressions.Regex.IsMatch(
               name,
               @"^oem\d+\.inf$",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsSafeDirectory(string path)
        => Directory.Exists(path) && !SafetyGuard.IsReparsePoint(path);

    private static bool IsWithinRoot(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(
                       fullRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TryHashFile(
        string path,
        out long size,
        out string hash,
        out string reason)
    {
        size = 0;
        hash = "";
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists || (before.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = "The exported file is missing or reparse-backed.";
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            var bytes = SHA256.HashData(stream);
            var after = new FileInfo(path);
            if (!after.Exists || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                reason = "An exported file changed while it was being hashed.";
                return false;
            }

            size = before.Length;
            hash = Convert.ToHexString(bytes).ToLowerInvariant();
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Could not hash exported file: {ex.Message}";
            return false;
        }
    }

    private static string ComputePackageHash(IEnumerable<DriverFileHash> files)
    {
        var canonical = string.Join(
            "\n",
            files.OrderBy(f => f.RelativePath, PathComparer)
                .Select(f => $"{f.RelativePath}\0{f.SizeBytes}\0{f.Sha256.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                HandleBoundFileOperations.DeleteDirectoryTreeWithinScope(
                    path,
                    Path.GetDirectoryName(path) ?? path,
                    out _);
        }
        catch { /* a failed export must never prevent reporting the failure */ }
    }
}
