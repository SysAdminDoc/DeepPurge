using System.Diagnostics;
using System.Text;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Packages;

public sealed record WingetEntry(string Id, string Name, string Version, string? Available, string Source);
public sealed record ScoopEntry(string Name, string Version, string Bucket);

/// <summary>
/// Secondary source adapter — borrowed conceptually from BCUninstaller.
/// Enriches the existing <see cref="InstalledProgram"/> list with metadata
/// from modern Windows package managers (winget, scoop) so the user can see
/// "installed via winget, upgrade available: 1.2.3".
///
/// Never adds synthetic entries for duplicates that already live in the
/// registry — matches by normalized DisplayName and updates in place.
/// </summary>
public static class PackageManagerScanner
{
    private const int ProcessTimeoutMs = 20_000;

    public static async Task EnrichAsync(
        IList<InstalledProgram> programs,
        CancellationToken ct = default)
    {
        var wingetTask = Task.Run(() => QueryWinget(ct), ct);
        var scoopTask  = Task.Run(() => QueryScoop(ct), ct);

        var winget = await wingetTask.ConfigureAwait(false);
        var scoop  = await scoopTask.ConfigureAwait(false);

        var lookup = BuildNameLookup(programs);

        foreach (var w in winget)
        {
            var norm = Normalize(w.Name);
            if (norm.Length == 0) continue;

            if (lookup.TryGetValue(norm, out var prog))
            {
                prog.PackageManager = "winget";
                prog.PackageId = w.Id;
                if (!string.IsNullOrEmpty(w.Available) && !w.Available.Equals(w.Version, StringComparison.OrdinalIgnoreCase))
                    prog.UpgradeAvailable = w.Available;
            }
        }

        // Scoop apps typically don't register with Windows Installer, so they'd
        // otherwise be invisible. Add them as synthetic entries only when they
        // don't overlap with an existing program.
        foreach (var s in scoop)
        {
            var norm = Normalize(s.Name);
            if (norm.Length == 0 || lookup.ContainsKey(norm)) continue;

            var synthetic = new InstalledProgram
            {
                DisplayName = s.Name,
                DisplayVersion = s.Version,
                Publisher = $"scoop / {s.Bucket}",
                PackageManager = "scoop",
                PackageId = s.Name,
                Source = RegistrySource.HKCU_Uninstall, // best fit; scoop is always user-scope
            };
            programs.Add(synthetic);
            lookup[norm] = synthetic;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  winget
    // ═══════════════════════════════════════════════════════

    public static List<WingetEntry> QueryWinget(CancellationToken ct = default)
    {
        var result = new List<WingetEntry>();
        string output;
        try
        {
            output = RunProcess(
                "winget.exe",
                "list --disable-interactivity --accept-source-agreements",
                ct);
        }
        catch
        {
            return result;
        }

        if (string.IsNullOrWhiteSpace(output)) return result;
        return ParseWingetTable(output);
    }

    /// <summary>
    /// Parses winget's tabular <c>list</c> output by column width. Header row
    /// defines column starts; we slice each data row at those positions.
    ///
    /// Winget is deliberately "human-readable first"; there is no JSON
    /// output for <c>list</c> as of Jan 2026, so we have to cope with the
    /// fixed-width table. Anchor columns: Name, Id, Version, Available, Source.
    /// </summary>
    internal static List<WingetEntry> ParseWingetTable(string output)
    {
        var entries = new List<WingetEntry>();
        var lines = output.Split('\n', StringSplitOptions.None);

        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("Name", StringComparison.Ordinal) &&
                trimmed.Contains("Id", StringComparison.Ordinal) &&
                trimmed.Contains("Version", StringComparison.Ordinal))
            {
                headerIndex = i;
                break;
            }
        }
        if (headerIndex < 0 || headerIndex + 2 >= lines.Length) return entries;

        var header = lines[headerIndex];
        int idxId = header.IndexOf(" Id", StringComparison.Ordinal);
        int idxVersion = header.IndexOf(" Version", StringComparison.Ordinal);
        int idxAvailable = header.IndexOf(" Available", StringComparison.Ordinal);
        int idxSource = header.IndexOf(" Source", StringComparison.Ordinal);
        if (idxId < 0 || idxVersion < 0) return entries;

        // +1 so we skip the leading space we matched on.
        idxId++;
        idxVersion++;
        if (idxAvailable >= 0) idxAvailable++;
        if (idxSource >= 0) idxSource++;

        // Separator line is next — skip it plus any leading blanks.
        for (int i = headerIndex + 2; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (raw.Length < idxVersion) continue;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.StartsWith("-", StringComparison.Ordinal)) continue;

            string name = Slice(raw, 0, idxId).Trim();
            string id = Slice(raw, idxId, idxVersion).Trim();
            string version = idxAvailable >= 0
                ? Slice(raw, idxVersion, idxAvailable).Trim()
                : Slice(raw, idxVersion, raw.Length).Trim();
            string available = idxAvailable >= 0 && idxSource > idxAvailable
                ? Slice(raw, idxAvailable, idxSource).Trim()
                : "";
            string source = idxSource >= 0
                ? Slice(raw, idxSource, raw.Length).Trim()
                : "";

            if (name.Length == 0 || id.Length == 0) continue;
            if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
                continue; // Skip winget's footer messaging.

            entries.Add(new WingetEntry(id, name, version, available, source));
        }
        return entries;
    }

    // ═══════════════════════════════════════════════════════
    //  scoop
    // ═══════════════════════════════════════════════════════

    public static List<ScoopEntry> QueryScoop(CancellationToken ct = default)
    {
        var result = new List<ScoopEntry>();

        // Scoop apps live in a well-known folder regardless of whether the
        // CLI is on PATH. Prefer the filesystem — it's faster, doesn't spawn
        // a process, and works even when the scoop shim is broken.
        var scoopRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "scoop", "apps");

        if (!Directory.Exists(scoopRoot)) return result;

        try
        {
            foreach (var appDir in Directory.EnumerateDirectories(scoopRoot))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(appDir);
                if (string.IsNullOrEmpty(name) || name.Equals("scoop", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Active version lives under <appDir>\current, which is a junction
                // to the actual version folder; read current\install.json for accuracy.
                var installJson = Path.Combine(appDir, "current", "install.json");
                var manifestJson = Path.Combine(appDir, "current", "manifest.json");
                string version = "";
                string bucket = "";

                if (File.Exists(installJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(installJson));
                        if (doc.RootElement.TryGetProperty("bucket", out var b))
                            bucket = b.GetString() ?? "";
                    }
                    catch { /* keep defaults */ }
                }

                if (File.Exists(manifestJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestJson));
                        if (doc.RootElement.TryGetProperty("version", out var v))
                            version = v.GetString() ?? "";
                    }
                    catch { /* keep defaults */ }
                }

                result.Add(new ScoopEntry(name, version, string.IsNullOrEmpty(bucket) ? "main" : bucket));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* permission error or weird FS state — return what we have */ }

        return result;
    }

    // ═══════════════════════════════════════════════════════
    //  helpers
    // ═══════════════════════════════════════════════════════

    private static Dictionary<string, InstalledProgram> BuildNameLookup(IList<InstalledProgram> programs)
    {
        var dict = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in programs)
        {
            var key = Normalize(p.DisplayName);
            if (key.Length > 0) dict[key] = p;
        }
        return dict;
    }

    private static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string Slice(string s, int start, int end)
    {
        if (start < 0) start = 0;
        if (end > s.Length) end = s.Length;
        if (end <= start) return "";
        return s[start..end];
    }

    private static string RunProcess(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi);
        if (proc == null) return "";

        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(ProcessTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already dead */ }
            return sb.ToString();
        }
        proc.WaitForExit(); // flush async handlers
        ct.ThrowIfCancellationRequested();
        return sb.ToString();
    }
}
