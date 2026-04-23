using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Repair;

public enum RepairOperation
{
    SfcScan,                 // sfc /scannow
    DismScanHealth,          // DISM /Online /Cleanup-Image /ScanHealth
    DismRestoreHealth,       // DISM /Online /Cleanup-Image /RestoreHealth
    DismComponentCleanup,    // DISM /Online /Cleanup-Image /StartComponentCleanup
    DismResetBase,           // DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase
    ChkDsk,                  // chkdsk C: /scan
    RebuildFontCache,        // stop FontCache + delete cache files
    RebuildIconCache,        // kill explorer + delete IconCache.db
    WingetRepair,            // winget repair <id>
    MsiRepair,               // msiexec /fa {ProductCode} /qn
}

public class RepairResult
{
    public RepairOperation Operation { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
    public bool Success => ExitCode == 0;
}

/// <summary>
/// One-stop Windows integrity-repair orchestrator.
///
/// Everything here wraps Microsoft-supplied tools (sfc, DISM, chkdsk,
/// msiexec, winget). No proprietary logic — just the canonical sequence
/// sysadmins type from memory, bound to live stdout streaming so the UI /
/// CLI can surface progress.
///
/// <para>
/// Encoding note: <c>sfc.exe</c> and <c>chkdsk.exe</c> write UTF-16 LE to
/// their pipes on older Windows builds. Forcing UTF-8 decodes the bytes
/// wrong (NUL interleaved). We leave the encoding unset so .NET uses the
/// OEM console CP, which is the right default for these tools.
/// </para>
///
/// <para>
/// Font/icon cache rebuild used to shell-out broad <c>del /s</c> patterns;
/// now deletes a known list of specific files to eliminate blast-radius
/// surprises if a user places a <c>FontCache-something.dat</c> under an
/// unrelated app's profile.
/// </para>
/// </summary>
public class WindowsRepairEngine
{
    public async Task<RepairResult> RunAsync(
        RepairOperation op,
        IProgress<string>? log = null,
        string? argExtra = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new RepairResult { Operation = op };

        // The cache-rebuild ops are multi-step; run them via our helper which
        // logs each sub-step rather than spawning one gigantic cmd line.
        if (op is RepairOperation.RebuildFontCache or RepairOperation.RebuildIconCache)
        {
            var buffer = new System.Text.StringBuilder();
            IProgress<string> combined = new Progress<string>(line =>
            {
                buffer.AppendLine(line);
                log?.Report(line);
            });
            result.ExitCode = op == RepairOperation.RebuildFontCache
                ? await RebuildFontCacheAsync(combined, ct)
                : await RebuildIconCacheAsync(combined, ct);
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            result.Output = buffer.ToString();
            return result;
        }

        var (file, args) = ResolveCommand(op, argExtra);
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow  = true,
            // Do NOT force UTF-8 — sfc/chkdsk emit UTF-16/OEM depending on build.
        };

