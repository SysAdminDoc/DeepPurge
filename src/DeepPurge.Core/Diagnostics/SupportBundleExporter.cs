using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DeepPurge.Core.App;
using DeepPurge.Core.Packages;
using DeepPurge.Core.Security;

namespace DeepPurge.Core.Diagnostics;

public sealed record SupportBundleResult(
    bool Success,
    string OutputPath,
    int SectionCount,
    long ByteCount,
    string? ErrorMessage = null);

public static class SupportBundleExporter
{
    public static SupportBundleResult Export(string outputPath)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DeepPurge-bundle-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            try
            {
                int sections = 0;

                sections += WriteDoctorResults(tempDir);
                sections += WriteAppSummary(tempDir);
                sections += WritePackageSourceHealth(tempDir);
                sections += WriteRecentActivity(tempDir);
                sections += WriteRecentLogs(tempDir);
                sections += WriteExecutableTrust(tempDir);

                FinalRedactionPass(tempDir);

                if (File.Exists(outputPath))
                    File.Delete(outputPath);

                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                ZipFile.CreateFromDirectory(tempDir, outputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                var size = new FileInfo(outputPath).Length;

                return new SupportBundleResult(true, outputPath, sections, size);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error("SupportBundleExporter.Export", ex);
            return new SupportBundleResult(false, outputPath, 0, 0, ex.Message);
        }
    }

    private static int WriteDoctorResults(string dir)
    {
        var results = SelfTest.RunAll();
        var lines = new List<string> { "DeepPurge Doctor Results", new('=', 40), "" };
        int fails = 0, warns = 0;

        foreach (var r in results)
        {
            var tag = r.Status switch
            {
                SelfTestStatus.Ok => "[ OK ]",
                SelfTestStatus.Warn => "[WARN]",
                SelfTestStatus.Fail => "[FAIL]",
                _ => "[skip]",
            };
            if (r.Status == SelfTestStatus.Fail) fails++;
            else if (r.Status == SelfTestStatus.Warn) warns++;

            lines.Add($"{tag} {r.Check,-20} {Redact(r.Detail)}");
            if (!string.IsNullOrWhiteSpace(r.Hint) && r.Status != SelfTestStatus.Ok)
                lines.Add($"       -> {Redact(r.Hint)}");
        }

        lines.Add("");
        lines.Add($"Summary: {results.Count - fails - warns} ok, {warns} warn, {fails} fail");

        File.WriteAllLines(Path.Combine(dir, "doctor.txt"), lines);
        return 1;
    }

    private static int WriteAppSummary(string dir)
    {
        var asm = typeof(SupportBundleExporter).Assembly.GetName();
        var version = (asm.Version ?? new Version(0, 9, 0)).ToString(3);

        var lines = new List<string>
        {
            "DeepPurge Application Summary",
            new('=', 40),
            "",
            $"Version:       v{version}",
            $"Mode:          {(DataPaths.IsPortable ? "Portable" : "Installed")}",
            $"Data root:     {Redact(DataPaths.Root)}",
            $"Logs:          {Redact(DataPaths.Logs)}",
            $"Backups:       {Redact(DataPaths.Backups)}",
            $"Snapshots:     {Redact(DataPaths.Snapshots)}",
            $"Cleaners:      {Redact(DataPaths.Cleaners)}",
            $"Config:        {Redact(DataPaths.Config)}",
            "",
            $"OS:            {Environment.OSVersion}",
            $"CLR:           {Environment.Version}",
            $"64-bit OS:     {Environment.Is64BitOperatingSystem}",
            $"64-bit proc:   {Environment.Is64BitProcess}",
            $"Machine:       {Environment.MachineName}",
        };

        File.WriteAllLines(Path.Combine(dir, "app-summary.txt"), lines);
        return 1;
    }

    private static int WritePackageSourceHealth(string dir)
    {
        try
        {
            var health = PackageManagerScanner.GetSourceHealth();
            var lines = new List<string> { "Package Source Health", new('=', 40), "" };

            foreach (var h in health)
            {
                var tag = h.Status switch
                {
                    SelfTestStatus.Ok => "[ OK ]",
                    SelfTestStatus.Warn => "[WARN]",
                    SelfTestStatus.Fail => "[FAIL]",
                    _ => "[skip]",
                };
                var ver = string.IsNullOrWhiteSpace(h.Version) ? "" : $" v{h.Version}";
                lines.Add($"{tag} {h.Source,-12}{ver} — {h.Detail}; {h.LastScannerStatus}");
                if (!string.IsNullOrWhiteSpace(h.Hint))
                    lines.Add($"       -> {Redact(h.Hint)}");
            }

            File.WriteAllLines(Path.Combine(dir, "package-health.txt"), lines);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"SupportBundle: package health failed: {ex.Message}");
            return 0;
        }
    }

    private static int WriteRecentActivity(string dir)
    {
        try
        {
            var entries = ActivityLog.LoadRecent(50);
            if (entries.Count == 0) return 0;

            var redacted = entries.Select(e => e with { Summary = Redact(e.Summary) });
            var json = JsonSerializer.Serialize(redacted, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, "recent-activity.json"), json);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"SupportBundle: activity log failed: {ex.Message}");
            return 0;
        }
    }

    private static int WriteRecentLogs(string dir)
    {
        try
        {
            var logsDir = DataPaths.Logs;
            if (!Directory.Exists(logsDir)) return 0;

            int written = 0;
            foreach (var logFile in Directory.EnumerateFiles(logsDir, "deeppurge.log*").Take(3))
            {
                var content = File.ReadAllText(logFile);
                var redacted = PrivacyRedactor.RedactPaths(content);
                File.WriteAllText(
                    Path.Combine(dir, $"log-{Path.GetFileName(logFile)}"),
                    redacted);
                written++;
            }

            return written > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"SupportBundle: log copy failed: {ex.Message}");
            return 0;
        }
    }

    private static int WriteExecutableTrust(string dir)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return 0;

            var lines = new List<string> { "Executable Trust Facts", new('=', 40), "" };
            lines.Add($"Path: {Redact(exePath)}");

            try
            {
                using var stream = File.OpenRead(exePath);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                lines.Add($"SHA256: {hash}");
            }
            catch (Exception ex)
            {
                lines.Add($"SHA256: unavailable ({ex.Message})");
            }

            try
            {
                var sig = DigitalSignatureInspector.Inspect(exePath);
                lines.Add($"Signature: {sig.Status}");
                if (!string.IsNullOrWhiteSpace(sig.Subject))
                    lines.Add($"Subject: {sig.Subject}");
            }
            catch (Exception ex)
            {
                lines.Add($"Signature: unavailable ({ex.Message})");
            }

            File.WriteAllLines(Path.Combine(dir, "exe-trust.txt"), lines);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"SupportBundle: exe trust failed: {ex.Message}");
            return 0;
        }
    }

    private static void FinalRedactionPass(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try
            {
                var content = File.ReadAllText(file);
                var redacted = PrivacyRedactor.RedactPaths(content);
                if (redacted != content)
                    File.WriteAllText(file, redacted, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }

    private static string Redact(string value) => PrivacyRedactor.RedactPaths(value);
}
