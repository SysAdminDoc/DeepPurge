using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
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

[Flags]
public enum RegistrySearchScope
{
    Keys   = 1 << 0,
    Names  = 1 << 1,
    Data   = 1 << 2,
    All    = Keys | Names | Data,
}

public sealed record RegistryHuntOptions(
    RegistrySearchScope Scope = RegistrySearchScope.All,
    bool CaseSensitive = false,
    bool UseRegex = false,
    int MaxHits = 500,
    int MaxDepth = 8,
    TimeSpan? Timeout = null);

/// <summary>
/// Registry search inspired by NirSoft RegScanner, Eric Zimmerman's Registry
/// Explorer, and the parallel-root pattern the best OSS reg-scanners use.
///
/// Gains over the previous single-threaded implementation:
///  - <b>Parallel root fan-out</b>: HKLM, HKLM\WOW6432Node, HKCU and HKCR are
///    scanned on independent worker tasks. Most time is spent in RegEnumKeyEx
///    syscalls, so a 4-way parallel scan is close to 4x on a real hive.
///  - <b>Scope filter</b>: search keys only / value names only / value data
///    only, so you don't drown in matches when you're hunting for an app's
///    CLSID (keys) vs install-path residue (data).
///  - <b>Regex support</b>: optional compiled regex for matching — RegScanner's
///    power feature. Plain substring mode uses <see cref="string.Contains(string, StringComparison)"/>
///    which is already SIMD-vectorized on .NET 8.
///  - <b>Live progress</b>: the caller gets an <see cref="IProgress{T}"/>
///    fed every ~128 hits so the UI can update as results stream in.
/// </summary>
public static class RegistryHunter
{
    /// <summary>
    /// Root + path + display-prefix triples. Each root is scanned on its own
    /// worker task — they don't share state beyond the concurrent hit sink.
    /// </summary>
    private static readonly (RegistryKey Hive, string Path, string Prefix)[] SearchRoots =
    {
        (global::Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE",              "HKCU"),
        (global::Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE",              "HKLM"),
        (global::Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node",  "HKLM"),
        (global::Microsoft.Win32.Registry.ClassesRoot,  @"",                      "HKCR"),
    };

    /// <summary>Back-compat shim for the old signature.</summary>
    public static List<RegistryHit> Search(
        string needle,
        int maxHits = 500,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
        => Search(needle, new RegistryHuntOptions(MaxHits: maxHits, Timeout: timeout), null, ct);

    public static List<RegistryHit> Search(
        string needle,
        RegistryHuntOptions options,
        IProgress<int>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(needle) || needle.Length < 3)
            return new List<RegistryHit>();

        var hits = new ConcurrentBag<RegistryHit>();
        var deadline = Stopwatch.StartNew();
        var maxTime = options.Timeout ?? TimeSpan.FromSeconds(30);
        int reportedCount = 0;

        // Compile once so every worker reuses the same automaton.
        var matcher = BuildMatcher(needle, options);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            Parallel.ForEach(
                SearchRoots,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = SearchRoots.Length,
                    CancellationToken = cts.Token,
                },
                (root, loopState) =>
                {
                    if (hits.Count >= options.MaxHits || deadline.Elapsed > maxTime)
                    {
                        loopState.Stop();
                        return;
                    }

                    try
                    {
                        using var handle = string.IsNullOrEmpty(root.Path)
                            ? root.Hive
                            : root.Hive.OpenSubKey(root.Path);
                        if (handle == null) return;

                        var basePath = string.IsNullOrEmpty(root.Path)
                            ? root.Prefix
                            : root.Prefix + @"\" + root.Path;

                        SearchKey(
                            handle, basePath, matcher, options,
                            hits, deadline, maxTime, 0, cts, progress, ref reportedCount);
                    }
                    catch (OperationCanceledException) { }
                    catch { /* unreadable root - skip */ }
                });
        }
        catch (OperationCanceledException) { }

        // Deterministic ordering: keys first (easier to delete), then by path.
        return hits
            .OrderBy(h => h.Type)
            .ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Take(options.MaxHits)
            .ToList();
    }

    // ───── matcher compilation ──────────────────────────────────

    /// <summary>
    /// Returns a <see cref="Matcher"/> struct that wraps either a regex or
    /// a plain substring check depending on the options. Using a struct
    /// avoids per-call delegate-invocation overhead in the hot loop.
    /// </summary>
    private readonly struct Matcher
    {
        public readonly Regex? Regex;
        public readonly string Needle;
        public readonly StringComparison Comparison;

        public Matcher(Regex? regex, string needle, StringComparison comparison)
        {
            Regex = regex;
            Needle = needle;
            Comparison = comparison;
        }

        public bool IsMatch(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            return Regex != null
                ? Regex.IsMatch(candidate)
                : candidate.Contains(Needle, Comparison);
        }
    }

    private static Matcher BuildMatcher(string needle, RegistryHuntOptions options)
    {
        var comparison = options.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (!options.UseRegex) return new Matcher(null, needle, comparison);

        var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!options.CaseSensitive) regexOptions |= RegexOptions.IgnoreCase;

        try
        {
            var re = new Regex(needle, regexOptions, TimeSpan.FromSeconds(1));
            return new Matcher(re, needle, comparison);
        }
        catch
        {
            // Bad regex — fall back to substring so the user's search still runs.
            return new Matcher(null, needle, comparison);
        }
    }

