using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepPurge.Core.Execution;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Tasks;

public class ScheduledTaskInfo : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Action { get; set; } = "";
    public string State { get; set; } = "";
    public string LastRunTime { get; set; } = "";
    public string NextRunTime { get; set; } = "";
    public bool IsOrphaned { get; set; }
    public string Status => IsOrphaned ? "Orphaned" : State;
    public bool IsProtected => !SafetyGuard.IsTaskSafeToDelete(Path);
    public bool MutationSupported => !string.IsNullOrWhiteSpace(Name) && !IsProtected;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ScheduledTaskScanner
{
    public static List<ScheduledTaskInfo> GetAllTasks()
    {
        var tasks = new List<ScheduledTaskInfo>();
        try
        {
            var result = ExternalProcessRunner.Run(PowerShellCommand(
                "Get-ScheduledTask | " +
                "Select-Object TaskName,TaskPath,Author,Description,State," +
                "@{N='Action';E={($_.Actions | Select-Object -First 1).Execute}}," +
                "@{N='LastRun';E={(Get-ScheduledTaskInfo -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue).LastRunTime}}," +
                "@{N='NextRun';E={(Get-ScheduledTaskInfo -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue).NextRunTime}} | " +
                "ConvertTo-Json -Depth 2 -Compress",
                TimeSpan.FromSeconds(30)));
            var output = result.Output;
            if (string.IsNullOrWhiteSpace(output)) return tasks;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    tasks.Add(ParseTask(el));
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                tasks.Add(ParseTask(root));
            }
        }
        catch { /* PowerShell unavailable / parse failure - return whatever we have */ }

        foreach (var task in tasks)
        {
            if (string.IsNullOrEmpty(task.Action)) continue;
            var exePath = ExtractPath(task.Action);
            if (!string.IsNullOrEmpty(exePath) && !IsSystemPath(exePath) && !File.Exists(exePath))
                task.IsOrphaned = true;
        }

        return tasks.OrderByDescending(t => t.IsOrphaned).ThenBy(t => t.Name).ToList();
    }

    public static bool DisableTask(ScheduledTaskInfo task)
        => DisableTaskDetailed(task).Succeeded;

    public static AdministrativeMutationResult DisableTaskDetailed(
        ScheduledTaskInfo task,
        bool dryRun = false)
        => ChangeTaskState(task, enabled: false, dryRun);

    public static bool EnableTask(ScheduledTaskInfo task)
        => EnableTaskDetailed(task).Succeeded;

    public static AdministrativeMutationResult EnableTaskDetailed(
        ScheduledTaskInfo task,
        bool dryRun = false)
        => ChangeTaskState(task, enabled: true, dryRun);

    public static bool DeleteTask(ScheduledTaskInfo task)
        => DeleteTaskDetailed(task).Succeeded;

    public static AdministrativeMutationResult DeleteTaskDetailed(
        ScheduledTaskInfo task,
        bool dryRun = false)
    {
        const string operation = "scheduled-task-delete";
        var target = TaskTarget(task);
        var validation = ValidateTask(task, operation, target);
        if (validation != null) return validation;

        var before = JsonSerializer.Serialize(new
        {
            task.Name,
            task.Path,
            task.State,
            task.Action,
            task.Author,
        });

        var export = RunPsCommandResult(
            $"Export-ScheduledTask -TaskName '{EscapePs(task.Name)}' " +
            $"-TaskPath '{EscapePs(task.Path)}' -ErrorAction Stop");
        if (!export.Success || string.IsNullOrWhiteSpace(export.Output))
            return AdministrativeMutationPolicy.Unsupported(
                operation,
                target,
                "The task definition could not be exported for rollback; deletion is disabled.");

        var rollback = JsonSerializer.Serialize(new
        {
            task.Name,
            task.Path,
            TaskXml = export.Output,
            Restore = $"Register-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -Xml <exported task XML> -Force",
        });

        if (dryRun)
            return AdministrativeMutationPolicy.Preview(
                operation,
                target,
                before,
                "Absent (planned)",
                rollback);

        var command =
            $"Unregister-ScheduledTask -TaskName '{EscapePs(task.Name)}' " +
            $"-TaskPath '{EscapePs(task.Path)}' -Confirm:$false -ErrorAction Stop; " +
            $"if (Get-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -ErrorAction SilentlyContinue) " +
            "{ throw 'The scheduled task still exists after removal.' }";
        var result = RunPsCommandResult(command);
        if (!result.Success)
            return AdministrativeMutationPolicy.Failed(
                operation,
                target,
                before,
                result.CombinedOutput,
                rollback);

        SystemRefreshNotifier.NotifyShellChanged();
        return AdministrativeMutationPolicy.Changed(
            operation,
            target,
            before,
            "Absent",
            rollback);
    }

    private static AdministrativeMutationResult ChangeTaskState(
        ScheduledTaskInfo task,
        bool enabled,
        bool dryRun)
    {
        var operation = enabled ? "scheduled-task-enable" : "scheduled-task-disable";
        var target = TaskTarget(task);
        var validation = ValidateTask(task, operation, target);
        if (validation != null) return validation;

        var before = JsonSerializer.Serialize(new
        {
            task.Name,
            task.Path,
            task.State,
        });
        var expected = enabled ? "Ready" : "Disabled";
        var command = enabled
            ? $"Enable-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -ErrorAction Stop"
            : $"Disable-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -ErrorAction Stop";
        var verify =
            $"; if ((Get-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -ErrorAction Stop).State.ToString() -ne '{expected}') " +
            $"{{ throw 'The scheduled task state was not verified as {expected}.' }}";
        var rollback = enabled
            ? $"Disable-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}'"
            : $"Enable-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}'";

        if (dryRun)
            return AdministrativeMutationPolicy.Preview(
                operation,
                target,
                before,
                expected,
                rollback);

        var result = RunPsCommandResult(command + verify);
        if (!result.Success)
            return AdministrativeMutationPolicy.Failed(
                operation,
                target,
                before,
                result.CombinedOutput,
                rollback);

        task.State = expected;
        SystemRefreshNotifier.NotifyShellChanged();
        return AdministrativeMutationPolicy.Changed(
            operation,
            target,
            before,
            expected,
            rollback);
    }

    private static AdministrativeMutationResult? ValidateTask(
        ScheduledTaskInfo task,
        string operation,
        string target)
    {
        if (string.IsNullOrWhiteSpace(task.Name))
            return AdministrativeMutationPolicy.Unsupported(
                operation,
                target,
                "The scheduled task has no stable task name.");
        if (!SafetyGuard.IsTaskSafeToDelete(task.Path))
            return AdministrativeMutationPolicy.Skipped(
                operation,
                target,
                "Present",
                "Protected Windows scheduled task path.");
        return null;
    }

    private static string TaskTarget(ScheduledTaskInfo task)
        => string.IsNullOrWhiteSpace(task.Path)
            ? task.Name
            : $"{task.Path.TrimEnd('\\')}\\{task.Name}";

    // ═══════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════

    private static bool RunPsCommand(string command)
        => RunPsCommandResult(command).Success;

    private static ExternalProcessResult RunPsCommandResult(string command)
    {
        try
        {
            return ExternalProcessRunner.Run(PowerShellCommand(command, TimeSpan.FromSeconds(15)));
        }
        catch (Exception ex)
        {
            return new ExternalProcessResult(
                PowerShellCommand(command, TimeSpan.FromSeconds(15)),
                -1,
                "",
                ex.Message,
                Started: false,
                TimedOut: false,
                Canceled: false,
                StartError: ex.Message);
        }
    }

    private static ExternalProcessCommand PowerShellCommand(string script, TimeSpan timeout)
        => new("powershell.exe")
        {
            Arguments = new[] { "-NoProfile", "-Command", script },
            Timeout = timeout,
            OutputLimitChars = 512 * 1024,
            ErrorLimitChars = 64 * 1024,
            RedactAbsolutePaths = true,
        };

    private static string EscapePs(string s) => string.IsNullOrEmpty(s)
        ? ""
        : s.Replace("\0", "").Replace("'", "''");

    private static ScheduledTaskInfo ParseTask(JsonElement el) => new()
    {
        Name = GetStr(el, "TaskName"),
        Path = GetStr(el, "TaskPath"),
        Author = GetStr(el, "Author"),
        Description = GetStr(el, "Description"),
        Action = GetStr(el, "Action"),
        State = GetStr(el, "State"),
        LastRunTime = GetDateTime(el, "LastRun"),
        NextRunTime = GetDateTime(el, "NextRun"),
    };

    private static string GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return "";
        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString() ?? "",
            JsonValueKind.Number => val.ToString(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null => "",
            JsonValueKind.Undefined => "",
            _ => "",
        };
    }

    /// <summary>
    /// ConvertTo-Json serializes DateTime as either an ISO string or
    /// { "value": "...", "DateTime": "..." } depending on PS version.
    /// Handle both shapes so LastRun/NextRun display instead of empty.
    /// </summary>
    private static string GetDateTime(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return "";
        if (val.ValueKind == JsonValueKind.String && DateTime.TryParse(val.GetString(), out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm");
        if (val.ValueKind == JsonValueKind.Object)
        {
            if (val.TryGetProperty("DateTime", out var niceStr) && niceStr.ValueKind == JsonValueKind.String)
                return niceStr.GetString() ?? "";
            if (val.TryGetProperty("value", out var rawStr) &&
                rawStr.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(rawStr.GetString(), out var dt2))
                return dt2.ToString("yyyy-MM-dd HH:mm");
        }
        return "";
    }

    private static string ExtractPath(string action)
    {
        action = action.Trim();
        if (action.StartsWith('"'))
        {
            var end = action.IndexOf('"', 1);
            return end > 0 ? action[1..end] : action;
        }
        var space = action.IndexOf(' ');
        var raw = space > 0 ? action[..space] : action;
        try { return Environment.ExpandEnvironmentVariables(raw); }
        catch { return raw; }
    }

    private static bool IsSystemPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var lower = path.ToLowerInvariant();
        return lower.Contains(@"\windows\system32") ||
               lower.Contains(@"\windows\syswow64") ||
               lower.Contains(@"\windows\microsoft.net") ||
               lower.StartsWith(@"\systemroot\", StringComparison.OrdinalIgnoreCase) ||
               lower.StartsWith("cmd", StringComparison.OrdinalIgnoreCase) ||
               lower.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
               lower.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase) ||
               lower.StartsWith("schtasks", StringComparison.OrdinalIgnoreCase);
    }
}
