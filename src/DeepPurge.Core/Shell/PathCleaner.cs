using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Safety;
// Using fully-qualified Microsoft.Win32.Registry to avoid collision with DeepPurge.Core.Registry namespace.

namespace DeepPurge.Core.Shell;

public class PathEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Directory { get; set; } = "";
    public string Source { get; set; } = ""; // "System" or "User"
    public bool IsOrphaned { get; set; }
    public bool IsProtected => !SafetyGuard.IsPathEntrySafeToRemove(Directory);
    public bool MutationSupported => IsOrphaned && !IsProtected;
    public string Status => IsProtected ? "Protected" : IsOrphaned ? "Orphaned" : "Valid";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Scans the system and user PATH environment variables for entries that
/// point to non-existent directories. These accumulate after programs are
/// uninstalled and can slow process startup (the loader probes every PATH
/// entry when resolving DLLs).
/// </summary>
public static class PathCleaner
{
    private static readonly string SystemRoot =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Enumerate all PATH entries from both system and user scopes,
    /// flagging any that point to non-existent directories.
    /// </summary>
    public static List<PathEntry> ScanPathEntries(bool orphanedOnly = false)
        => ScanPathEntriesDetailed(orphanedOnly).Items.ToList();

    public static ScanResult<PathEntry> ScanPathEntriesDetailed(
        bool orphanedOnly = false,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var entries = new List<PathEntry>();
        var failures = new List<ScanIssue>();
        var warnings = new List<string>();

        // System PATH
        ScanScope(entries, orphanedOnly, "System",
            global::Microsoft.Win32.Registry.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
            "Path", failures, warnings, ct);

        // User PATH
        ScanScope(entries, orphanedOnly, "User",
            global::Microsoft.Win32.Registry.CurrentUser,
            @"Environment",
            "Path", failures, warnings, ct);

        var result = ScanResult<PathEntry>.Create(
            "path-entries",
            entries,
            failures,
            warnings,
            stopwatch.Elapsed,
            isCancelled: ct.IsCancellationRequested);
        ScanDiagnosticsLedger.Record("path-entries", result);
        return result;
    }

    /// <summary>
    /// Remove orphaned entries from the PATH environment variable.
    /// Only removes entries that are both selected and orphaned.
    /// Returns the number of entries removed.
    /// </summary>
    public static int RemoveOrphanedEntries(IEnumerable<PathEntry> entries)
    {
        return RemoveOrphanedEntriesDetailed(entries)
            .Where(result => result.Succeeded)
            .Sum(result => result.ItemsAffected);
    }