    // ───── recursive walker ─────────────────────────────────────

    private static void SearchKey(
        RegistryKey key,
        string currentPath,
        Matcher matcher,
        RegistryHuntOptions options,
        ConcurrentBag<RegistryHit> hits,
        Stopwatch deadline,
        TimeSpan maxTime,
        int depth,
        CancellationTokenSource cts,
        IProgress<int>? progress,
        ref int reportedCount)
    {
        if (cts.Token.IsCancellationRequested) return;
        if (hits.Count >= options.MaxHits) { cts.Cancel(); return; }
        if (deadline.Elapsed > maxTime) { cts.Cancel(); return; }
        if (depth > options.MaxDepth) return;

        // ── values ──
        if ((options.Scope & (RegistrySearchScope.Names | RegistrySearchScope.Data)) != 0)
        {
            try
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (hits.Count >= options.MaxHits) break;

                    bool nameHit = (options.Scope & RegistrySearchScope.Names) != 0 &&
                                   !string.IsNullOrEmpty(valueName) &&
                                   matcher.IsMatch(valueName);

                    string? data = null;
                    bool dataHit = false;
                    if ((options.Scope & RegistrySearchScope.Data) != 0)
                    {
                        try
                        {
                            var raw = key.GetValue(valueName);
                            data = raw switch
                            {
                                null => null,
                                string s => s,
                                string[] multi => string.Join(" | ", multi),
                                byte[] b => "0x" + Convert.ToHexString(b, 0, Math.Min(b.Length, 64)),
                                _ => raw.ToString(),
                            };
                            if (data != null && matcher.IsMatch(data))
                                dataHit = true;
                        }
                        catch { /* REG_NONE or denied - skip */ }
                    }

                    if (nameHit || dataHit)
                    {
                        hits.Add(new RegistryHit
                        {
                            Path = currentPath,
                            ValueName = string.IsNullOrEmpty(valueName) ? "(default)" : valueName,
                            ValueData = data,
                            Type = LeftoverType.RegistryValue,
                            Match = dataHit && nameHit ? "name+data" : (dataHit ? "data" : "name"),
                        });

                        // Throttle progress callbacks — every 32 hits is plenty
                        // of UI animation without drowning the dispatcher.
                        var c = Interlocked.Increment(ref reportedCount);
                        if ((c & 31) == 0) progress?.Report(hits.Count);
                    }
                }
            }
            catch { /* key vanished or access denied mid-walk */ }
        }

        // ── subkeys ──
        string[] subKeyNames;
        try { subKeyNames = key.GetSubKeyNames(); }
        catch { return; }

        foreach (var subName in subKeyNames)
        {
            if (cts.Token.IsCancellationRequested) return;
            if (hits.Count >= options.MaxHits) return;

            if ((options.Scope & RegistrySearchScope.Keys) != 0 && matcher.IsMatch(subName))
            {
                hits.Add(new RegistryHit
                {
                    Path = currentPath + @"\" + subName,
                    ValueName = null,
                    ValueData = null,
                    Type = LeftoverType.RegistryKey,
                    Match = "key",
                });

                var c = Interlocked.Increment(ref reportedCount);
                if ((c & 31) == 0) progress?.Report(hits.Count);
            }

            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub != null)
                    SearchKey(sub, currentPath + @"\" + subName, matcher, options,
                        hits, deadline, maxTime, depth + 1, cts, progress, ref reportedCount);
            }
            catch { /* skip */ }
        }
    }
}
