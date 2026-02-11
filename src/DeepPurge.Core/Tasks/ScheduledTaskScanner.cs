using System.Diagnostics;
using System.Text.Json;

namespace DeepPurge.Core.Tasks;

public class ScheduledTaskInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Action { get; set; } = "";
    public string State { get; set; } = "";
    public string LastRunTime { get; set; } = "";
    public string NextRunTime { get; set; } = "";
    public bool IsOrphaned { get; set; }
    public bool IsSelected { get; set; }
    public string Status => IsOrphaned ? "Orphaned" : State;
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
                Arguments = "-NoProfile -Command \"Get-ScheduledTask | Select-Object TaskName,TaskPath,Author,Description,State,@{N='Action';E={($_.Actions | Select-Object -First 1).Execute}},@{N='LastRun';E={(Get-ScheduledTaskInfo $_.TaskName -ErrorAction SilentlyContinue).LastRunTime}},@{N='NextRun';E={(Get-ScheduledTaskInfo $_.TaskName -ErrorAction SilentlyContinue).NextRunTime}} | ConvertTo-Json -Depth 2\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return tasks;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
            if (string.IsNullOrWhiteSpace(output)) return tasks;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() :
                new[] { root }.AsEnumerable().GetEnumerator() as IEnumerator<JsonElement> != null ?
                Enumerable.Repeat(root, 1) : Enumerable.Empty<JsonElement>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    tasks.Add(ParseTask(el));
            }
            else
            {
                tasks.Add(ParseTask(root));
            }
        }
        catch { }

        // Check for orphaned tasks
        foreach (var task in tasks)
        {
            if (!string.IsNullOrEmpty(task.Action))
            {
                var exePath = ExtractPath(task.Action);
                if (!string.IsNullOrEmpty(exePath) && !IsSystemPath(exePath) && !File.Exists(exePath))
                    task.IsOrphaned = true;
            }
        }

        return tasks.OrderByDescending(t => t.IsOrphaned).ThenBy(t => t.Name).ToList();
    }

    public static bool DisableTask(ScheduledTaskInfo task)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Disable-ScheduledTask -TaskName '{task.Name.Replace("'", "''")}' -TaskPath '{task.Path.Replace("'", "''")}'\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool DeleteTask(ScheduledTaskInfo task)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Unregister-ScheduledTask -TaskName '{task.Name.Replace("'", "''")}' -TaskPath '{task.Path.Replace("'", "''")}' -Confirm:$false\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static ScheduledTaskInfo ParseTask(JsonElement el)
    {
        return new ScheduledTaskInfo
        {
            Name = GetStr(el, "TaskName"),
            Path = GetStr(el, "TaskPath"),
            Author = GetStr(el, "Author"),
            Description = GetStr(el, "Description"),
            Action = GetStr(el, "Action"),
            State = GetStr(el, "State"),
            LastRunTime = GetStr(el, "LastRun"),
            NextRunTime = GetStr(el, "NextRun"),
        };
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? "";
            if (val.ValueKind == JsonValueKind.Number) return val.ToString();
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
        return space > 0 ? action[..space] : action;
    }

    private static bool IsSystemPath(string path) =>
        path.Contains(@"C:\Windows\system32", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"C:\Windows\SysWOW64", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"C:\Windows\Microsoft.NET", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("cmd", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("powershell", StringComparison.OrdinalIgnoreCase);
}
