using System.Diagnostics;
using global::Microsoft.Win32;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Registry;

public sealed class RegistryHit
{
    public string Path { get; init; } = "";
    public string? ValueName { get; init; }
    public string? ValueData { get; init; }
    public LeftoverType Type { get; init; }
    public string Match { get; init; } = "";
}

/// <summary>
/// Deep registry search, lifted from Revo's trace scanner concept: give us a
/// string, we walk the well-known roots and return every key, value name, or
/// value data that contains it.
///
/// Hard limits are enforced so a broad search ("a") doesn't melt the UI:
/// per-call timeout, max hit count, and depth cap.
/// </summary>
public static class RegistryHunter
{
    private const int DefaultMaxHits = 500;
    private const int DefaultMaxDepth = 8;

    /// <summary>
    /// Roots worth searching for uninstall-leftover / trace hunting.
    /// Intentionally excludes HKLM\SAM, HKLM\SECURITY (not readable anyway)
    /// and HKLM\SYSTEM\CurrentControlSet (too vast; rarely what the user wants).
    /// </summary>
    private static readonly (RegistryKey Hive, string Path, string Prefix)[] SearchRoots =
    {
        (global::Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE",                              "HKCU"),
        (global::Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE",                              "HKLM"),
        (global::Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node",                  "HKLM"),
        (global::Microsoft.Win32.Registry.ClassesRoot,  @"",                                       "HKCR"),
    };

    public static List<RegistryHit> Search(
        string needle,
        int maxHits = DefaultMaxHits,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var hits = new List<RegistryHit>();
        if (string.IsNullOrWhiteSpace(needle) || needle.Length < 3) return hits;

        var deadline = Stopwatch.StartNew();
        var maxTime = timeout ?? TimeSpan.FromSeconds(30);

        foreach (var (hive, path, prefix) in SearchRoots)
        {
            if (hits.Count >= maxHits || deadline.Elapsed > maxTime) break;
            try
            {
                using var root = string.IsNullOrEmpty(path) ? hive : hive.OpenSubKey(path);
                if (root == null) continue;

                SearchKey(root, prefix + (string.IsNullOrEmpty(path) ? "" : @"\" + path),
                    needle, hits, maxHits, deadline, maxTime, 0, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* unreadable root - skip */ }
        }

        return hits;
    }

    private static void SearchKey(
        RegistryKey key,
        string currentPath,
        string needle,
        List<RegistryHit> hits,
        int maxHits,
        Stopwatch deadline,
        TimeSpan maxTime,
        int depth,
        CancellationToken ct)
    {
        if (hits.Count >= maxHits || deadline.Elapsed > maxTime || depth > DefaultMaxDepth) return;
        ct.ThrowIfCancellationRequested();

        // Values
        try
        {
            foreach (var valueName in key.GetValueNames())
            {
                if (hits.Count >= maxHits) return;

                var nameHit = !string.IsNullOrEmpty(valueName) &&
                              valueName.Contains(needle, StringComparison.OrdinalIgnoreCase);

                string? data = null;
                bool dataHit = false;
                try
                {
                    var raw = key.GetValue(valueName);
                    data = raw?.ToString();
                    if (data != null && data.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        dataHit = true;
                }
                catch { /* REG_BINARY etc. - skip */ }

                if (nameHit || dataHit)
                {
                    hits.Add(new RegistryHit
                    {
                        Path = currentPath,
                        ValueName = string.IsNullOrEmpty(valueName) ? "(default)" : valueName,
                        ValueData = data,
                        Type = LeftoverType.RegistryValue,
                        Match = dataHit ? "data" : "name",
                    });
                }
            }
        }
        catch { /* key might disappear mid-walk */ }

        // Subkeys
        string[] subKeyNames;
        try { subKeyNames = key.GetSubKeyNames(); }
        catch { return; }

        foreach (var subName in subKeyNames)
        {
            if (hits.Count >= maxHits || deadline.Elapsed > maxTime) return;

            if (subName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new RegistryHit
                {
                    Path = currentPath + @"\" + subName,
                    ValueName = null,
                    ValueData = null,
                    Type = LeftoverType.RegistryKey,
                    Match = "key",
                });
            }

            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub != null)
                    SearchKey(sub, currentPath + @"\" + subName, needle,
                        hits, maxHits, deadline, maxTime, depth + 1, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* permission denied on a child — skip */ }
        }
    }
}
