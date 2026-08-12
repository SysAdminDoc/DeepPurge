namespace DeepPurge.Core.Safety;

/// <summary>
/// Options that apply to any destructive operation (junk, evidence, leftovers,
/// autoruns). Carrying these as a single struct prevents method-signature
/// sprawl — every new flag used to grow every deletion API by one argument.
///
/// - <see cref="DryRun"/>: enumerate, size, and report what *would* be deleted
///   without touching the filesystem/registry. The default for any new feature
///   users might not understand.
/// - <see cref="SecureDelete"/>: wipe files via <see cref="Safety.SecureDelete"/>
///   before unlink — privacy-grade, slower, no recycle-bin recovery.
/// - <see cref="UseRecycleBin"/>: prefer SHFileOperation(FO_DELETE) with
///   FOF_ALLOWUNDO. Mutually exclusive with <see cref="SecureDelete"/>.
/// </summary>
public readonly record struct DeleteOptions(
    bool DryRun = false,
    bool SecureDelete = false,
    bool UseRecycleBin = true,
    int MinAgeDays = 0)
{
    public static DeleteOptions Default => new();

    public static DeleteOptions Preview => new(DryRun: true);

    public bool IsDestructive => !DryRun;
}

/// <summary>
/// Progress report emitted by deletion pipelines.
/// Raised on a worker thread — the UI should dispatch.
/// </summary>
public readonly record struct DeleteProgress(
    int ItemsProcessed,
    int ItemsTotal,
    long BytesFreed,
    string CurrentItem,
    bool Skipped)
{
    public double Percent => ItemsTotal <= 0 ? 0 : 100.0 * ItemsProcessed / ItemsTotal;
}

/// <summary>
/// The only terminal states a destructive item can report. Preview is a
/// deliberate non-mutating result; the remaining successful states represent
/// a confirmed disposition. Queued is reserved for a future reboot/worker
/// hand-off and must never be presented as deleted.
/// </summary>
public enum DeletionOutcomeKind
{
    Preview,
    Recycled,
    PermanentlyDeleted,
    SecurelyDeleted,
    Queued,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>One filesystem item and the result of applying DeleteOptions.</summary>
public sealed record DeletionResult(
    string Path,
    DeletionOutcomeKind Outcome,
    long SizeBytes = 0,
    string Operation = "delete",
    string? Reason = null,
    bool Recoverable = false)
{
    public bool IsConfirmed => Outcome is
        DeletionOutcomeKind.Recycled or
        DeletionOutcomeKind.PermanentlyDeleted or
        DeletionOutcomeKind.SecurelyDeleted;

    public bool IsPreview => Outcome == DeletionOutcomeKind.Preview;

    public bool IsFailure => Outcome is
        DeletionOutcomeKind.Failed or
        DeletionOutcomeKind.Cancelled;

    public static DeletionResult Queued(
        string path,
        long sizeBytes = 0,
        string operation = "delete",
        string? reason = null)
        => new(path, DeletionOutcomeKind.Queued, sizeBytes, operation, reason);
}

/// <summary>
/// Aggregate result of a deletion pipeline. Carries <see cref="DryRun"/> so
/// the UI can tell the user "would have freed X" vs "freed X".
/// </summary>
public readonly record struct DeleteSummary
{
    public int ItemsDeleted { get; init; }
    public int ItemsSkipped { get; init; }
    public long BytesFreed { get; init; }
    public bool DryRun { get; init; }
    public IReadOnlyList<string> SkippedReasons { get; init; }
    public IReadOnlyList<DeletionResult> Results { get; init; }

    /// <summary>Count of operations confirmed by the filesystem.</summary>
    public int ItemsConfirmed => (Results ?? Array.Empty<DeletionResult>()).Count(r => r.IsConfirmed);

    /// <summary>Count of non-mutating preview candidates.</summary>
    public int ItemsPreviewed => (Results ?? Array.Empty<DeletionResult>()).Count(r => r.IsPreview);

    public int ItemsFailed => (Results ?? Array.Empty<DeletionResult>()).Count(r => r.Outcome == DeletionOutcomeKind.Failed);

    public int ItemsCancelled => (Results ?? Array.Empty<DeletionResult>()).Count(r => r.Outcome == DeletionOutcomeKind.Cancelled);

    public bool HasFailures => ItemsFailed > 0 || ItemsCancelled > 0;

    public long BytesConfirmed => (Results ?? Array.Empty<DeletionResult>())
        .Where(r => r.IsConfirmed)
        .Sum(r => r.SizeBytes);

    public long BytesPlanned => (Results ?? Array.Empty<DeletionResult>())
        .Where(r => r.IsPreview)
        .Sum(r => r.SizeBytes);

    public DeleteSummary(
        int itemsDeleted,
        int itemsSkipped,
        long bytesFreed,
        bool dryRun,
        IReadOnlyList<string>? skippedReasons = null)
    {
        ItemsDeleted = itemsDeleted;
        ItemsSkipped = itemsSkipped;
        BytesFreed = bytesFreed;
        DryRun = dryRun;
        SkippedReasons = skippedReasons ?? Array.Empty<string>();
        Results = Array.Empty<DeletionResult>();
    }

    internal DeleteSummary(
        int itemsDeleted,
        int itemsSkipped,
        long bytesFreed,
        bool dryRun,
        IReadOnlyList<string>? skippedReasons,
        IReadOnlyList<DeletionResult> results)
    {
        ItemsDeleted = itemsDeleted;
        ItemsSkipped = itemsSkipped;
        BytesFreed = bytesFreed;
        DryRun = dryRun;
        SkippedReasons = skippedReasons ?? Array.Empty<string>();
        Results = results;
    }

    public static DeleteSummary FromResults(
        IEnumerable<DeletionResult> results,
        bool dryRun)
    {
        var list = results.ToList();
        var successful = list.Where(r => r.IsConfirmed).ToList();
        var previews = list.Where(r => r.IsPreview).ToList();
        var skipped = list.Where(r => !r.IsConfirmed && !r.IsPreview).ToList();
        var reasons = skipped
            .Select(r => string.IsNullOrWhiteSpace(r.Reason)
                ? r.Outcome.ToString()
                : r.Reason!)
            .ToList();

        return new DeleteSummary(
            dryRun ? previews.Count : successful.Count,
            skipped.Count,
            dryRun ? previews.Sum(r => r.SizeBytes) : successful.Sum(r => r.SizeBytes),
            dryRun,
            reasons,
            list);
    }

    public static DeleteSummary Empty => new(0, 0, 0, false);
}
