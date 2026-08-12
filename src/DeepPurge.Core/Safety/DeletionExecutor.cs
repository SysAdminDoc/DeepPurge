namespace DeepPurge.Core.Safety;

/// <summary>
/// A single policy boundary for filesystem cleanup. Callers describe an
/// already-selected object; this class applies the requested mode, performs
/// the final safety/type/reparse checks, and returns a typed result. The
/// handle-bound primitives are responsible for writing manifest entries only
/// after the exact operation succeeds.
/// </summary>
public sealed class DeletionExecutor
{
    public DeletionResult Execute(
        DeletionRequest request,
        DeleteOptions options,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new DeletionResult(
                request.Path,
                DeletionOutcomeKind.Cancelled,
                request.ExpectedSizeBytes,
                request.Operation,
                "Cancellation requested.");

        if (string.IsNullOrWhiteSpace(request.Path))
            return Skipped(request, "Path is empty.");

        if (!SafetyGuard.IsPathSafeToDelete(request.Path))
            return Skipped(request, "The path is protected or invalid.");

        var exists = request.IsDirectory
            ? Directory.Exists(request.Path)
            : File.Exists(request.Path);
        if (!exists)
            return Skipped(request, "Missing.");

        if (SafetyGuard.IsReparsePoint(request.Path))
            return Skipped(request, "Reparse point.");

        var actualTypeMatches = request.IsDirectory
            ? Directory.Exists(request.Path)
            : File.Exists(request.Path);
        if (!actualTypeMatches)
            return Skipped(request, "The target type changed.");

        var size = GetSize(request);
        if (options.DryRun)
        {
            return new DeletionResult(
                request.Path,
                DeletionOutcomeKind.Preview,
                size,
                request.Operation,
                "Would be deleted.",
                Recoverable: options.UseRecycleBin && !options.SecureDelete);
        }

        try
        {
            string reason;
            bool deleted;
            DeletionOutcomeKind outcome;
            bool recoverable;

            if (options.SecureDelete)
            {
                deleted = request.IsDirectory
                    ? HandleBoundFileOperations.SecureDeleteDirectoryTree(
                        request.Path,
                        request.Operation,
                        out reason)
                    : HandleBoundFileOperations.SecureDeleteFile(
                        request.Path,
                        request.Operation,
                        out reason);
                outcome = DeletionOutcomeKind.SecurelyDeleted;
                recoverable = false;
            }
            else if (options.UseRecycleBin)
            {
                deleted = SafetyGuard.SafeMoveToRecycleBin(
                    request.Path,
                    request.IsDirectory,
                    request.Operation,
                    out reason);
                outcome = DeletionOutcomeKind.Recycled;
                recoverable = true;
            }
            else
            {
                deleted = request.IsDirectory
                    ? HandleBoundFileOperations.DeleteDirectoryTree(
                        request.Path,
                        request.Operation,
                        out reason)
                    : HandleBoundFileOperations.DeleteFile(
                        request.Path,
                        request.Operation,
                        out reason,
                        idempotentMissing: false);
                outcome = DeletionOutcomeKind.PermanentlyDeleted;
                recoverable = false;
            }

            if (!deleted)
            {
                return new DeletionResult(
                    request.Path,
                    DeletionOutcomeKind.Failed,
                    size,
                    request.Operation,
                    string.IsNullOrWhiteSpace(reason) ? "Delete failed." : reason);
            }

            return new DeletionResult(
                request.Path,
                outcome,
                size,
                request.Operation,
                null,
                recoverable);
        }
        catch (OperationCanceledException)
        {
            return new DeletionResult(
                request.Path,
                DeletionOutcomeKind.Cancelled,
                size,
                request.Operation,
                "Cancellation requested.");
        }
        catch (Exception ex)
        {
            return new DeletionResult(
                request.Path,
                DeletionOutcomeKind.Failed,
                size,
                request.Operation,
                ex.Message);
        }
    }

    public DeletionBatchResult Execute(
        IEnumerable<DeletionRequest> requests,
        DeleteOptions options,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = requests.ToList();
        var results = new List<DeletionResult>(list.Count);
        long bytes = 0;

        for (var i = 0; i < list.Count; i++)
        {
            var result = Execute(list[i], options, cancellationToken);
            results.Add(result);
            if (result.IsConfirmed || result.IsPreview)
                bytes += result.SizeBytes;

            progress?.Report(new DeleteProgress(
                i + 1,
                list.Count,
                bytes,
                result.Path,
                !result.IsConfirmed && !result.IsPreview));

            if (result.Outcome == DeletionOutcomeKind.Cancelled)
            {
                for (var remaining = i + 1; remaining < list.Count; remaining++)
                {
                    var cancelled = new DeletionResult(
                        list[remaining].Path,
                        DeletionOutcomeKind.Cancelled,
                        list[remaining].ExpectedSizeBytes,
                        list[remaining].Operation,
                        "Cancellation requested.");
                    results.Add(cancelled);
                }

                break;
            }
        }

        return new DeletionBatchResult(results, options.DryRun);
    }

    public static DeletionResult Skipped(
        DeletionRequest request,
        string reason)
        => new(
            request.Path,
            DeletionOutcomeKind.Skipped,
            request.ExpectedSizeBytes,
            request.Operation,
            reason);

    public static DeletionResult ConfirmedExternal(
        string path,
        long sizeBytes,
        string operation,
        bool recoverable = false)
        => new(
            path,
            DeletionOutcomeKind.PermanentlyDeleted,
            sizeBytes,
            operation,
            null,
            recoverable);

    public static DeletionResult FailedExternal(
        string path,
        string operation,
        string reason,
        long sizeBytes = 0)
        => new(path, DeletionOutcomeKind.Failed, sizeBytes, operation, reason);

    private static long GetSize(DeletionRequest request)
    {
        if (request.ExpectedSizeBytes > 0)
            return request.ExpectedSizeBytes;

        if (request.IsDirectory)
            return 0;

        try { return new FileInfo(request.Path).Length; }
        catch { return 0; }
    }
}

public sealed record DeletionRequest(
    string Path,
    bool IsDirectory = false,
    long ExpectedSizeBytes = 0,
    string Operation = "delete");

public sealed record DeletionBatchResult(
    IReadOnlyList<DeletionResult> Results,
    bool DryRun)
{
    public DeleteSummary Summary => DeleteSummary.FromResults(Results, DryRun);

    public bool HasFailures => Results.Any(r => r.IsFailure);
}