        Process? p = null;
        var buf = new System.Text.StringBuilder();
        try
        {
            p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) { buf.AppendLine(e.Data); log?.Report(e.Data); } };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) { buf.AppendLine(e.Data); log?.Report(e.Data); } };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            result.ExitCode = p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            result.ExitCode = -1;
            log?.Report("[cancelled]");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Typical cause: the binary isn't on PATH (winget missing, etc).
            buf.AppendLine($"[error] Could not launch '{file}': {ex.Message}");
            log?.Report(buf.ToString());
            result.ExitCode = -2;
            Log.Warn($"WindowsRepairEngine: {ex.Message}");
        }
        catch (Exception ex)
        {
            buf.AppendLine($"[error] {ex.GetType().Name}: {ex.Message}");
            result.ExitCode = -3;
            Log.Error("WindowsRepairEngine unexpected", ex);
        }
        finally
        {
            p?.Dispose();
        }

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        result.Output  = buf.ToString();
        return result;
    }

    private static (string File, string Args) ResolveCommand(RepairOperation op, string? extra) => op switch
    {
        RepairOperation.SfcScan              => ("sfc.exe", "/scannow"),
        RepairOperation.DismScanHealth       => ("DISM.exe", "/Online /Cleanup-Image /ScanHealth"),
        RepairOperation.DismRestoreHealth    => ("DISM.exe", "/Online /Cleanup-Image /RestoreHealth"),
        RepairOperation.DismComponentCleanup => ("DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup"),
        RepairOperation.DismResetBase        => ("DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"),
        RepairOperation.ChkDsk               => ("chkdsk.exe", "C: /scan"),
        RepairOperation.WingetRepair         => ("winget.exe", $"repair {SanitizeToken(extra)} --silent"),
        RepairOperation.MsiRepair            => ("msiexec.exe", $"/fa {SanitizeProductCode(extra)} /qn"),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    // ═══════════════════════════════════════════════════════
    //  CACHE REBUILD — narrow, specific, explain-what-we're-doing
    // ═══════════════════════════════════════════════════════

    private static async Task<int> RebuildFontCacheAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Stopping FontCache services...");
        await RunShort(log, "net.exe", "stop FontCache", ct);
        await RunShort(log, "net.exe", "stop FontCache3.0.0.0", ct);

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cachePaths = new[]
        {
            Path.Combine(windir, "ServiceProfiles", "LocalService", "AppData", "Local"),
            Path.Combine(windir, "System32"),
        };

        int removed = 0;
        foreach (var dir in cachePaths)
        {
            if (!Directory.Exists(dir)) continue;
            // Enumerate only the top-level entries matching known cache filenames.
            // NO /s recursion — we don't touch user profiles or arbitrary sub-dirs.
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.StartsWith("FontCache",   StringComparison.OrdinalIgnoreCase) ||
                    name.Equals  ("FNTCACHE.DAT", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); removed++; log.Report($"deleted {name}"); }
                    catch (Exception ex) { log.Report($"skip {name}: {ex.Message}"); }
                }
            }
        }

        log.Report($"Removed {removed} cache file(s). Restarting FontCache...");
        await RunShort(log, "net.exe", "start FontCache", ct);
        return 0;
    }

    private static async Task<int> RebuildIconCacheAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Stopping Explorer...");
        await RunShort(log, "taskkill.exe", "/f /im explorer.exe", ct);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var explorerDir = Path.Combine(local, "Microsoft", "Windows", "Explorer");

        var victims = new List<string>();
        try
        {
            if (Directory.Exists(explorerDir))
            {
                victims.AddRange(Directory.EnumerateFiles(explorerDir, "iconcache_*.db"));
                victims.AddRange(Directory.EnumerateFiles(explorerDir, "thumbcache_*.db"));
            }
            var legacyIcon = Path.Combine(local, "IconCache.db");
            if (File.Exists(legacyIcon)) victims.Add(legacyIcon);
        }
        catch (Exception ex) { log.Report($"enumerate: {ex.Message}"); }

        int removed = 0;
        foreach (var v in victims)
        {
            try { File.Delete(v); removed++; log.Report($"deleted {Path.GetFileName(v)}"); }
            catch (Exception ex) { log.Report($"skip {Path.GetFileName(v)}: {ex.Message}"); }
        }

        log.Report($"Removed {removed} cache file(s). Restarting Explorer...");
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); }
        catch (Exception ex) { log.Report($"restart explorer: {ex.Message}"); return 1; }
        return 0;
    }

    private static async Task RunShort(IProgress<string> log, string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string outStr = await p.StandardOutput.ReadToEndAsync(ct);
            string errStr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var combined = (outStr + errStr).Trim();
            if (!string.IsNullOrEmpty(combined)) log.Report(combined);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.Report($"{exe}: {ex.Message}"); }
    }

    // Winget package IDs allow letters/digits/dot/dash/underscore. Reject everything else.
    private static string SanitizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "\"\"";
        var clean = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return clean.Length == 0 ? "\"\"" : clean;
    }

    // {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
    private static string SanitizeProductCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "\"\"";
        var m = System.Text.RegularExpressions.Regex.Match(
            raw, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        return m.Success ? m.Value : "\"\"";
    }
}