    public static IReadOnlyList<AdministrativeMutationResult> RemoveOrphanedEntriesDetailed(
        IEnumerable<PathEntry> entries,
        bool dryRun = false)
    {
        var selected = entries.Where(entry => entry.IsSelected && entry.IsOrphaned).ToList();
        var results = new List<AdministrativeMutationResult>();

        foreach (var protectedEntry in selected.Where(entry => entry.IsProtected))
        {
            results.Add(AdministrativeMutationPolicy.Skipped(
                "path-entry-remove",
                protectedEntry.Directory,
                "Present",
                "Protected system PATH entry."));
        }

        var systemRemoves = selected
            .Where(entry => entry.Source == "System" && !entry.IsProtected)
            .Select(entry => entry.Directory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userRemoves = selected
            .Where(entry => entry.Source == "User" && !entry.IsProtected)
            .Select(entry => entry.Directory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (systemRemoves.Count > 0)
            results.Add(RemoveFromScopeDetailed(
                global::Microsoft.Win32.Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                "Path",
                "HKLM",
                systemRemoves,
                dryRun));

        if (userRemoves.Count > 0)
            results.Add(RemoveFromScopeDetailed(
                global::Microsoft.Win32.Registry.CurrentUser,
                @"Environment",
                "Path",
                "HKCU",
                userRemoves,
                dryRun));

        return results;
    }

    // ===============================================================
    //  Internals
    // ===============================================================

    private static void ScanScope(List<PathEntry> entries, bool orphanedOnly,
        string source, global::Microsoft.Win32.RegistryKey root, string subKeyPath, string valueName,
        List<ScanIssue> failures, List<string> warnings, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var key = root.OpenSubKey(subKeyPath);
            if (key == null)
            {
                warnings.Add($"{source} PATH registry scope is unavailable.");
                return;
            }

            var raw = key.GetValue(valueName, "", global::Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                         ?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return;

            var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                var dir = part.Trim();
                if (string.IsNullOrEmpty(dir)) continue;

                var expanded = Environment.ExpandEnvironmentVariables(dir);
                var isProtected = !SafetyGuard.IsPathEntrySafeToRemove(dir);
                var isOrphaned = !isProtected && IsOrphanedPathEntry(expanded);

                if (orphanedOnly && !isOrphaned) continue;

                entries.Add(new PathEntry
                {
                    Directory = dir,
                    Source = source,
                    IsOrphaned = isOrphaned,
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            warnings.Add($"{source} PATH scan was cancelled.");
        }
        catch (Exception ex)
        {
            failures.Add(new ScanIssue($"path-{source.ToLowerInvariant()}", ex.Message, ex.GetType().Name));
        }
    }

    private static bool IsOrphanedPathEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Never flag system directories as orphaned.
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith(SystemRoot.ToLowerInvariant()))
            return false;

        // Only flag fully-qualified paths.
        if (!Path.IsPathRooted(path)) return false;

        return !System.IO.Directory.Exists(path);
    }

    private static AdministrativeMutationResult RemoveFromScopeDetailed(
        global::Microsoft.Win32.RegistryKey root,
        string subKeyPath,
        string valueName,
        string hiveName,
        HashSet<string> toRemove,
        bool dryRun)
    {
        var target = $"{hiveName}\\{subKeyPath}\\{valueName}";
        try
        {
            using var key = root.OpenSubKey(subKeyPath, writable: true);
            if (key == null)
                return AdministrativeMutationPolicy.Failed(
                    "path-entry-remove",
                    target,
                    "Unavailable",
                    "The PATH registry scope could not be opened.");

            // Preserve the original value kind (REG_EXPAND_SZ vs REG_SZ).
            var kind = key.GetValueKind(valueName);
            var raw = key.GetValue(valueName, "", global::Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                         ?.ToString() ?? "";

            var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var removed = 0;
            var kept = new List<string>();

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (toRemove.Contains(trimmed))
                    removed++;
                else
                    kept.Add(trimmed);
            }

            var newPath = string.Join(';', kept);
            var rollback = JsonSerializer.Serialize(new
            {
                Hive = hiveName,
                SubKey = subKeyPath,
                Value = valueName,
                Kind = kind.ToString(),
                Raw = raw,
            });
            var before = JsonSerializer.Serialize(new { Kind = kind.ToString(), Raw = raw });

            if (removed == 0)
                return AdministrativeMutationPolicy.Skipped(
                    "path-entry-remove",
                    target,
                    before,
                    "The selected PATH entries were no longer present.");

            if (dryRun)
                return AdministrativeMutationPolicy.Preview(
                    "path-entry-remove",
                    target,
                    before,
                    JsonSerializer.Serialize(new { Kind = kind.ToString(), Raw = newPath }),
                    rollback,
                    removed);

            key.SetValue(valueName, newPath, kind);
            var afterRaw = key.GetValue(
                valueName,
                "",
                global::Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                ?.ToString() ?? "";
            if (!string.Equals(afterRaw, newPath, StringComparison.Ordinal))
                return AdministrativeMutationPolicy.Failed(
                    "path-entry-remove",
                    target,
                    before,
                    "The PATH value did not match the requested post-write state.",
                    rollback);

            SystemRefreshNotifier.NotifyEnvironmentChanged();
            return AdministrativeMutationPolicy.Changed(
                "path-entry-remove",
                target,
                before,
                JsonSerializer.Serialize(new { Kind = kind.ToString(), Raw = afterRaw }),
                rollback,
                removed);
        }
        catch (Exception ex)
        {
            return AdministrativeMutationPolicy.Failed(
                "path-entry-remove",
                target,
                "Unknown",
                ex.Message);
        }
    }
}
