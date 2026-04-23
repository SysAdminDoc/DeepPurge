// Local alias avoids the name collision between the in-project
// DeepPurge.Core.Diagnostics namespace and the global-usinged
// System.Diagnostics — the shorter "Diagnostics." prefix resolves
// ambiguously when both are in scope.
using DpDiag = DeepPurge.Core.Diagnostics;

using DeepPurge.Core.App;
using DeepPurge.Core.Cleaning;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Drivers;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.InstallMonitor;
using DeepPurge.Core.Privacy;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Repair;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Schedule;
using DeepPurge.Core.Shortcuts;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Uninstall;
using DeepPurge.Core.Updates;

namespace DeepPurge.Cli;

/// <summary>
/// Headless entry point — exposes every long-running DeepPurge workflow
/// for scripting / SCCM / Intune / Task Scheduler use.
///
/// Exit codes follow BCU convention:
///   0    = success
///   1    = general failure
///   2    = bad argument
///   13   = access denied
///   1223 = user cancelled (CTRL_C / uninstaller returned 1223)
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] rawArgs)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            if (rawArgs.Length == 0 || IsHelp(rawArgs[0])) { PrintHelp(); return 0; }
            var cmd = rawArgs[0].ToLowerInvariant();
            var args = ParsedArgs.From(rawArgs.Skip(1).ToArray());

            return cmd switch
            {
                "version"         => CmdVersion(),
                "portable"        => CmdPortable(args),
                "list"            => await CmdListAsync(cts.Token),
                "clean"           => await CmdCleanAsync(args, cts.Token),
                "uninstall"       => await CmdUninstallAsync(args, cts.Token),
                "repair"          => await CmdRepairAsync(args, cts.Token),
                "drivers"         => await CmdDriversAsync(args, cts.Token),
                "startup-impact"  => CmdStartupImpact(),
                "shortcuts"       => CmdShortcuts(args),
                "duplicates"      => await CmdDuplicatesAsync(args, cts.Token),
                "snapshot"        => await CmdSnapshotAsync(args, cts.Token),
                "winapp2"         => await CmdWinapp2Async(args, cts.Token),
                "schedule"        => CmdSchedule(args),
                "check-update"    => await CmdCheckUpdateAsync(cts.Token),
                "doctor"          => CmdDoctor(),
                _ => Fail($"Unknown command: {cmd}. Run 'deeppurgecli --help' for usage."),
            };
        }
        catch (OperationCanceledException) { return 1223; }
        catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"access denied: {ex.Message}"); return 13; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            Log.Error("CLI unhandled", ex);
            return 1;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  COMMANDS
    // ═══════════════════════════════════════════════════════

    private static int CmdVersion()
    {
        var asm = typeof(Program).Assembly.GetName().Version ?? new Version(0, 9, 0);
        Console.WriteLine($"DeepPurge CLI v{asm.ToString(3)}");
        Console.WriteLine(DataPaths.IsPortable ? "[portable mode]" : "[installed mode]");
        Console.WriteLine($"Data:     {DataPaths.Root}");
        Console.WriteLine($"Backups:  {DataPaths.Backups}");
        return 0;
    }

    private static int CmdPortable(ParsedArgs a)
    {
        if (a.HasFlag("enable"))
        {
            if (!DataPaths.TryEnablePortable(out var err))
            {
                Console.Error.WriteLine($"Cannot create portable marker: {err}");
                return 13;
            }
            Console.WriteLine("Portable mode enabled. Restart DeepPurge to pick up the marker.");
            return 0;
        }
        Console.WriteLine(DataPaths.IsPortable ? "Portable: ON" : "Portable: OFF");
        return 0;
    }

    private static async Task<int> CmdListAsync(CancellationToken ct)
    {
        var items = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms(), ct);
        foreach (var p in items.OrderBy(p => p.DisplayName))
            Console.WriteLine($"{p.DisplayName}\t{p.DisplayVersion}\t{p.Publisher}");
        Console.WriteLine($"# {items.Count} programs");
        return 0;
    }

    private static async Task<int> CmdCleanAsync(ParsedArgs a, CancellationToken ct)
    {
        bool dryRun = a.HasFlag("dry-run");
        bool secure = a.HasFlag("secure");
        var categories = a.Positional.Count > 0 ? a.Positional : new List<string> { "junk", "evidence" };

        var opt = new DeleteOptions(DryRun: dryRun, SecureDelete: secure, UseRecycleBin: !secure);
        long total = 0;
        foreach (var cat in categories)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"[{cat}] scanning...");
            long freed = 0;
            switch (cat.ToLowerInvariant())
            {
                case "junk":
                {
                    var scan = await Task.Run(() => JunkFilesCleaner.ScanForJunk(), ct);
                    foreach (var c in scan) c.IsSelected = true;
                    var s = await Task.Run(() => JunkFilesCleaner.DeleteJunkSafe(scan, opt, ProgressSink("junk"), ct), ct);
                    freed = s.BytesFreed;
                    break;
                }
                case "evidence":
                {
                    var cats = await Task.Run(() => EvidenceRemover.ScanAllTraces(), ct);
                    foreach (var c in cats) c.IsSelected = true;
                    var s = await Task.Run(() => EvidenceRemover.CleanTracesSafe(cats, opt, ProgressSink("evidence"), ct), ct);
                    freed = s.BytesFreed;
                    break;
                }
                default:
                    Console.Error.WriteLine($"unknown category: {cat} (expected junk | evidence)");
                    return 2;
            }
            Console.WriteLine();
            Console.WriteLine($"[{cat}] {(dryRun ? "would free" : "freed")} {FormatBytes(freed)}");
            total += freed;
        }
        Console.WriteLine($"Total: {FormatBytes(total)} {(dryRun ? "(dry-run)" : "")}");
        return 0;
    }

    private static async Task<int> CmdUninstallAsync(ParsedArgs a, CancellationToken ct)
    {
        if (a.Positional.Count == 0) return Fail("usage: deeppurgecli uninstall <name-or-id> [--silent]");
        bool silent = a.HasFlag("silent");
        var nameArg = a.Positional[0];

        var items = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms(), ct);
        var match = items.FirstOrDefault(p =>
            string.Equals(p.DisplayName,    nameArg, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.RegistryKeyName, nameArg, StringComparison.OrdinalIgnoreCase));
        if (match == null) return Fail($"program not found: {nameArg}");

        var engine = new UninstallEngine();
        engine.StatusChanged += s => Console.Error.WriteLine($"[status] {s}");
        Console.WriteLine($"Uninstalling {match.DisplayName}...");
        var result = await engine.UninstallAsync(match, DeepPurge.Core.Models.ScanMode.Moderate, silent: silent, ct: ct);
        Console.WriteLine($"[exit={result.ExitCode}] success={result.Success}");
        if (!string.IsNullOrWhiteSpace(result.Output)) Console.WriteLine(result.Output);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> CmdRepairAsync(ParsedArgs a, CancellationToken ct)
    {
        if (a.Positional.Count == 0) return Fail("usage: deeppurgecli repair <sfc|dism-scan|dism-restore|dism-cleanup|dism-resetbase|chkdsk|fontcache|iconcache>");
        var engine = new WindowsRepairEngine();
        RepairOperation? op = a.Positional[0].ToLowerInvariant() switch
        {
            "sfc"            => RepairOperation.SfcScan,
            "dism-scan"      => RepairOperation.DismScanHealth,
            "dism-restore"   => RepairOperation.DismRestoreHealth,
            "dism-cleanup"   => RepairOperation.DismComponentCleanup,
            "dism-resetbase" => RepairOperation.DismResetBase,
            "chkdsk"         => RepairOperation.ChkDsk,
            "fontcache"      => RepairOperation.RebuildFontCache,
            "iconcache"      => RepairOperation.RebuildIconCache,
            _ => null,
        };
        if (op == null) return Fail($"unknown repair op: {a.Positional[0]}");
        var log = new Progress<string>(Console.WriteLine);
        var r = await engine.RunAsync(op.Value, log, ct: ct);
        Console.WriteLine($"[exit={r.ExitCode}] elapsed {r.Elapsed}");
        return r.Success ? 0 : r.ExitCode;
    }

    private static async Task<int> CmdDriversAsync(ParsedArgs a, CancellationToken ct)
    {
        var pkgs = await new DriverStoreScanner().EnumerateAsync(ct);
        var oldOnly = a.HasFlag("old");
        foreach (var p in pkgs.Where(p => !oldOnly || p.IsOldVersion))
        {
            var tag = p.IsOldVersion ? "OLD" : "   ";
            Console.WriteLine($"[{tag}] {p.PublishedName,-12} {p.OriginalName,-28} {p.ProviderName,-22} {p.DriverVersion,-30} {FormatBytes(p.SizeBytes)}");
        }
        Console.WriteLine($"# {pkgs.Count(p => p.IsOldVersion)} old / {pkgs.Count} total");
        return 0;
    }

    private static int CmdStartupImpact()
    {
        var impacts = new StartupImpactCalculator().CalculateForCurrentUser();
        if (impacts.Count == 0)
        {
            Console.Error.WriteLine("No WDI startup traces available.");
            Console.Error.WriteLine("Possible causes: ran without admin, or the system has not booted since WDI was enabled.");
            return 1;
        }
        foreach (var e in impacts.Values.OrderByDescending(e => (int)e.Impact).ThenByDescending(e => e.DiskBytes))
            Console.WriteLine($"{e.Impact,-6} {e.ProcessName,-32} disk={FormatBytes(e.DiskBytes)} cpu={e.CpuMs}ms");
        return 0;
    }

    private static int CmdShortcuts(ParsedArgs a)
    {
        var scanner = new ShortcutRepairScanner();
        var shortcuts = scanner.ScanAll();
        var broken = shortcuts.Where(s => s.Status == ShortcutStatus.Broken).ToList();
        foreach (var s in broken) Console.WriteLine($"BROKEN  {s.Path}  ->  {s.TargetPath}");
        Console.WriteLine($"# {broken.Count} broken of {shortcuts.Count} total");
        if (a.HasFlag("delete") || a.HasFlag("recycle"))
        {
            var removed = scanner.RecycleBroken(broken);
            Console.WriteLine($"Moved {removed} broken shortcut(s) to Recycle Bin.");
        }
        return 0;
    }

    private static async Task<int> CmdDuplicatesAsync(ParsedArgs a, CancellationToken ct)
    {
        var roots = a.Positional.Count > 0
            ? a.Positional.ToArray()
            : new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(roots, progress: new Progress<string>(Console.Error.WriteLine), ct: ct);
        foreach (var g in groups)
        {
            Console.WriteLine($"[{FormatBytes(g.WastedBytes)} wasted, {g.Paths.Count} copies @ {FormatBytes(g.FileSize)}]");
            foreach (var p in g.Paths) Console.WriteLine($"  {p}");
        }
        Console.WriteLine($"# {groups.Count} duplicate groups, {FormatBytes(groups.Sum(g => g.WastedBytes))} reclaimable");
        return 0;
    }

    private static async Task<int> CmdSnapshotAsync(ParsedArgs a, CancellationToken ct)
    {
        if (a.Positional.Count == 0) return Fail("usage: deeppurgecli snapshot <trace> <name> <installer> [--args \"...\"]");
        if (!a.Positional[0].Equals("trace", StringComparison.OrdinalIgnoreCase))
            return Fail("snapshot: only 'trace' subcommand supported today");
        if (a.Positional.Count < 3) return Fail("snapshot trace: need <name> and <installer>");

        var name = a.Positional[1];
        var installer = a.Positional[2];
        var extraArgs = a.GetOption("args");

        var engine = new InstallSnapshotEngine();
        var delta = await engine.TraceInstallAsync(name, installer, extraArgs, ct);
        Console.WriteLine($"Added files: {delta.AddedFiles.Count} ({FormatBytes(delta.TotalAddedBytes)})");
        Console.WriteLine($"Added regkeys: {delta.AddedRegistryKeys.Count}");
        Console.WriteLine($"Removed files: {delta.RemovedFiles.Count}");
        Console.WriteLine($"Removed regkeys: {delta.RemovedRegistryKeys.Count}");
        return 0;
    }

    private static async Task<int> CmdWinapp2Async(ParsedArgs a, CancellationToken ct)
    {
        if (a.Positional.Count == 0) return Fail("usage: deeppurgecli winapp2 <path-to-winapp2.ini> [--dry-run]");
        var path = a.Positional[0];
        var dryRun = a.HasFlag("dry-run");
        if (!File.Exists(path)) return Fail($"not found: {path}");

        var entries = Winapp2Parser.ParseFile(path);
        Console.WriteLine($"Loaded {entries.Count} entries from {path}");

        var runner = new Winapp2Runner();
        var summary = await runner.RunAsync(
            entries,
            new DeleteOptions(DryRun: dryRun),
            new Progress<DeleteProgress>(p => Console.Error.Write($"\r{Truncate(p.CurrentItem, 60),-60} ({p.ItemsProcessed}/{p.ItemsTotal})")),
            ct);
        Console.WriteLine();
        Console.WriteLine($"{(dryRun ? "Would free" : "Freed")}: {FormatBytes(summary.BytesFreed)} ({summary.ItemsDeleted} entries, {summary.ItemsSkipped} skipped)");
        return 0;
    }

    private static int CmdSchedule(ParsedArgs a)
    {
        var mgr = new ScheduleManager();
        var sub = a.Positional.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "list";

        switch (sub)
        {
            case "list":
                foreach (var j in mgr.ListJobs()) Console.WriteLine(j);
                return 0;

            case "add":
            {
                // deeppurgecli schedule add --name X --freq weekly --time 03:00 --day Mon --args "clean junk evidence"
                var name = a.GetOption("name");
                var freqStr = a.GetOption("freq") ?? "weekly";
                var timeStr = a.GetOption("time");
                var dayStr  = a.GetOption("day");
                var cliArgs = a.GetOption("args") ?? "clean junk evidence";

                if (string.IsNullOrWhiteSpace(name))  return Fail("schedule add: --name is required");
                if (string.IsNullOrWhiteSpace(timeStr)) return Fail("schedule add: --time HH:MM is required");

                var freq = freqStr.Equals("daily",   StringComparison.OrdinalIgnoreCase) ? ScheduleFrequency.Daily :
                           freqStr.Equals("monthly", StringComparison.OrdinalIgnoreCase) ? ScheduleFrequency.Monthly :
                                                                                          ScheduleFrequency.Weekly;

                var tParts = timeStr!.Split(':');
                if (!int.TryParse(tParts[0], out var hh) ||
                    !int.TryParse(tParts.ElementAtOrDefault(1) ?? "0", out var mm))
                    return Fail($"schedule add: bad time '{timeStr}' (expected HH:MM)");

                var dow = DayOfWeek.Monday;
                if (!string.IsNullOrEmpty(dayStr) && !Enum.TryParse(dayStr, true, out dow))
                    return Fail($"schedule add: unknown day '{dayStr}'");

                var cliPath = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath unavailable");
                var ok = mgr.CreateJob(new ScheduleJob(name!, freq, dow, hh, mm, cliArgs), cliPath);
                Console.WriteLine(ok ? $"Scheduled: {name}" : "Failed to schedule. See log.");
                return ok ? 0 : 1;
            }

            case "remove":
            {
                var name = a.GetOption("name") ?? a.Positional.ElementAtOrDefault(1);
                if (string.IsNullOrWhiteSpace(name)) return Fail("schedule remove: --name or positional name required");
                var ok = mgr.DeleteJob(name!);
                Console.WriteLine(ok ? $"Removed: {name}" : "Failed to remove.");
                return ok ? 0 : 1;
            }

            default:
                return Fail("usage: deeppurgecli schedule <list|add|remove> [--name ...] [--freq ...] [--time HH:MM] [--day Mon] [--args \"...\"]");
        }
    }

    private static int CmdDoctor()
    {
        Console.WriteLine("DeepPurge Doctor - environment self-test");
        Console.WriteLine("----------------------------------------");
        var results = DpDiag.SelfTest.RunAll();
        int fails = 0, warns = 0;
        foreach (var r in results)
        {
            var tag = r.Status switch
            {
                DpDiag.SelfTestStatus.Ok   => "[ OK ]",
                DpDiag.SelfTestStatus.Warn => "[WARN]",
                DpDiag.SelfTestStatus.Fail => "[FAIL]",
                _                          => "[skip]",
            };
            if      (r.Status == DpDiag.SelfTestStatus.Fail) fails++;
            else if (r.Status == DpDiag.SelfTestStatus.Warn) warns++;
            Console.WriteLine($"{tag} {r.Check,-20} {r.Detail}");
            if (!string.IsNullOrWhiteSpace(r.Hint) && r.Status != DpDiag.SelfTestStatus.Ok)
                Console.WriteLine($"       -> {r.Hint}");
        }
        Console.WriteLine("----------------------------------------");
        Console.WriteLine($"Summary: {results.Count - fails - warns} ok, {warns} warn, {fails} fail");
        return fails > 0 ? 1 : 0;
    }

    private static async Task<int> CmdCheckUpdateAsync(CancellationToken ct)
    {
        var cur = (typeof(Program).Assembly.GetName().Version ?? new Version(0, 9, 0)).ToString(3);
        var info = await new UpdateChecker().CheckAsync(cur, ct);
        if (info == null) { Console.WriteLine("(update check failed)"); return 1; }
        Console.WriteLine($"Current: v{info.CurrentVersion}");
        Console.WriteLine($"Latest:  v{info.LatestVersion}");
        Console.WriteLine(info.HasUpdate ? $"Update available: {info.ReleaseUrl}" : "Up to date.");
        return 0;
    }

    // ═══════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════

    private static IProgress<DeleteProgress> ProgressSink(string label) =>
        new Progress<DeleteProgress>(p =>
            Console.Error.Write($"\r[{label}] {Truncate(p.CurrentItem, 50),-50} ({p.ItemsProcessed}/{p.ItemsTotal})"));

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b,6:F1} {u[i]}";
    }

    private static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

    private static bool IsHelp(string a) => a is "--help" or "-h" or "help" or "/?";

    private static void PrintHelp()
    {
        Console.WriteLine("DeepPurge CLI — headless system cleaner / uninstaller");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  version                                  Show build + data paths");
        Console.WriteLine("  portable [--enable]                      Query or toggle portable mode");
        Console.WriteLine("  list                                     List installed programs (TSV)");
        Console.WriteLine("  uninstall <name> [--silent]              Uninstall a program");
        Console.WriteLine("  clean [junk|evidence ...] [--dry-run] [--secure]");
        Console.WriteLine("  repair <sfc|dism-scan|dism-restore|dism-cleanup|dism-resetbase|chkdsk|fontcache|iconcache>");
        Console.WriteLine("  drivers [--old]                          List third-party drivers in DriverStore");
        Console.WriteLine("  startup-impact                           Show boot-time cost per process");
        Console.WriteLine("  shortcuts [--recycle]                    Scan Desktop/Start Menu for broken .lnk");
        Console.WriteLine("  duplicates [roots...]                    Find duplicate files");
        Console.WriteLine("  snapshot trace <name> <installer> [--args \"...\"]");
        Console.WriteLine("  winapp2 <path.ini> [--dry-run]           Run community cleaner definitions");
        Console.WriteLine("  schedule list");
        Console.WriteLine("  schedule add --name N --time HH:MM [--freq daily|weekly|monthly] [--day Mon] [--args \"...\"]");
        Console.WriteLine("  schedule remove --name N");
        Console.WriteLine("  check-update                             Check GitHub for a newer release");
        Console.WriteLine("  doctor                                   Run environment self-test + report");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 ok | 1 fail | 2 bad args | 13 access denied | 1223 cancelled");
    }
}

