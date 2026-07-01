// Local alias avoids the name collision between the in-project
// DeepPurge.Core.Diagnostics namespace and the global-usinged
// System.Diagnostics — the shorter "Diagnostics." prefix resolves
// ambiguously when both are in scope.
using DpDiag = DeepPurge.Core.Diagnostics;

using DeepPurge.Core.App;
using DeepPurge.Core.Cleaning;
using DeepPurge.Core.Data;
using DeepPurge.Core.Export;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Drivers;
using DeepPurge.Core.Firewall;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.InstallMonitor;
using DeepPurge.Core.Privacy;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Registry;
using DeepPurge.Core.Repair;
using DeepPurge.Core.Safety;
using DeepPurge.Core.Schedule;
using DeepPurge.Core.Shell;
using DeepPurge.Core.Shortcuts;
using DeepPurge.Core.Startup;
using DeepPurge.Core.Uninstall;
using DeepPurge.Core.Updates;
using System.Text.Json;
using System.Text.Json.Serialization;

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
                "list"            => await CmdListAsync(args, cts.Token),
                "clean"           => await CmdCleanAsync(args, cts.Token),
                "uninstall"       => await CmdUninstallAsync(args, cts.Token),
                "repair"          => await CmdRepairAsync(args, cts.Token),
                "drivers"         => await CmdDriversAsync(args, cts.Token),
                "startup-impact"  => CmdStartupImpact(args),
                "shortcuts"       => CmdShortcuts(args),
                "duplicates"      => await CmdDuplicatesAsync(args, cts.Token),
                "snapshot"        => await CmdSnapshotAsync(args, cts.Token),
                "winapp2"         => await CmdWinapp2Async(args, cts.Token),
                "schedule"        => CmdSchedule(args),
                "orphans"         => CmdOrphans(args),
                "check-update"    => await CmdCheckUpdateAsync(cts.Token),
                "detection-script"=> CmdDetectionScript(args),
                "doctor"          => CmdDoctor(),
                "register-shell"  => CmdRegisterShell(),
                "unregister-shell"=> CmdUnregisterShell(),
                "cleaners"        => CmdCleaners(args),
                "settings"        => CmdSettings(args),
                "update-winapp2"  => await CmdUpdateWinapp2Async(args, cts.Token),
                "restore"         => CmdRestore(args),
                "note"            => CmdNote(args),
                "support-bundle"  => CmdSupportBundle(args),
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

    private static async Task<int> CmdListAsync(ParsedArgs a, CancellationToken ct)
    {
        var items = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms(), ct);
        if (!a.HasFlag("registry-only"))
            await PackageManagerScanner.EnrichAsync(items, ct);
        PrefetchScanner.EnrichWithLastUsed(items);
        var sorted = items.OrderBy(p => p.DisplayName).ToList();

        if (a.HasFlag("json"))
        {
            var notes = AppSettings.Current.ProgramNotes;
            WriteJson(sorted.Select(p => new {
                p.DisplayName, p.DisplayVersion, p.Publisher,
                Source = p.SourceDisplay, p.PackageId, p.LastUsedDisplay,
                p.InstallDate, p.EstimatedSizeKB, p.InstallLocation,
                p.SignatureDisplay,
                Flags = p.FlagsDisplay,
                p.IsSuspectedBundleware,
                p.OemBloatScore,
                p.OemBloatReason,
                Note = notes.TryGetValue(p.DisplayName ?? "", out var n) ? n : null,
            }));
            return 0;
        }

        foreach (var p in sorted)
        {
            var source = p.SourceDisplay;
            var pkgId = !string.IsNullOrEmpty(p.PackageId) ? $"\t{p.PackageId}" : "";
            var lastUsed = !string.IsNullOrEmpty(p.LastUsedDisplay) ? $"\t{p.LastUsedDisplay}" : "";
            var flags = !string.IsNullOrEmpty(p.FlagsDisplay) ? $"\t{p.FlagsDisplay}" : "";
            Console.WriteLine($"{p.DisplayName}\t{p.DisplayVersion}\t{p.Publisher}\t{source}{pkgId}{lastUsed}{flags}");
        }
        Console.WriteLine($"# {sorted.Count} programs");
        return 0;
    }

    private sealed record CleanCategoryResult(
        string Category,
        long BytesFreed,
        int ItemsDeleted,
        int ItemsSkipped,
        IReadOnlyList<string> SkippedReasons);

    private static async Task<int> CmdCleanAsync(ParsedArgs a, CancellationToken ct)
    {
        bool dryRun = a.HasFlag("dry-run");
        bool secure = a.HasFlag("secure");
        int minAge = int.TryParse(a.GetOption("min-age"), out var ma) ? ma : 0;
        var categories = a.Positional.Count > 0 ? a.Positional : new List<string> { "junk", "evidence" };

        var keepCookies = a.GetOption("keep-cookies");
        if (!string.IsNullOrEmpty(keepCookies))
        {
            var domains = keepCookies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppSettings.Current.CookieWhitelist = domains.ToList();
        }

        var opt = new DeleteOptions(DryRun: dryRun, SecureDelete: secure, UseRecycleBin: !secure, MinAgeDays: minAge);
        long total = 0;
        int skippedTotal = 0;
        var results = new List<CleanCategoryResult>();
        foreach (var cat in categories)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"[{cat}] scanning...");
            DeleteSummary summary;
            switch (cat.ToLowerInvariant())
            {
                case "junk":
                {
                    var scan = await Task.Run(() => JunkFilesCleaner.ScanForJunk(), ct);
                    foreach (var c in scan) c.IsSelected = true;
                    summary = await Task.Run(() => JunkFilesCleaner.DeleteJunkSafe(scan, opt, ProgressSink("junk"), ct), ct);
                    break;
                }
                case "evidence":
                {
                    var cats = await Task.Run(() => EvidenceRemover.ScanAllTraces(), ct);
                    foreach (var c in cats) c.IsSelected = true;
                    summary = await Task.Run(() => EvidenceRemover.CleanTracesSafe(cats, opt, ProgressSink("evidence"), ct), ct);
                    break;
                }
                case "dev":
                {
                    var root = a.GetOption("path") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var devDirs = await Task.Run(() => DeepPurge.Core.FileSystem.DevDirectoryScanner.Scan(root, ct), ct);
                    foreach (var d in devDirs) Console.WriteLine($"  {d.Type,-16} {FormatBytes(d.SizeBytes),10}  {d.Path}");
                    summary = await Task.Run(() => DeepPurge.Core.FileSystem.DevDirectoryScanner.Delete(devDirs, opt, ProgressSink("dev"), ct), ct);
                    break;
                }
                default:
                    Console.Error.WriteLine($"unknown category: {cat} (expected junk | evidence | dev)");
                    return 2;
            }
            Console.WriteLine();
            Console.WriteLine($"[{cat}] {(dryRun ? "would free" : "freed")} {FormatBytes(summary.BytesFreed)} ({summary.ItemsDeleted} items, {summary.ItemsSkipped} skipped)");
            foreach (var reason in summary.SkippedReasons.Take(10))
                Console.Error.WriteLine($"  skipped: {reason}");

            results.Add(new CleanCategoryResult(
                cat,
                summary.BytesFreed,
                summary.ItemsDeleted,
                summary.ItemsSkipped,
                summary.SkippedReasons));
            total += summary.BytesFreed;
            skippedTotal += summary.ItemsSkipped;
        }
        if (a.HasFlag("json"))
        {
            WriteJson(new { total, categories = results, dryRun });
        }
        else
        {
            Console.WriteLine($"Total: {FormatBytes(total)} {(dryRun ? "(dry-run)" : "")}" + (skippedTotal > 0 ? $" | {skippedTotal} skipped" : ""));
        }
        if (!dryRun && total > 0)
            DeepPurge.Core.Diagnostics.ActivityLog.Record(
                "CLI Clean",
                $"{categories.Count} categories" + (skippedTotal > 0 ? $"; {skippedTotal} skipped" : ""),
                total,
                categories.Count);
        return 0;
    }

    private static async Task<int> CmdUninstallAsync(ParsedArgs a, CancellationToken ct)
    {
        if (a.Positional.Count == 0) return Fail("usage: deeppurgecli uninstall <name-or-id> [--silent] [--dry-run] [--timeout <minutes>]");
        bool silent = a.HasFlag("silent");
        bool dryRun = a.HasFlag("dry-run");
        var nameArg = a.Positional[0];
        var timeoutStr = a.GetOption("timeout");
        if (timeoutStr != null && int.TryParse(timeoutStr, out var mins))
            UninstallEngine.UninstallerTimeout = TimeSpan.FromMinutes(mins);

        var items = await Task.Run(() => InstalledProgramScanner.GetAllInstalledPrograms(), ct);
        await PackageManagerScanner.EnrichAsync(items, ct);
        var match = items.FirstOrDefault(p =>
            string.Equals(p.DisplayName,    nameArg, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.RegistryKeyName, nameArg, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.PackageId,       nameArg, StringComparison.OrdinalIgnoreCase));
        if (match == null) return Fail($"program not found: {nameArg}");

        var engine = new UninstallEngine();
        engine.StatusChanged += s => Console.Error.WriteLine($"[status] {s}");
        Console.WriteLine(dryRun ? $"Previewing uninstall for {match.DisplayName}..." : $"Uninstalling {match.DisplayName}...");
        var result = await engine.UninstallAsync(
            match,
            DeepPurge.Core.Models.ScanMode.Moderate,
            silent: silent,
            dryRun: dryRun,
            ct: ct);
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
        var filtered = pkgs.Where(p => !oldOnly || p.IsOldVersion).ToList();

        var exportPath = ValidateExportPath(a.GetOption("export"));
        if (a.GetOption("export") != null && exportPath == null) return 2;
        if (exportPath != null)
        {
            var fmt = ParseExportFormat(a);
            GridExporter.ExportDrivers(filtered, exportPath, fmt);
            Console.WriteLine($"Exported {filtered.Count} drivers to {exportPath}");
            return 0;
        }

        if (a.HasFlag("json"))
        {
            WriteJson(filtered.Select(p => new {
                p.PublishedName, p.OriginalName, p.ProviderName, p.DriverVersion,
                p.DriverDate, p.SizeBytes, p.IsOldVersion
            }));
            return 0;
        }

        foreach (var p in filtered)
        {
            var tag = p.IsOldVersion ? "OLD" : "   ";
            Console.WriteLine($"[{tag}] {p.PublishedName,-12} {p.OriginalName,-28} {p.ProviderName,-22} {p.DriverVersion,-30} {FormatBytes(p.SizeBytes)}");
        }
        Console.WriteLine($"# {pkgs.Count(p => p.IsOldVersion)} old / {pkgs.Count} total");
        return 0;
    }

    private static int CmdStartupImpact(ParsedArgs a)
    {
        var impacts = new StartupImpactCalculator().CalculateForCurrentUser();
        if (impacts.Count == 0)
        {
            Console.Error.WriteLine("No WDI startup traces available.");
            Console.Error.WriteLine("Possible causes: ran without admin, or the system has not booted since WDI was enabled.");
            return 1;
        }
        var sorted = impacts.Values.OrderByDescending(e => (int)e.Impact).ThenByDescending(e => e.DiskBytes).ToList();

        var exportPath = ValidateExportPath(a.GetOption("export"));
        if (a.GetOption("export") != null && exportPath == null) return 2;
        if (exportPath != null)
        {
            GridExporter.ExportStartupImpact(sorted, exportPath, ParseExportFormat(a));
            Console.WriteLine($"Exported {sorted.Count} entries to {exportPath}");
            return 0;
        }

        if (a.HasFlag("json"))
        {
            WriteJson(sorted.Select(e => new {
                e.ProcessName, Impact = e.Impact.ToString(), e.DiskBytes, e.CpuMs
            }));
            return 0;
        }

        foreach (var e in sorted)
            Console.WriteLine($"{e.Impact,-6} {e.ProcessName,-32} disk={FormatBytes(e.DiskBytes)} cpu={e.CpuMs}ms");
        return 0;
    }

    private static int CmdShortcuts(ParsedArgs a)
    {
        var scanner = new ShortcutRepairScanner();
        var shortcuts = scanner.ScanAll();
        var broken = shortcuts.Where(s => s.Status == ShortcutStatus.Broken).ToList();

        var exportPath = ValidateExportPath(a.GetOption("export"));
        if (a.GetOption("export") != null && exportPath == null) return 2;
        if (exportPath != null)
        {
            var exportSet = a.HasFlag("all") ? shortcuts : broken;
            GridExporter.ExportShortcuts(exportSet, exportPath, ParseExportFormat(a));
            Console.WriteLine($"Exported {exportSet.Count} shortcuts to {exportPath}");
            return 0;
        }

        if (a.HasFlag("json"))
        {
            var exportSet = a.HasFlag("all") ? shortcuts : broken;
            WriteJson(exportSet.Select(s => new { s.Path, s.TargetPath, Status = s.Status.ToString(), s.Description }));
            return 0;
        }

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

        if (a.HasFlag("dirs"))
        {
            var dirGroups = await finder.FindDuplicateDirectoriesAsync(roots, progress: new Progress<string>(Console.Error.WriteLine), ct: ct);
            foreach (var g in dirGroups)
            {
                Console.WriteLine($"[{FormatBytes(g.WastedBytes)} wasted, {g.Paths.Count} copies, {g.FileCount} files @ {FormatBytes(g.TotalSize)}]");
                foreach (var p in g.Paths) Console.WriteLine($"  {p}");
            }
            Console.WriteLine($"# {dirGroups.Count} duplicate directory groups, {FormatBytes(dirGroups.Sum(g => g.WastedBytes))} reclaimable");
            return 0;
        }

        var groups = await finder.FindAsync(roots, progress: new Progress<string>(Console.Error.WriteLine), ct: ct);

        var exportPath = ValidateExportPath(a.GetOption("export"));
        if (a.GetOption("export") != null && exportPath == null) return 2;
        if (exportPath != null)
        {
            GridExporter.ExportDuplicates(groups, exportPath, ParseExportFormat(a));
            Console.WriteLine($"Exported {groups.Count} groups to {exportPath}");
            return 0;
        }

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
        var useV2 = !a.HasFlag("legacy") && UsnJournalReader.IsSupported(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ?? @"C:\");
        if (useV2) Console.WriteLine("[v2 mode: USN journal + registry snapshot]");
        var delta = useV2
            ? await engine.TraceInstallV2Async(name, installer, extraArgs, ct)
            : await engine.TraceInstallAsync(name, installer, extraArgs, ct);
        if (a.HasFlag("json"))
        {
            WriteJson(new {
                delta.IsUpgrade,
                AddedFiles = delta.AddedFiles.Select(f => new { f.Path, f.SizeBytes }),
                delta.AddedRegistryKeys, delta.RemovedFiles, delta.RemovedRegistryKeys,
                delta.TotalAddedBytes,
            });
            return 0;
        }

        if (delta.IsUpgrade) Console.WriteLine("[upgrade detected — showing diff against prior version]");
        Console.WriteLine($"Added files:      {delta.AddedFiles.Count,5} ({FormatBytes(delta.TotalAddedBytes)})");
        Console.WriteLine($"Added regkeys:    {delta.AddedRegistryKeys.Count,5}");
        Console.WriteLine($"Removed files:    {delta.RemovedFiles.Count,5}");
        Console.WriteLine($"Removed regkeys:  {delta.RemovedRegistryKeys.Count,5}");
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
                var jobs = mgr.ListJobs();
                if (a.HasFlag("json")) { WriteJson(jobs); return 0; }
                foreach (var j in jobs) Console.WriteLine(j);
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

    private static int CmdDetectionScript(ParsedArgs a)
    {
        if (a.Positional.Count == 0) return Fail("usage: deeppurgecli detection-script --program \"Program Name\" [--export file.ps1]");
        var nameArg = a.GetOption("program") ?? a.Positional[0];
        var items = InstalledProgramScanner.GetAllInstalledPrograms();
        var match = items.FirstOrDefault(p =>
            string.Equals(p.DisplayName, nameArg, StringComparison.OrdinalIgnoreCase) ||
            (p.DisplayName != null && p.DisplayName.Contains(nameArg, StringComparison.OrdinalIgnoreCase)));
        if (match == null) return Fail($"program not found: {nameArg}");

        var regPath = match.RegistryPath ?? $@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{match.RegistryKeyName}";
        var version = match.DisplayVersion ?? "1.0";

        var script = $@"# Intune/SCCM detection script for: {match.DisplayName}
# Generated by DeepPurge CLI
# Exit 0 + stdout = detected. Exit 1 + no stdout = not detected.

$regPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

$app = Get-ItemProperty -Path $regPaths -ErrorAction SilentlyContinue |
       Where-Object {{ $_.DisplayName -eq '{match.DisplayName.Replace("'", "''")}' }}

if ($app) {{
    Write-Output ""Detected: $($app.DisplayName) v$($app.DisplayVersion)""
    exit 0
}} else {{
    exit 1
}}
";
        var exportPath = ValidateExportPath(a.GetOption("export"));
        if (a.GetOption("export") != null && exportPath == null) return 2;
        if (exportPath != null)
        {
            File.WriteAllText(exportPath, script, System.Text.Encoding.UTF8);
            Console.WriteLine($"Detection script written to {exportPath}");
        }
        else
        {
            Console.Write(script);
        }
        return 0;
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

    private static int CmdRegisterShell()
    {
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) { Console.Error.WriteLine("Could not determine exe path"); return 1; }
        var guiPath = Path.Combine(Path.GetDirectoryName(exePath)!, "DeepPurge.exe");
        if (!File.Exists(guiPath)) guiPath = exePath;
        if (ShellExtensionRegistrar.Register(guiPath))
        {
            Console.WriteLine($"Shell extension registered: {guiPath}");
            Console.WriteLine("Right-click any .exe → 'Uninstall with DeepPurge'");
            return 0;
        }
        Console.Error.WriteLine("Failed to register shell extension");
        return 1;
    }

    private static int CmdUnregisterShell()
    {
        if (ShellExtensionRegistrar.Unregister())
        {
            Console.WriteLine("Shell extension removed.");
            return 0;
        }
        Console.Error.WriteLine("Failed to unregister shell extension");
        return 1;
    }

    private static int CmdCleaners(ParsedArgs a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : "list";
        if (sub == "schema")
        {
            var schema = CleanerDefinitionRunner.GetSchemaJson();
            var exportPath = ValidateExportPath(a.GetOption("export"));
            if (a.GetOption("export") != null && exportPath == null) return 2;
            if (exportPath != null)
            {
                File.WriteAllText(exportPath, schema, System.Text.Encoding.UTF8);
                Console.WriteLine($"Cleaner schema written to {exportPath}");
            }
            else
            {
                Console.WriteLine(schema);
            }
            return 0;
        }

        if (sub == "validate")
        {
            var path = a.Positional.Count > 1 ? a.Positional[1] : null;
            if (string.IsNullOrWhiteSpace(path))
                return Fail("usage: deeppurgecli cleaners validate <file.cleaner.json> [--json]");

            var report = CleanerDefinitionRunner.ValidateFile(path);
            if (a.HasFlag("json")) WriteJson(report);
            else PrintCleanerValidationReport(report);
            return report.IsValid ? 0 : 1;
        }

        var reports = CleanerDefinitionRunner.ValidateAll();
        var invalidReports = reports.Where(r => !r.IsValid).ToList();
        var rules = reports.Where(r => r.IsValid).SelectMany(r => r.Rules).ToList();
        var applicable = CleanerDefinitionRunner.FilterApplicable(rules);

        if (invalidReports.Count > 0)
            Console.Error.WriteLine($"{invalidReports.Count} invalid cleaner definition file(s) skipped. Run 'deeppurgecli cleaners validate <file>' for details.");

        switch (sub)
        {
            case "list":
                if (applicable.Count == 0) { Console.WriteLine("No applicable custom cleaners found."); return 0; }
                Console.WriteLine($"{"Name",-30} {"Risk",-8} {"Estimate",-14} {"Description",-40} Rules");
                foreach (var r in applicable)
                {
                    var report = FindCleanerReport(reports, r);
                    var estimate = report == null ? "(unknown)" : $"{report.EstimatedItems} / {FormatBytes(report.EstimatedBytes)}";
                    Console.WriteLine($"{r.Name,-30} {report?.RiskLabel ?? "Low",-8} {estimate,-14} {r.Description,-40} {r.Files.Count}F/{r.Registry.Count}R");
                }
                Console.WriteLine($"\n# {applicable.Count} applicable cleaners (from {rules.Count} loaded)");
                return 0;

            case "preview":
                foreach (var r in applicable)
                {
                    var (size, count) = CleanerDefinitionRunner.Preview(r);
                    if (count > 0) Console.WriteLine($"{r.Name,-30} {count,6} items  {size / 1024,8} KB");
                }
                return 0;

            case "run":
                if (applicable.Count == 0) { Console.WriteLine("No applicable cleaners."); return 0; }
                bool dryRun = a.HasFlag("dry-run");
                var opt = new DeleteOptions(DryRun: dryRun, SecureDelete: false, UseRecycleBin: false);
                foreach (var r in applicable)
                {
                    var result = CleanerDefinitionRunner.Execute(r, opt);
                    var verb = dryRun ? "Would clean" : "Cleaned";
                    Console.WriteLine($"{verb} {r.Name}: {result.ItemsDeleted} items, {result.BytesFreed / 1024} KB freed");
                }
                return 0;

            default:
                return Fail("usage: deeppurgecli cleaners [list|preview|run [--dry-run]|validate <file.cleaner.json>|schema [--export file.json]]");
        }
    }

    private static CleanerValidationReport? FindCleanerReport(IEnumerable<CleanerValidationReport> reports, CleanerRule rule)
        => reports.FirstOrDefault(r => r.Rules.Contains(rule));

    private static void PrintCleanerValidationReport(CleanerValidationReport report)
    {
        Console.WriteLine($"File:            {report.FilePath}");
        Console.WriteLine($"Schema:          {report.SchemaDisplay} ({report.SchemaId})");
        Console.WriteLine($"Provenance:      {report.ProvenanceDisplay}");
        Console.WriteLine($"Status:          {report.Status}");
        Console.WriteLine($"Risk:            {report.RiskLabel}");
        Console.WriteLine($"Rules:           {report.Rules.Count}");
        Console.WriteLine($"Estimate:        {report.EstimatedItems} item(s), {FormatBytes(report.EstimatedBytes)}");
        Console.WriteLine($"Registry scopes: {report.RegistryScopesDisplay}");
        Console.WriteLine($"Targets:         {report.ExpandedTargetsDisplay}");

        if (report.Issues.Count == 0)
        {
            Console.WriteLine("Issues:          none");
            return;
        }

        Console.WriteLine("Issues:");
        foreach (var issue in report.Issues)
            Console.WriteLine($"  [{issue.Severity}] {issue.RuleName}.{issue.Field}: {issue.Message}");
    }

    private static int CmdSettings(ParsedArgs a)
    {
        var sub = a.Positional.Count > 0 ? a.Positional[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "export":
            {
                var path = a.Positional.Count > 1 ? a.Positional[1] : null;
                var validated = ValidateExportPath(path);
                if (path != null && validated == null) return 2;
                if (validated == null) return Fail("usage: deeppurgecli settings export <path.json>");
                var preview = AppSettings.Current.ExportTo(validated);
                Console.WriteLine($"Settings exported to {validated}");
                Console.WriteLine($"Preview: {preview.ToRedactedSummary()}");
                return 0;
            }
            case "import":
            {
                var path = a.Positional.Count > 1 ? a.Positional[1] : null;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return Fail("usage: deeppurgecli settings import <path.json> [--preview]");

                var plan = AppSettings.PreviewImportFromFile(path);
                PrintSettingsImportPlan(plan);
                if (!plan.IsValid) return 2;
                if (a.HasFlag("preview")) return 0;

                var outcome = AppSettings.ImportAndApply(path);
                Console.WriteLine($"Settings imported from {path}");
                Console.WriteLine(string.IsNullOrWhiteSpace(outcome.BackupPath)
                    ? "Rollback: no previous settings file existed"
                    : $"Rollback: previous settings saved to {outcome.BackupPath}");
                return 0;
            }
            case "show":
            {
                var s = AppSettings.Current;
                Console.WriteLine($"ExpertMode:         {s.ExpertMode}");
                Console.WriteLine($"MinAgeDaysJunk:     {s.MinAgeDaysJunk}");
                Console.WriteLine($"MinAgeDaysEvidence: {s.MinAgeDaysEvidence}");
                Console.WriteLine($"RetentionDaysLogs:  {s.RetentionDaysLogs}");
                Console.WriteLine($"RetentionDaysActivity: {s.RetentionDaysActivity}");
                Console.WriteLine($"RetentionDaysDeletionManifests: {s.RetentionDaysDeletionManifests}");
                Console.WriteLine($"ScrubSensitivePathsInReports: {s.ScrubSensitivePathsInReports}");
                Console.WriteLine($"ExcludedPaths:      {(s.ExcludedPaths.Count > 0 ? string.Join("; ", s.ExcludedPaths) : "(none)")}");
                Console.WriteLine($"CookieWhitelist:    {(s.CookieWhitelist.Count > 0 ? string.Join(", ", s.CookieWhitelist) : "(none — cookies will be cleaned)")}");
                Console.WriteLine($"ProgramNotes:       {(s.ProgramNotes.Count > 0 ? $"{s.ProgramNotes.Count} program(s)" : "(none)")}");
                return 0;
            }
            case "prune":
            {
                var dryRun = a.HasFlag("dry-run");
                var result = PrivacyMaintenance.Apply(AppSettings.Current, dryRun);
                if (a.HasFlag("json"))
                {
                    WriteJson(result);
                    return 0;
                }

                Console.WriteLine(dryRun ? "Privacy retention preview:" : "Privacy retention complete:");
                Console.WriteLine($"  Files deleted:       {result.FilesDeleted}");
                Console.WriteLine($"  Bytes deleted:       {FormatBytes(result.BytesDeleted)}");
                Console.WriteLine($"  Activity removed:    {result.ActivityEntriesDeleted}");
                Console.WriteLine($"  Files scrubbed:      {result.FilesScrubbed}");
                foreach (var detail in result.Details)
                    Console.WriteLine($"  - {detail}");
                return 0;
            }
            default:
                return Fail("usage: deeppurgecli settings [show|export <path>|import <path> [--preview]|prune [--dry-run]]");
        }
    }

    private static void PrintSettingsImportPlan(SettingsImportPlan plan)
    {
        Console.WriteLine($"Preview: {plan.Preview.ToRedactedSummary()}");
        if (plan.Issues.Count == 0)
        {
            Console.WriteLine("Validation: OK");
            return;
        }

        Console.WriteLine("Validation:");
        foreach (var issue in plan.Issues)
            Console.WriteLine($"  [{issue.Severity}] {issue.Field}: {issue.Message}");
    }

    private static int CmdOrphans(ParsedArgs a)
    {
        Console.Error.WriteLine("Scanning for orphaned artifacts...");

        var services = DeepPurge.Core.Services.ServiceScanner.GetAllServices(orphanedOnly: true);
        var tasks = DeepPurge.Core.Tasks.ScheduledTaskScanner.GetAllTasks().Where(t => t.IsOrphaned).ToList();
        var firewall = FirewallRuleScanner.GetAllRules(orphanedOnly: true);
        var paths = DeepPurge.Core.Shell.PathCleaner.ScanPathEntries(orphanedOnly: true);

        if (a.HasFlag("json"))
        {
            WriteJson(new {
                services = services.Select(s => new { s.Name, s.DisplayName, s.ImagePath }),
                tasks = tasks.Select(t => new { t.Name, t.Action }),
                firewall = firewall.Select(r => new { r.DisplayName, r.Program }),
                paths = paths.Select(p => new { p.Source, p.Directory }),
            });
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"=== Orphaned Services ({services.Count}) ===");
        foreach (var s in services)
            Console.WriteLine($"  {s.Name,-30} {s.DisplayName,-40} {s.ImagePath}");

        Console.WriteLine();
        Console.WriteLine($"=== Orphaned Scheduled Tasks ({tasks.Count}) ===");
        foreach (var t in tasks)
            Console.WriteLine($"  {t.Name,-40} {t.Action}");

        Console.WriteLine();
        Console.WriteLine($"=== Orphaned Firewall Rules ({firewall.Count}) ===");
        foreach (var r in firewall)
            Console.WriteLine($"  {r.DisplayName,-50} {r.Program}");

        Console.WriteLine();
        Console.WriteLine($"=== Orphaned PATH Entries ({paths.Count}) ===");
        foreach (var p in paths)
            Console.WriteLine($"  [{p.Source}] {p.Directory}");

        var total = services.Count + tasks.Count + firewall.Count + paths.Count;
        Console.WriteLine();
        Console.WriteLine($"# Total: {total} orphaned artifacts ({services.Count} services, " +
                         $"{tasks.Count} tasks, {firewall.Count} firewall, {paths.Count} PATH)");

        if (a.HasFlag("remnants"))
        {
            Console.WriteLine();
            Console.WriteLine("Scanning for remnants of uninstalled programs (signature DB)...");
            var installed = new HashSet<string>(
                InstalledProgramScanner.GetAllInstalledPrograms().Select(p => p.DisplayName),
                StringComparer.OrdinalIgnoreCase);
            var orphanResults = LeftoverSignatureDb.ScanForOrphans(installed);

            Console.WriteLine($"\n=== Program Remnants ({orphanResults.Count}) ===");
            foreach (var r in orphanResults)
            {
                Console.Write($"  {r.ProgramName,-35}");
                if (r.Match.FilePaths.Count > 0) Console.Write($" files:{r.Match.FilePaths.Count}");
                if (r.Match.RegistryPaths.Count > 0) Console.Write($" reg:{r.Match.RegistryPaths.Count}");
                Console.WriteLine();
                foreach (var f in r.Match.FilePaths) Console.WriteLine($"    F  {f}");
                foreach (var reg in r.Match.RegistryPaths) Console.WriteLine($"    R  {reg}");
            }
            total += orphanResults.Count;

            var bamRemnants = AmcacheParser.FindRemnants(installed);
            if (bamRemnants.Count > 0)
            {
                Console.WriteLine($"\n=== BAM Execution Remnants ({bamRemnants.Count}) ===");
                foreach (var b in bamRemnants)
                    Console.WriteLine($"  {b.Name,-35} {b.InstallPath}");
                total += bamRemnants.Count;
            }
        }

        return 0;
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

    private static async Task<int> CmdUpdateWinapp2Async(ParsedArgs a, CancellationToken ct)
    {
        var provenance = await Winapp2Updater.GetProvenanceAsync(ct);
        PrintWinapp2Provenance(provenance);

        if (a.HasFlag("check-only"))
        {
            if (provenance.Remote is null)
            {
                Console.WriteLine("Remote check failed.");
                return 1;
            }
            Console.WriteLine(provenance.IsStale ? "Update available." : "Up to date.");
            return provenance.IsStale ? 1 : 0;
        }

        if (provenance.Remote is null && provenance.LocalExists)
        {
            Console.WriteLine("Remote provenance unavailable; keeping the current local database.");
            return 1;
        }

        if (!provenance.IsStale)
        {
            Console.WriteLine("Already up to date.");
            return 0;
        }

        Console.Write("Downloading latest winapp2.ini... ");
        var result = await Winapp2Updater.UpdateDetailedAsync(ct);
        if (result.Success)
        {
            Console.WriteLine("done.");
            Console.WriteLine($"Downloaded: {result.Metadata!.ShortCommit} ({result.Metadata.CommitDateUtc:yyyy-MM-dd HH:mm UTC}), {FormatBytes(result.Metadata.ByteCount)}, sha256 {result.Metadata.ShortSha256}");
            Console.WriteLine($"Backup:     {(string.IsNullOrWhiteSpace(result.BackupPath) ? "(none)" : result.BackupPath)}");
            return 0;
        }
        Console.WriteLine($"failed: {result.ErrorMessage}");
        return 1;
    }

    private static void PrintWinapp2Provenance(Winapp2Provenance provenance)
    {
        if (!provenance.LocalExists)
        {
            Console.WriteLine("Local:  (not downloaded)");
        }
        else if (provenance.LocalMetadata is { } metadata)
        {
            Console.WriteLine($"Local:  {metadata.ShortCommit} ({metadata.CommitDateUtc:yyyy-MM-dd HH:mm UTC}), {FormatBytes(metadata.ByteCount)}, sha256 {metadata.ShortSha256}");
            Console.WriteLine($"File:   {provenance.LocalPath}");
            Console.WriteLine($"Backup: {(string.IsNullOrWhiteSpace(metadata.BackupPath) ? "(none)" : metadata.BackupPath)}");
        }
        else
        {
            var date = provenance.LocalWriteTimeUtc.HasValue
                ? provenance.LocalWriteTimeUtc.Value.ToString("yyyy-MM-dd HH:mm UTC")
                : "date unknown";
            Console.WriteLine($"Local:  file {date}, {FormatBytes(provenance.LocalByteCount ?? 0)}, sha256 {ShortHash(provenance.LocalSha256)} (metadata missing)");
            Console.WriteLine($"File:   {provenance.LocalPath}");
        }

        if (provenance.Remote is { } remote)
            Console.WriteLine($"Remote: {remote.ShortCommit} ({remote.CommitDateUtc:yyyy-MM-dd HH:mm UTC})");
        else
            Console.WriteLine($"Remote: (check failed{(string.IsNullOrWhiteSpace(provenance.RemoteError) ? "" : $": {provenance.RemoteError}")})");
    }

    private static int CmdRestore(ParsedArgs a)
    {
        var dateStr = a.GetOption("date");
        bool dryRun = a.HasFlag("dry-run");

        if (string.IsNullOrEmpty(dateStr))
        {
            var manifests = DpDiag.DeletionManifest.ListManifests();
            if (manifests.Count == 0) { Console.WriteLine("No deletion manifests found."); return 0; }
            Console.WriteLine($"{"Date",-12} {"Entries",8} {"Size",10}");
            foreach (var m in manifests)
                Console.WriteLine($"{m.Date:yyyy-MM-dd}  {m.EntryCount,8}  {FormatBytes(m.TotalBytes),10}");
            Console.WriteLine($"\n# {manifests.Count} manifests. Use --date YYYY-MM-DD to inspect or restore.");
            return 0;
        }

        if (!DateTime.TryParse(dateStr, out var date)) return Fail($"Bad date: {dateStr}");

        var entries = DpDiag.DeletionManifest.LoadManifest(date);
        if (entries.Count == 0) return Fail($"No manifest found for {date:yyyy-MM-dd}");

        Console.WriteLine($"Manifest for {date:yyyy-MM-dd}: {entries.Count} entries");

        if (a.HasFlag("list"))
        {
            var redact = AppSettings.Current.ScrubSensitivePathsInReports;
            foreach (var e in entries)
            {
                var path = redact ? PrivacyRedactor.RedactPaths(e.Path) : e.Path;
                Console.WriteLine($"  [{e.Type,-9}] {e.Operation,-15} {FormatBytes(e.SizeBytes),10}  {path}");
            }
            return 0;
        }

        var result = DpDiag.DeletionManifest.RestoreFromManifest(date, dryRun);
        foreach (var d in result.Details)
        {
            Console.WriteLine($"  {(AppSettings.Current.ScrubSensitivePathsInReports ? PrivacyRedactor.RedactPaths(d) : d)}");
        }
        Console.WriteLine();
        Console.WriteLine($"Registry restored: {result.RegistryRestored}");
        Console.WriteLine($"Files to check Recycle Bin: {result.FilesRecoverable}");
        Console.WriteLine($"Unrecoverable (secure-deleted or no backup): {result.Unrecoverable}");
        if (dryRun) Console.WriteLine("(dry-run — no changes made)");
        return 0;
    }

    private static int CmdNote(ParsedArgs a)
    {
        if (a.Positional.Count == 0) return Fail("usage: deeppurgecli note <program-name> [note text]\n       deeppurgecli note --clear <program-name>");
        var programName = a.Positional[0];

        if (a.HasFlag("clear"))
        {
            AppSettings.Current.ProgramNotes.Remove(programName);
            AppSettings.Current.Save();
            Console.WriteLine($"Note cleared for: {programName}");
            return 0;
        }

        if (a.Positional.Count < 2)
        {
            if (AppSettings.Current.ProgramNotes.TryGetValue(programName, out var existing))
                Console.WriteLine($"{programName}: {existing}");
            else
                Console.WriteLine($"No note for: {programName}");
            return 0;
        }

        var noteText = string.Join(" ", a.Positional.Skip(1));
        AppSettings.Current.ProgramNotes[programName] = noteText;
        AppSettings.Current.Save();
        Console.WriteLine($"Note saved for: {programName}");
        return 0;
    }

    private static int CmdSupportBundle(ParsedArgs a)
    {
        var output = a.GetOption("output") ?? a.Positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(output))
            return Fail("usage: deeppurgecli support-bundle --output <path.zip>");

        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            output += ".zip";

        var validated = ValidateExportPath(output);
        if (validated == null) return 2;

        Console.Write("Collecting diagnostic data... ");
        var result = DpDiag.SupportBundleExporter.Export(validated);
        if (!result.Success)
        {
            Console.WriteLine("failed.");
            Console.Error.WriteLine($"error: {result.ErrorMessage}");
            return 1;
        }

        Console.WriteLine("done.");
        Console.WriteLine($"Bundle: {result.OutputPath}");
        Console.WriteLine($"Sections: {result.SectionCount}, Size: {FormatBytes(result.ByteCount)}");
        Console.WriteLine("All user-profile paths have been redacted.");
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

    private static string ShortHash(string? hash)
        => string.IsNullOrWhiteSpace(hash) ? "unknown" : hash.Length <= 12 ? hash : hash[..12];

    private static string FormatBytes(long bytes) => DeepPurge.Core.Diagnostics.SizeFormatter.Format(bytes);

    private static ExportFormat ParseExportFormat(ParsedArgs a)
    {
        var fmt = a.GetOption("format")?.ToLowerInvariant();
        return fmt == "json" ? ExportFormat.Json : ExportFormat.Csv;
    }

    private static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

    private static string? ValidateExportPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var full = Path.GetFullPath(raw);
        var cwd = Path.GetFullPath(Environment.CurrentDirectory);
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (full.StartsWith(cwd, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return full;
        Console.Error.WriteLine($"Export path must be under the current directory or user profile: {raw}");
        return null;
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, _jsonOpts));

    private static bool IsHelp(string a) => a is "--help" or "-h" or "help" or "/?";

    private static void PrintHelp()
    {
        Console.WriteLine("DeepPurge CLI — headless system cleaner / uninstaller");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  version                                  Show build + data paths");
        Console.WriteLine("  portable [--enable]                      Query or toggle portable mode");
        Console.WriteLine("  list [--registry-only]                    List installed programs (TSV)");
        Console.WriteLine("  uninstall <name> [--silent] [--dry-run]  Uninstall a program or preview the native command");
        Console.WriteLine("  clean [junk|evidence ...] [--dry-run] [--secure] [--keep-cookies domain1,domain2]");
        Console.WriteLine("  repair <sfc|dism-scan|dism-restore|dism-cleanup|dism-resetbase|chkdsk|fontcache|iconcache>");
        Console.WriteLine("  drivers [--old] [--export file --format csv|json]");
        Console.WriteLine("  startup-impact [--export file --format csv|json]");
        Console.WriteLine("  shortcuts [--recycle] [--all] [--export file --format csv|json]");
        Console.WriteLine("  duplicates [roots...] [--export file --format csv|json]");
        Console.WriteLine("  snapshot trace <name> <installer> [--args \"...\"]");
        Console.WriteLine("  winapp2 <path.ini> [--dry-run]           Run community cleaner definitions");
        Console.WriteLine("  schedule list");
        Console.WriteLine("  schedule add --name N --time HH:MM [--freq daily|weekly|monthly] [--day Mon] [--args \"...\"]");
        Console.WriteLine("  schedule remove --name N");
        Console.WriteLine("  orphans                                  Scan for orphaned services, tasks, firewall rules, PATH entries");
        Console.WriteLine("  orphans --remnants                       Include BAM execution evidence in orphan scan");
        Console.WriteLine("  cleaners list|preview|run [--dry-run]    Manage validated custom JSON cleaner definitions");
        Console.WriteLine("  cleaners validate <file.cleaner.json>     Lint schema, risk, targets, and estimates");
        Console.WriteLine("  cleaners schema [--export file.json]      Print or export the cleaner JSON schema");
        Console.WriteLine("  register-shell                           Add 'Uninstall with DeepPurge' to .exe right-click menu");
        Console.WriteLine("  unregister-shell                         Remove the shell context menu entry");
        Console.WriteLine("  settings [show|export <path>|import <path> [--preview]|prune [--dry-run]]  View, move, validate, or prune settings");
        Console.WriteLine("  detection-script --program \"Name\" [--export file.ps1]   Generate Intune/SCCM detection script");
        Console.WriteLine("  update-winapp2 [--check-only]            Download latest winapp2.ini from GitHub");
        Console.WriteLine("  restore [--date YYYY-MM-DD] [--list] [--dry-run]  List or restore from deletion manifests");
        Console.WriteLine("  note <program> [text]                    Set/view per-program notes (--clear to remove)");
        Console.WriteLine("  support-bundle --output <path.zip>       Export redacted diagnostic bundle for bug reports");
        Console.WriteLine("  check-update                             Check GitHub for a newer release");
        Console.WriteLine("  doctor                                   Run environment self-test + report");
        Console.WriteLine();
        Console.WriteLine("Global flags:");
        Console.WriteLine("  --json                                   Output as JSON (list, drivers, startup-impact, orphans, shortcuts, snapshot, schedule, clean)");
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
        "name", "freq", "time", "day", "args", "export", "format", "program", "timeout",
        "keep-cookies", "date", "min-age", "path", "output",
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
