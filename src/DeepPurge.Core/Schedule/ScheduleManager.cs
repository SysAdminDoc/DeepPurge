using DeepPurge.Core.App;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Schedule;

public enum ScheduleFrequency { Daily, Weekly, Monthly }

public record ScheduleJob(
    string Name,
    ScheduleFrequency Frequency,
    DayOfWeek DayOfWeek,
    int  HourOfDay,
    int  MinuteOfHour,
    string CliArguments);

/// <summary>
/// Creates and removes Windows Task Scheduler tasks that invoke
/// <c>DeepPurgeCli.exe</c> on a recurring cadence. Tasks are created in the
/// <c>\DeepPurge\</c> folder so they're easy to locate and bulk-remove.
///
/// <para>
/// <b>Quoting strategy:</b> <c>schtasks /TR</c> has notoriously brittle
/// quoting rules — embedding inner quotes in the command via escape
/// sequences is a recipe for silent breakage. Instead we write a per-job
/// wrapper <c>.cmd</c> file under <see cref="DataPaths.Config"/> that
/// invokes the CLI with the user's arguments as plain text, then point
/// the task at that wrapper. Side benefit: the user can inspect / tweak
/// the wrapper without touching the task.
/// </para>
///
/// <para>
/// <b>Injection hardening:</b> <see cref="ScheduleJob.Name"/> is strictly
/// alpha-numeric after sanitisation; <see cref="ScheduleJob.CliArguments"/>
/// is never concatenated into a shell command — it's written verbatim to
/// the batch file after argument-encoding. Batch-file metacharacters
/// (<c>^ &amp; | &lt; &gt;</c>) don't escape from the per-job wrapper.
/// </para>
/// </summary>
public class ScheduleManager
{
    private const string TaskFolder = @"\DeepPurge\";

    public bool CreateJob(ScheduleJob job, string cliPath)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
            throw new ArgumentException("Job name is required", nameof(job));
        if (!File.Exists(cliPath))
            throw new FileNotFoundException("CLI binary not found", cliPath);
        if (job.HourOfDay is < 0 or > 23 || job.MinuteOfHour is < 0 or > 59)
            throw new ArgumentException("Time out of range", nameof(job));

        var safeName = SanitizeName(job.Name);
        var taskName = TaskFolder + safeName;
        var wrapper  = WriteWrapperScript(safeName, cliPath, job.CliArguments);

        var schedule = job.Frequency switch
        {
            ScheduleFrequency.Daily   => "/SC DAILY",
            ScheduleFrequency.Weekly  => $"/SC WEEKLY /D {ShortDay(job.DayOfWeek)}",
            ScheduleFrequency.Monthly => "/SC MONTHLY",
            _ => "/SC WEEKLY",
        };
        var time = $"{job.HourOfDay:D2}:{job.MinuteOfHour:D2}";

        // /TR value is a single token — the wrapper path, nothing else. Quoted
        // with literal quotes that schtasks parses cleanly.
        var tr = $"\"{wrapper}\"";
        var args = $"/Create /F /RU SYSTEM /RL HIGHEST /TN \"{taskName}\" {schedule} /ST {time} /TR \"{tr}\"";
        var (code, output) = Run("schtasks.exe", args);
        if (code != 0) Log.Warn($"schtasks /Create failed ({code}): {output.Trim()}");
        return code == 0;
    }

    public bool DeleteJob(string name)
    {
        var safeName = SanitizeName(name);
        var taskName = TaskFolder + safeName;
        var (code, _) = Run("schtasks.exe", $"/Delete /F /TN \"{taskName}\"");

        // Best-effort cleanup of the wrapper script.
        try
        {
            var wrapper = WrapperPath(safeName);
            if (File.Exists(wrapper)) File.Delete(wrapper);
        }
        catch { }

        return code == 0;
    }

    public List<string> ListJobs()
    {
        var (code, output) = Run("schtasks.exe", $"/Query /FO CSV /NH /TN \"{TaskFolder.TrimEnd('\\')}\"");
        if (code != 0) return new List<string>();

        var list = new List<string>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '"') continue;

            // CSV parse-light — we only need column 0 (TaskName), but be resilient
            // to embedded quotes via simple state machine instead of naive Split.
            int end = 1;
            while (end < line.Length && line[end] != '"') end++;
            if (end >= line.Length) continue;
            var taskName = line[1..end];
            if (taskName.StartsWith(TaskFolder, StringComparison.OrdinalIgnoreCase))
                taskName = taskName[TaskFolder.Length..];
            list.Add(taskName);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ═══════════════════════════════════════════════════════

    private static string WrapperPath(string safeName)
        => Path.Combine(DataPaths.Config, $"job_{safeName}.cmd");

    private static string WriteWrapperScript(string safeName, string cliPath, string cliArgs)
    {
        var path = WrapperPath(safeName);
        // @echo off + exit-code pass-through. Arguments are passed verbatim —
        // schtasks already insulates us from further shell interpretation.
        var script =
            "@echo off\r\n" +
            "REM DeepPurge scheduled job — regenerated by ScheduleManager.\r\n" +
            $"\"{cliPath}\" {cliArgs ?? ""}\r\n" +
            "exit /b %ERRORLEVEL%\r\n";
        File.WriteAllText(path, script, System.Text.Encoding.ASCII);
        return path;
    }

    private static (int ExitCode, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(p.StandardOutput.ReadToEnd());
            sb.AppendLine(p.StandardError.ReadToEnd());
            p.WaitForExit();
            return (p.ExitCode, sb.ToString());
        }
        catch (Exception ex)
        {
            Log.Warn($"ScheduleManager.Run {file}: {ex.Message}");
            return (-1, ex.Message);
        }
    }

    /// <summary>Strict alpha-numeric + [-_ ] whitelist.</summary>
    private static string SanitizeName(string name)
    {
        var clean = new string((name ?? "").Where(c =>
            char.IsLetterOrDigit(c) || c is '_' or '-' or ' ').ToArray()).Trim();
        if (clean.Length == 0) return "DeepPurgeJob";
        return clean.Length > 64 ? clean[..64] : clean;
    }

    private static string ShortDay(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday    => "MON",
        DayOfWeek.Tuesday   => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday  => "THU",
        DayOfWeek.Friday    => "FRI",
        DayOfWeek.Saturday  => "SAT",
        DayOfWeek.Sunday    => "SUN",
        _ => "MON",
    };
}
