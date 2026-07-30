using System.Text;
using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Execution;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Packages;

public sealed record WingetEntry(string Id, string Name, string Version, string? Available, string Source);
public sealed record ScoopEntry(string Name, string Version, string Bucket);
public sealed record ChocolateyEntry(string Name, string Version);
public sealed record PackageSourceHealth(
    string Source,
    SelfTestStatus Status,
    string Detail,
    string Version,
    string Root,
    int PackageCount,
    string LastScannerStatus,
    string? Hint = null);

/// <summary>
/// Secondary source adapter — borrowed conceptually from BCUninstaller.
/// Enriches the existing <see cref="InstalledProgram"/> list with metadata
/// from modern Windows package managers (winget, scoop, Chocolatey) so the user can see
/// "installed via winget, upgrade available: 1.2.3".
///
/// Never adds synthetic entries for duplicates that already live in the
/// registry — matches by normalized DisplayName and updates in place.
/// </summary>
public static class PackageManagerScanner
{
    private const int ProcessTimeoutMs = 20_000;
    private static IReadOnlyList<PackageSourceHealth> _lastSourceHealth = Array.Empty<PackageSourceHealth>();

    public static IReadOnlyList<PackageSourceHealth> LastSourceHealth => _lastSourceHealth;

    public static async Task EnrichAsync(
        IList<InstalledProgram> programs,
        CancellationToken ct = default)
    {
        var wingetTask = Task.Run(() => QueryWinget(ct), ct);
        var scoopTask  = Task.Run(() => QueryScoop(ct), ct);
        var chocoTask  = Task.Run(() => QueryChocolatey(ct), ct);
        var portableTask = Task.Run(() =>
        {
            var known = new HashSet<string>(
                programs.Select(p => p.DisplayName),
                StringComparer.OrdinalIgnoreCase);
            return PortableAppScanner.Scan(known);
        }, ct);
        var gamesTask = Task.Run(() => GamePlatformScanner.ScanAll(), ct);

        var winget = await wingetTask.ConfigureAwait(false);
        var scoop  = await scoopTask.ConfigureAwait(false);
        var choco  = await chocoTask.ConfigureAwait(false);
        var portables = await portableTask.ConfigureAwait(false);
        var games = await gamesTask.ConfigureAwait(false);

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
                Source = RegistrySource.HKCU_Uninstall,
            };
            programs.Add(synthetic);
            lookup[norm] = synthetic;
        }

        foreach (var c in choco)
        {
            var norm = Normalize(c.Name);
            if (norm.Length == 0) continue;

            if (lookup.TryGetValue(norm, out var prog))
            {
                prog.PackageManager = "chocolatey";
                prog.PackageId = c.Name;
                if (string.IsNullOrEmpty(prog.DisplayVersion))
                    prog.DisplayVersion = c.Version;
                continue;
            }

            var synthetic = new InstalledProgram
            {
                DisplayName = c.Name,
                DisplayVersion = c.Version,
                Publisher = "Chocolatey",
                PackageManager = "chocolatey",
                PackageId = c.Name,
                Source = RegistrySource.HKCU_Uninstall,
            };
            programs.Add(synthetic);
            lookup[norm] = synthetic;
        }

        PortableAppScanner.InjectIntoPrograms(programs, portables);
        GamePlatformScanner.InjectIntoPrograms(programs, games);
    }

    public static IReadOnlyList<PackageSourceHealth> GetSourceHealth(CancellationToken ct = default)
    {
        var results = new List<PackageSourceHealth>
        {
            CheckWingetHealth(ct),
            CheckScoopHealth(ct),
            CheckChocolateyHealth(ct),
        };
        _lastSourceHealth = results;
        return results;
    }

    // ═══════════════════════════════════════════════════════
    //  winget
    // ═══════════════════════════════════════════════════════

    public static List<WingetEntry> QueryWinget(CancellationToken ct = default)
    {
        try
        {
            var jsonOutput = RunPackageManager("winget",
                new[] { "list", "--disable-interactivity", "--accept-source-agreements", "--output", "json" }, ct);
            if (!string.IsNullOrWhiteSpace(jsonOutput) && jsonOutput.TrimStart().StartsWith('['))
                return ParseWingetJson(jsonOutput);
        }
        catch (Exception ex) { Log.Warn($"Winget JSON query failed: {ex.Message}"); }

        try
        {
            var tableOutput = RunPackageManager("winget",
                new[] { "list", "--disable-interactivity", "--accept-source-agreements" }, ct);
            if (!string.IsNullOrWhiteSpace(tableOutput)) return ParseWingetTable(tableOutput);
        }
        catch (Exception ex) { Log.Warn($"Winget table query failed: {ex.Message}"); }
        return new();
    }

    internal static List<WingetEntry> ParseWingetJson(string json)
    {
        var entries = new List<WingetEntry>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var id = item.TryGetProperty("Id", out var i) ? i.GetString() ?? "" : "";
                var version = item.TryGetProperty("InstalledVersion", out var v)
                    ? v.GetString() ?? ""
                    : item.TryGetProperty("Version", out var v2) ? v2.GetString() ?? "" : "";
                var available = item.TryGetProperty("AvailableVersion", out var a) ? a.GetString() ?? "" : "";
                var source = item.TryGetProperty("Source", out var s) ? s.GetString() ?? "" : "";
                if (name.Length > 0 && id.Length > 0)
                    entries.Add(new WingetEntry(id, name, version, available, source));
            }
        }
        catch (Exception ex) { Log.Warn($"Winget JSON parse failed: {ex.Message}"); }
        return entries;
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
            UserIdentity.RealProfilePath,
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
                    catch (Exception ex) { Log.Warn($"Scoop install.json parse failed for '{name}': {ex.Message}"); }
                }

                if (File.Exists(manifestJson))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestJson));
                        if (doc.RootElement.TryGetProperty("version", out var v))
                            version = v.GetString() ?? "";
                    }
                    catch (Exception ex) { Log.Warn($"Scoop manifest.json parse failed for '{name}': {ex.Message}"); }
                }

                result.Add(new ScoopEntry(name, version, string.IsNullOrEmpty(bucket) ? "main" : bucket));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"Scoop directory enumeration failed: {ex.Message}"); }

        return result;
    }

    // ═══════════════════════════════════════════════════════
    //  chocolatey
    // ═══════════════════════════════════════════════════════

    public static List<ChocolateyEntry> QueryChocolatey(CancellationToken ct = default)
    {
        try
        {
            var output = RunPackageManager("chocolatey",
                new[] { "list", "--local-only", "--limit-output", "--no-color" }, ct);
            if (!string.IsNullOrWhiteSpace(output))
                return ParseChocolateyLimitOutput(output);
        }
        catch (Exception ex) { Log.Warn($"Chocolatey query failed: {ex.Message}"); }
        return new();
    }

    internal static List<ChocolateyEntry> ParseChocolateyLimitOutput(string output)
    {
        var entries = new List<ChocolateyEntry>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("Chocolatey v", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (string.IsNullOrWhiteSpace(parts[0])) continue;
            entries.Add(new ChocolateyEntry(parts[0], parts[1]));
        }
        return entries;
    }

    // ═══════════════════════════════════════════════════════
    //  helpers
    // ═══════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════
    //  source health
    // ═══════════════════════════════════════════════════════

    private static PackageSourceHealth CheckWingetHealth(CancellationToken ct)
    {
        var location = PackageManagerExecutableResolver.Resolve("winget");
        if (!location.Exists)
            return new(
                "winget",
                SelfTestStatus.Warn,
                "Not found at the original user's App Execution Alias",
                "",
                location.ExecutablePath,
                0,
                "resolved alias missing",
                "Install Microsoft App Installer for the desktop user, then verify its winget App Execution Alias is enabled.");

        var version = RunPackageManagerDetailed("winget", new[] { "--version" }, ct, timeoutMs: 5_000);
        if (!version.Started || version.ExitCode != 0)
            return new("winget", SelfTestStatus.Warn, "Not found at the original user's App Execution Alias", "", location.ExecutablePath, 0, version.Error,
                "Install Microsoft App Installer for the desktop user, then verify its winget App Execution Alias is enabled.");

        var versionText = FirstLine(version.OutputAndError);
        var json = RunPackageManagerDetailed("winget",
            new[] { "list", "--disable-interactivity", "--accept-source-agreements", "--output", "json" }, ct, timeoutMs: 10_000);
        if (!string.IsNullOrWhiteSpace(json.Output) && json.Output.TrimStart().StartsWith('['))
        {
            var entries = ParseWingetJson(json.Output);
            return new("winget", SelfTestStatus.Ok, $"{entries.Count} package(s) via JSON list; {location.ExecutionContext}", versionText, location.ExecutablePath, entries.Count, "JSON list parsed");
        }

        var table = RunPackageManagerDetailed("winget",
            new[] { "list", "--disable-interactivity", "--accept-source-agreements" }, ct, timeoutMs: 10_000);
        if (!string.IsNullOrWhiteSpace(table.Output))
        {
            var entries = ParseWingetTable(table.Output);
            if (entries.Count > 0 || table.Output.Contains("Name", StringComparison.OrdinalIgnoreCase))
                return new("winget", SelfTestStatus.Ok, $"{entries.Count} package(s) via table list; {location.ExecutionContext}", versionText, location.ExecutablePath, entries.Count, "table list parsed");
        }

        var detail = Shorten(json.OutputAndError.Length > 0 ? json.OutputAndError : table.OutputAndError);
        return new("winget", SelfTestStatus.Warn, $"Installed, but list output could not be parsed; {location.ExecutionContext}", versionText, location.ExecutablePath, 0, detail,
            "Run `winget source update`, then retry `winget list --disable-interactivity --accept-source-agreements`.");
    }

    private static PackageSourceHealth CheckScoopHealth(CancellationToken ct)
    {
        var location = PackageManagerExecutableResolver.Resolve("scoop");
        var root = Path.Combine(
            UserIdentity.RealProfilePath,
            "scoop", "apps");
        var version = location.Exists
            ? RunPackageManagerDetailed("scoop", new[] { "--version" }, ct, timeoutMs: 5_000)
            : new ProcessProbeResult(
                Started: false,
                ExitCode: -1,
                Output: "",
                Error: "Scoop command script not found.",
                TimedOut: false);
        var versionText = location.Exists && version.ExitCode == 0
            ? FirstLine(version.OutputAndError)
            : "";
        var health = InspectScoopRoot(root, versionText);
        return health with
        {
            Detail = $"{health.Detail}; command {location.ExecutablePath}; {location.ExecutionContext}",
        };
    }

    public static PackageSourceHealth InspectScoopRoot(string scoopAppsRoot, string version = "")
    {
        if (!Directory.Exists(scoopAppsRoot))
            return new("scoop", SelfTestStatus.Warn, "Scoop apps root not found", version, scoopAppsRoot, 0, "root missing",
                "Install Scoop or verify %USERPROFILE%\\scoop\\apps exists.");

        try
        {
            var count = Directory.EnumerateDirectories(scoopAppsRoot)
                .Select(Path.GetFileName)
                .Count(name => !string.IsNullOrWhiteSpace(name) &&
                               !name.Equals("scoop", StringComparison.OrdinalIgnoreCase));

            return new("scoop", SelfTestStatus.Ok, $"{count} app folder(s) in Scoop root", version, scoopAppsRoot, count, "root enumerated");
        }
        catch (Exception ex)
        {
            return new("scoop", SelfTestStatus.Warn, $"Scoop root unavailable: {ex.Message}", version, scoopAppsRoot, 0, ex.Message,
                "Check Scoop directory permissions and rerun under the affected Windows user.");
        }
    }

    private static PackageSourceHealth CheckChocolateyHealth(CancellationToken ct)
    {
        var location = PackageManagerExecutableResolver.Resolve("chocolatey");
        if (!location.Exists)
            return new(
                "chocolatey",
                SelfTestStatus.Warn,
                "Not found in a known Chocolatey installation root",
                "",
                location.ExecutablePath,
                0,
                "resolved executable missing",
                "Install Chocolatey for the desktop user or set ChocolateyInstall to its absolute installation root.");

        var version = RunPackageManagerDetailed("chocolatey", new[] { "--version" }, ct, timeoutMs: 5_000);
        if (!version.Started || version.ExitCode != 0)
            return new("chocolatey", SelfTestStatus.Warn, "Not found in a known Chocolatey installation root", "", location.ExecutablePath, 0, version.Error,
                "Install Chocolatey for the desktop user or set ChocolateyInstall to its absolute installation root.");

        var output = RunPackageManagerDetailed("chocolatey",
            new[] { "list", "--local-only", "--limit-output", "--no-color" }, ct, timeoutMs: 10_000);
        if (output.ExitCode == 0 && !string.IsNullOrWhiteSpace(output.Output))
        {
            var entries = ParseChocolateyLimitOutput(output.Output);
            return new("chocolatey", SelfTestStatus.Ok, $"{entries.Count} package(s) via choco list; {location.ExecutionContext}", FirstLine(version.OutputAndError), location.ExecutablePath, entries.Count, "limit-output parsed");
        }

        return new("chocolatey", SelfTestStatus.Warn, $"Installed, but local package list failed; {location.ExecutionContext}", FirstLine(version.OutputAndError), location.ExecutablePath, 0, Shorten(output.OutputAndError),
            "Run `choco list --local-only --limit-output --no-color` as the affected desktop user and inspect the error.");
    }

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

    private sealed record ProcessProbeResult(
        bool Started,
        int ExitCode,
        string Output,
        string Error,
        bool TimedOut)
    {
        public string OutputAndError => string.Join(
            Environment.NewLine,
            new[] { Output, Error }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    }

    private static string FirstLine(string value)
        => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";

    private static string Shorten(string value, int max = 180)
    {
        value = (value ?? "").ReplaceLineEndings(" ").Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }

    private static ProcessProbeResult RunPackageManagerDetailed(
        string packageManager,
        IReadOnlyList<string> args,
        CancellationToken ct,
        int timeoutMs)
    {
        var command = PackageManagerExecutableResolver.CreateCommand(
            packageManager,
            args,
            TimeSpan.FromMilliseconds(timeoutMs));
        var result = ExternalProcessRunner.Run(command, ct: ct);

        return new(
            result.Started,
            result.ExitCode,
            result.Output,
            result.StartError ?? result.Error,
            result.TimedOut);
    }

    private static string RunPackageManager(
        string packageManager,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var command = PackageManagerExecutableResolver.CreateCommand(
            packageManager,
            args,
            TimeSpan.FromMilliseconds(ProcessTimeoutMs));
        var result = ExternalProcessRunner.Run(command, ct: ct);
        ct.ThrowIfCancellationRequested();
        if (result.TimedOut)
            Log.Warn($"{packageManager} timed out after {ProcessTimeoutMs} ms");
        if (!result.Started && result.StartError is not null)
            Log.Warn($"{packageManager} failed to start: {result.StartError}");
        return result.Output;
    }
}
