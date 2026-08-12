using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Cleaning;

/// <summary>
/// Supported OS gates from <c>DetectOS=</c>. winapp2 uses a short string like
/// "10.0|" (≥Win10), "6.0|6.3" (Vista..8.1). We only *parse* the gate — the
/// runner decides whether to honour it based on the current OS version.
/// </summary>
public enum Winapp2OsMatch { Any, Matched, Excluded }

/// <summary>
/// One <c>[EntryName]</c> section from a <c>winapp2.ini</c> file.
///
/// winapp2 distinguishes five *independent* directive families that were
/// previously collapsed into a single "Detect" bucket — which incorrectly
/// fed OS-version strings into the registry-key check and suppressed every
/// applicable rule that had an OS gate.
/// </summary>
public class Winapp2Entry
{
    public string Section       { get; set; } = "";
    public string LangSecRef    { get; set; } = "";
    public string Default       { get; set; } = "false";
    public string DetectOS      { get; set; } = "";           // "10.0|"
    public string SpecialDetect { get; set; } = "";           // DET_CHROME, DET_FIREFOX, ...
    public List<string> Detect    { get; } = new();           // reg keys (presence gate)
    public List<string> DetectFile{ get; } = new();           // file/dir paths (presence gate)
    public List<string> Warning   { get; } = new();
    public List<string> FileKeys  { get; } = new();           // FileKey1=path|spec[|RECURSE|REMOVESELF]
    public List<string> RegKeys   { get; } = new();           // RegKey1=HKCU\...
    public List<string> ExcludeKeys { get; } = new();

    public bool IsApplicable()
    {
        if (!OsMatchesDetectOs(DetectOS)) return false;
        if (!string.IsNullOrEmpty(SpecialDetect) && !EvaluateSpecialDetect(SpecialDetect)) return false;
        foreach (var det in Detect) if (!RegistryKeyExists(det)) return false;
        foreach (var f in DetectFile)
        {
            var expanded = Environment.ExpandEnvironmentVariables(f);
            if (!File.Exists(expanded) && !Directory.Exists(expanded)) return false;
        }
        return true;
    }

    private static bool EvaluateSpecialDetect(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "DET_CHROME"      => AnyKeyExists(@"HKCU\Software\Google\Chrome", @"HKLM\SOFTWARE\Google\Chrome\BLBeacon"),
            "DET_FIREFOX"     => AnyKeyExists(@"HKLM\SOFTWARE\Mozilla\Mozilla Firefox", @"HKCU\Software\Mozilla\Firefox"),
            "DET_OPERA"       => AnyKeyExists(@"HKCU\Software\Opera Software", @"HKLM\SOFTWARE\Opera Software"),
            "DET_THUNDERBIRD" => AnyKeyExists(@"HKLM\SOFTWARE\Mozilla\Mozilla Thunderbird", @"HKCU\Software\Mozilla\Thunderbird"),
            "DET_EDGE"        => AnyKeyExists(@"HKLM\SOFTWARE\Microsoft\Edge", @"HKCU\Software\Microsoft\Edge"),
            "DET_SAFARI"      => RegistryKeyExists(@"HKLM\SOFTWARE\Apple Computer, Inc.\Safari"),
            "DET_SEAMONKEY"   => AnyKeyExists(@"HKLM\SOFTWARE\Mozilla\SeaMonkey", @"HKCU\Software\Mozilla\SeaMonkey"),
            "DET_WATERFOX"    => AnyKeyExists(@"HKLM\SOFTWARE\Waterfox", @"HKCU\Software\Waterfox"),
            "DET_PALE_MOON"   => AnyKeyExists(@"HKLM\SOFTWARE\Mozilla\Pale Moon", @"HKCU\Software\Mozilla\Pale Moon"),
            _ => true, // unknown token: allow — FileKey paths will no-op if the app is absent
        };
    }

    private static bool AnyKeyExists(params string[] paths)
    {
        foreach (var p in paths) if (RegistryKeyExists(p)) return true;
        return false;
    }

    private static bool OsMatchesDetectOs(string gate)
    {
        if (string.IsNullOrWhiteSpace(gate)) return true;
        // Format: min|max  (either can be blank, meaning "open-ended").
        var parts = gate.Split('|');
        var min = parts.Length > 0 ? parts[0].Trim() : "";
        var max = parts.Length > 1 ? parts[1].Trim() : "";
        var os = Environment.OSVersion.Version;
        if (!string.IsNullOrEmpty(min) && Version.TryParse(min, out var vmin) && os < vmin) return false;
        if (!string.IsNullOrEmpty(max) && Version.TryParse(max, out var vmax) && os > vmax) return false;
        return true;
    }

    internal static bool RegistryKeyExists(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var (hive, sub) = SplitHive(path);
            if (hive == null) return false;
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive.Value, Microsoft.Win32.RegistryView.Default);
            using var key = baseKey.OpenSubKey(sub);
            return key != null;
        }
        catch { return false; }
    }

    internal static (Microsoft.Win32.RegistryHive? Hive, string SubKey) SplitHive(string path)
    {
        var split = path.IndexOf('\\');
        if (split <= 0) return (null, "");
        var hiveName = path[..split].ToUpperInvariant();
        var sub = path[(split + 1)..];
        Microsoft.Win32.RegistryHive? hive = hiveName switch
        {
            "HKCU" or "HKEY_CURRENT_USER"  => Microsoft.Win32.RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Microsoft.Win32.RegistryHive.LocalMachine,
            "HKU"  or "HKEY_USERS"         => Microsoft.Win32.RegistryHive.Users,
            "HKCR" or "HKEY_CLASSES_ROOT"  => Microsoft.Win32.RegistryHive.ClassesRoot,
            _ => null,
        };
        return (hive, sub);
    }
}

