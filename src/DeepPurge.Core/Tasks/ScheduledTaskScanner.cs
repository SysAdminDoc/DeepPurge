using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-ScheduledTask | " +
                    "Select-Object TaskName,TaskPath,Author,Description,State," +
                    "@{N='Action';E={($_.Actions | Select-Object -First 1).Execute}}," +
                    "@{N='LastRun';E={(Get-ScheduledTaskInfo -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue).LastRunTime}}," +
                    "@{N='NextRun';E={(Get-ScheduledTaskInfo -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue).NextRunTime}} | " +
                    "ConvertTo-Json -Depth 2 -Compress\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return tasks;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
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

    public static bool DisableTask(ScheduledTaskInfo task) => RunPsCommand(
        $"Disable-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -ErrorAction Stop");

    public static bool EnableTask(ScheduledTaskInfo task) => RunPsCommand(
        $"Enable-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -ErrorAction Stop");

    public static bool DeleteTask(ScheduledTaskInfo task) => RunPsCommand(
        $"Unregister-ScheduledTask -TaskName '{EscapePs(task.Name)}' -TaskPath '{EscapePs(task.Path)}' -Confirm:$false -ErrorAction Stop");

    // ═══════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════

    private static bool RunPsCommand(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string EscapePs(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("'", "''");

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