/// <summary>
/// Lightweight argument parser.
///
/// Rules:
///   - <c>--flag</c>            : boolean flag, stored in <see cref="Flags"/>.
///   - <c>--option value</c>    : name/value, stored in <see cref="Options"/>.
///   - <c>--option=value</c>    : same, single-token form.
///   - anything else            : positional, stored in <see cref="Positional"/>.
///
/// This replaces the prior regex-free positional-only parser that mis-parsed
/// <c>--args "clean junk evidence"</c> because the shell already split the
/// quoted run, and the handler then tried to re-consume tokens by position.
/// </summary>
public sealed class ParsedArgs
{
    public List<string> Positional { get; } = new();
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Options that consume the next token as a value, even without `=`. Any
    // other `--xxx` is treated as a boolean flag. Add new value-options here
    // when you add a new command that needs them.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "freq", "time", "day", "args",
    };

    public bool HasFlag(string name) => Flags.Contains(name);
    public string? GetOption(string name) => Options.TryGetValue(name, out var v) ? v : null;

    public static ParsedArgs From(string[] argv)
    {
        var result = new ParsedArgs();
        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var raw = a[2..];
                int eq = raw.IndexOf('=');
                if (eq >= 0)
                {
                    result.Options[raw[..eq]] = raw[(eq + 1)..];
                    continue;
                }
                if (ValueOptions.Contains(raw) && i + 1 < argv.Length)
                {
                    result.Options[raw] = argv[++i];
                }
                else
                {
                    result.Flags.Add(raw);
                }
                continue;
            }
            result.Positional.Add(a);
        }
        return result;
    }
}