/// <summary>
/// Parser for the community-maintained <c>winapp2.ini</c> cleaner database.
/// We route every INI key to the correct bucket so downstream applicability
/// checks don't cross-contaminate — a past bug collapsed OS gates and
/// numbered detect rules into the registry Detect list, excluding every
/// entry with a DetectOS.
/// </summary>
public static class Winapp2Parser
{
    public static List<Winapp2Entry> Parse(TextReader reader)
    {
        var list = new List<Winapp2Entry>();
        Winapp2Entry? cur = null;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.TrimEnd();
            if (line.Length == 0) continue;
            if (line[0] == ';' || line[0] == '#') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                if (cur != null) list.Add(cur);
                cur = new Winapp2Entry { Section = line[1..^1] };
                continue;
            }
            if (cur == null) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            // Order matters: longest/most-specific prefixes first so
            // "DetectFile" doesn't get vacuumed into "Detect".
            if      (Eq(key, "LangSecRef"))    cur.LangSecRef    = val;
            else if (Eq(key, "Default"))        cur.Default       = val;
            else if (Eq(key, "DetectOS"))       cur.DetectOS      = val;
            else if (Eq(key, "SpecialDetect"))  cur.SpecialDetect = val;
            else if (Starts(key, "DetectFile")) cur.DetectFile.Add(val);
            else if (Starts(key, "Detect"))     cur.Detect.Add(val);     // Detect, Detect1..N
            else if (Starts(key, "Warning"))    cur.Warning.Add(val);
            else if (Starts(key, "ExcludeKey")) cur.ExcludeKeys.Add(val);
            else if (Starts(key, "FileKey"))    cur.FileKeys.Add(val);
            else if (Starts(key, "RegKey"))     cur.RegKeys.Add(val);
            // Unknown keys are ignored — winapp2 has many niche directives
            // and silent-skip is the correct behaviour for forward compat.
        }
        if (cur != null) list.Add(cur);
        return list;
    }

    public static List<Winapp2Entry> ParseFile(string path)
    {
        using var sr = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(sr);
    }

    private static bool Eq(string a, string b)      => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Starts(string a, string b)  => a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Runner that applies applicable <see cref="Winapp2Entry"/> records, gated
/// through <see cref="SafetyGuard"/> and <see cref="DeleteOptions"/>. Honours
/// <see cref="DeleteOptions.DryRun"/> — reports sizes without deleting.
/// </summary>
public class Winapp2Runner
{
    public async Task<DeleteSummary> RunAsync(
        IEnumerable<Winapp2Entry> entries,
        DeleteOptions options,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        var list = entries.ToList();
        int total = list.Count;
        var results = new List<DeletionResult>();

        await Task.Run(() =>
        {
            foreach (var entry in list)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DeleteProgress(
                    results.Count,
                    total,
                    CurrentBytes(results, options.DryRun),
                    entry.Section,
                    false));

                if (!entry.IsApplicable())
                {
                    results.Add(DeletionExecutor.Skipped(
                        new DeletionRequest(entry.Section, Operation: "winapp2-rule"),
                        "Rule is not applicable."));
                    continue;
                }

                foreach (var fk in entry.FileKeys)
                {
                    try { results.AddRange(RunFileKey(fk, options, ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn($"winapp2 FileKey '{fk}' failed: {ex.Message}");
                        results.Add(DeletionExecutor.FailedExternal(
                            fk,
                            "winapp2-file-key",
                            ex.Message));
                    }
                }

                foreach (var rk in entry.RegKeys)
                {
                    try { results.Add(RunRegKey(rk, options)); }
                    catch (Exception ex)
                    {
                        Log.Warn($"winapp2 RegKey '{rk}' failed: {ex.Message}");
                        results.Add(DeletionExecutor.FailedExternal(
                            rk,
                            "winapp2-regkey",
                            ex.Message));
                    }
                }
            }
        }, ct);

        return DeleteSummary.FromResults(results, options.DryRun);
    }

