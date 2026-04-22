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
    bool UseRecycleBin = true)
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
/// Aggregate result of a deletion pipeline. Carries <see cref="DryRun"/> so
/// the UI can tell the user "would have freed X" vs "freed X".
/// </summary>
public readonly record struct DeleteSummary(
    int ItemsDeleted,
    int ItemsSkipped,
    long BytesFreed,
    bool DryRun)
{
    public static DeleteSummary Empty => new(0, 0, 0, false);
}
