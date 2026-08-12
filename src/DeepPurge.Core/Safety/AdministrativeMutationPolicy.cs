using System.Runtime.InteropServices;
using System.Text.Json;
using DeepPurge.Core.App;

namespace DeepPurge.Core.Safety;

public enum AdministrativeMutationOutcome
{
    Preview,
    Changed,
    Restored,
    Skipped,
    Failed,
    Unsupported,
    Cancelled,
}

/// <summary>
/// Exact result for a non-file administrative mutation. Before/after state
/// and rollback data are deliberately part of the result rather than hidden
/// in a UI-specific boolean path.
/// </summary>
public sealed record AdministrativeMutationResult(
    string OperationId,
    string Operation,
    string Target,
    AdministrativeMutationOutcome Outcome,
    string BeforeState,
    string AfterState,
    string RollbackData,
    string? Reason = null,
    int ItemsAffected = 0)
{
    public DateTimeOffset RecordedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool Succeeded => Outcome is
        AdministrativeMutationOutcome.Changed or
        AdministrativeMutationOutcome.Restored;

    public bool IsReviewOnly => Outcome is
        AdministrativeMutationOutcome.Skipped or
        AdministrativeMutationOutcome.Unsupported;
}

/// <summary>
/// Creates and records administrative mutation results. The mutation itself
/// remains in the owning subsystem so it can capture the exact native state;
/// this policy is the common result/ledger boundary.
/// </summary>
public static class AdministrativeMutationPolicy
{
    public static AdministrativeMutationResult Preview(
        string operation,
        string target,
        string beforeState,
        string afterState,
        string rollbackData,
        int itemsAffected = 0)
        => Finish(
            operation,
            target,
            AdministrativeMutationOutcome.Preview,
            beforeState,
            afterState,
            rollbackData,
            reason: "Preview only; no administrative state was changed.",
            itemsAffected);

    public static AdministrativeMutationResult Changed(
        string operation,
        string target,
        string beforeState,
        string afterState,
        string rollbackData,
        int itemsAffected = 1)
        => Finish(
            operation,
            target,
            AdministrativeMutationOutcome.Changed,
            beforeState,
            afterState,
            rollbackData,
            reason: null,
            itemsAffected);

    public static AdministrativeMutationResult Skipped(
        string operation,
        string target,
        string beforeState,
        string reason)
        => Finish(
            operation,
            target,
            AdministrativeMutationOutcome.Skipped,
            beforeState,
            beforeState,
            string.Empty,
            reason,
            itemsAffected: 0);

    public static AdministrativeMutationResult Unsupported(
        string operation,
        string target,
        string reason)
        => Finish(
            operation,
            target,
            AdministrativeMutationOutcome.Unsupported,
            "Unavailable",
            "Unavailable",
            string.Empty,
            reason,
            itemsAffected: 0);

    public static AdministrativeMutationResult Failed(
        string operation,
        string target,
        string beforeState,
        string reason,
        string rollbackData = "")
        => Finish(
            operation,
            target,
            AdministrativeMutationOutcome.Failed,
            beforeState,
            "Unknown",
            rollbackData,
            reason,
            itemsAffected: 0);

    public static AdministrativeMutationResult Cancelled(
        string operation,
        string target,
        string beforeState)
        => Finish(
            operation,
            target,
            AdministrativeMutationOutcome.Cancelled,
            beforeState,
            beforeState,
            string.Empty,
            "Cancellation requested.",
            itemsAffected: 0);

    private static AdministrativeMutationResult Finish(
        string operation,
        string target,
        AdministrativeMutationOutcome outcome,
        string beforeState,
        string afterState,
        string rollbackData,
        string? reason,
        int itemsAffected)
    {
        var result = new AdministrativeMutationResult(
            Guid.NewGuid().ToString("N"),
            operation,
            target,
            outcome,
            beforeState,
            afterState,
            rollbackData,
            reason,
            itemsAffected);
        AdministrativeMutationLedger.Record(result);
        return result;
    }
}

public static class AdministrativeMutationLedger
{
    private static readonly object Sync = new();
    private static readonly AsyncLocal<string?> PathOverride = new();

    private static string CurrentPath => PathOverride.Value ?? Path.Combine(
        DataPaths.Logs,
        $"administrative-mutations-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    internal static IDisposable UsePathForTests(string path)
    {
        var previous = PathOverride.Value;
        PathOverride.Value = Path.GetFullPath(path);
        return new Scope(previous);
    }

    public static void Record(AdministrativeMutationResult result)
    {
        try
        {
            var json = JsonSerializer.Serialize(result);
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CurrentPath)!);
                File.AppendAllText(
                    CurrentPath,
                    json + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // A failed local audit write must not turn a verified mutation into
            // an unknown native state. The result still reports the operation.
        }
    }

    public static List<AdministrativeMutationResult> LoadRecent(int max = 100)
    {
        try
        {
            if (!File.Exists(CurrentPath)) return new();
            return File.ReadAllLines(CurrentPath)
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<AdministrativeMutationResult>(line); }
                    catch { return null; }
                })
                .Where(result => result != null)
                .Cast<AdministrativeMutationResult>()
                .OrderByDescending(result => result.RecordedAtUtc)
                .Take(max)
                .ToList();
        }
        catch { return new(); }
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            PathOverride.Value = previous;
            _disposed = true;
        }
    }
}

/// <summary>Best-effort broadcasts required after environment/shell changes.</summary>
public static class SystemRefreshNotifier
{
    private static readonly IntPtr Broadcast = new(0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    public static void NotifyEnvironmentChanged()
    {
        try
        {
            SendMessageTimeout(
                Broadcast,
                WmSettingChange,
                IntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                5000,
                out _);
        }
        catch { /* refresh is best effort after the registry write */ }
    }

    public static void NotifyShellChanged()
    {
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch { /* shell refresh is best effort */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