    // FileKey format: path|filespec[|RECURSE|REMOVESELF]
    private static List<DeletionResult> RunFileKey(
        string raw,
        DeleteOptions opt,
        CancellationToken ct)
    {
        var results = new List<DeletionResult>();
        var parts = raw.Split('|');
        if (parts.Length == 0) return results;

        var dir = Environment.ExpandEnvironmentVariables(parts[0]);
        var spec = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "*.*";
        bool recurse    = parts.Skip(2).Any(f => f.Equals("RECURSE",    StringComparison.OrdinalIgnoreCase));
        bool removeSelf = parts.Skip(2).Any(f => f.Equals("REMOVESELF", StringComparison.OrdinalIgnoreCase));

        if (!Directory.Exists(dir))
        {
            results.Add(DeletionExecutor.Skipped(
                new DeletionRequest(dir, IsDirectory: true, Operation: "winapp2-file-key"),
                "Missing."));
            return results;
        }
        if (!SafetyGuard.IsPathSafeToDelete(dir))
        {
            results.Add(DeletionExecutor.Skipped(
                new DeletionRequest(dir, IsDirectory: true, Operation: "winapp2-file-key"),
                "The path is protected or invalid."));
            return results;
        }

        IEnumerable<string> files;
        try
        {
            files = recurse
                ? SafetyGuard.SafeEnumerateFiles(dir, spec)
                : Directory.EnumerateFiles(dir, spec, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            results.Add(DeletionExecutor.FailedExternal(
                dir,
                "winapp2-file-key",
                "The cleaner target could not be enumerated."));
            return results;
        }

        var executor = new DeletionExecutor();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            long size = 0;
            try { size = new FileInfo(file).Length; } catch { /* executor reports missing metadata */ }
            results.Add(executor.Execute(
                new DeletionRequest(file, ExpectedSizeBytes: size, Operation: "winapp2-file"),
                opt,
                ct));
        }

        if (removeSelf)
        {
            results.Add(executor.Execute(
                new DeletionRequest(dir, IsDirectory: true, Operation: "winapp2-remove-self"),
                opt,
                ct));
        }
        return results;
    }

    private static DeletionResult RunRegKey(string raw, DeleteOptions opt)
    {
        var result = RegistryDeletion.DeleteKeyTree(raw, "winapp2-regkey", opt.DryRun);
        var outcome = result.Status switch
        {
            RegistryDeletionStatus.DryRun => DeletionOutcomeKind.Preview,
            RegistryDeletionStatus.Deleted => DeletionOutcomeKind.PermanentlyDeleted,
            RegistryDeletionStatus.SkippedMissing or
            RegistryDeletionStatus.SkippedMalformedPath or
            RegistryDeletionStatus.SkippedUnsafePath or
            RegistryDeletionStatus.SkippedSymlink or
            RegistryDeletionStatus.SkippedDrift => DeletionOutcomeKind.Skipped,
            _ => DeletionOutcomeKind.Failed,
        };
        if (outcome is DeletionOutcomeKind.Skipped or DeletionOutcomeKind.Failed)
            Log.Warn($"winapp2 RegKey '{raw}' skipped: {result.Status} {result.ErrorMessage}");
        return new DeletionResult(
            result.Path,
            outcome,
            0,
            "winapp2-regkey",
            outcome is DeletionOutcomeKind.Preview or DeletionOutcomeKind.PermanentlyDeleted
                ? null
                : result.ErrorMessage ?? result.Status.ToString(),
            Recoverable: result.Deleted);
    }

    private static long CurrentBytes(
        IReadOnlyList<DeletionResult> results,
        bool dryRun)
        => dryRun
            ? results.Where(r => r.IsPreview).Sum(r => r.SizeBytes)
            : results.Where(r => r.IsConfirmed).Sum(r => r.SizeBytes);
}
