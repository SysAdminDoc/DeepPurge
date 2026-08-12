using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepPurge.Core.Execution;
using DeepPurge.Core.Safety;

namespace DeepPurge.Core.Firewall;

public class FirewallRuleEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Action { get; set; } = "";
    public string Program { get; set; } = "";
    public string Enabled { get; set; } = "";
    public string Profile { get; set; } = "";
    public bool IsOrphaned { get; set; }
    public string Status => IsOrphaned ? "Orphaned" : "Valid";
    public bool IsProtected => !SafetyGuard.IsFirewallRuleSafeToDelete(DisplayName);
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

/// <summary>
/// Scans Windows Firewall rules for orphaned entries — rules whose Program
/// path points to a non-existent executable. These are left behind when
/// programs are uninstalled without cleaning up their firewall registrations.
/// </summary>
public static class FirewallRuleScanner
{
    private static readonly string SystemRoot =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Enumerate all firewall rules and flag those referencing deleted programs.
    /// Uses <c>Get-NetFirewallRule</c> + <c>Get-NetFirewallApplicationFilter</c>
    /// via PowerShell for reliable JSON output.
    /// </summary>
    public static List<FirewallRuleEntry> GetAllRules(bool orphanedOnly = false)
    {
        var entries = new List<FirewallRuleEntry>();

        try
        {
            // Step 1: Get all firewall rules with their application filters in one call.
            // We join rules with their application filters to get the Program path.
            var result = ExternalProcessRunner.Run(PowerShellCommand(
                "$rules = Get-NetFirewallRule | Select-Object Name,DisplayName,Direction,Action,Enabled,Profile; " +
                "$appFilters = Get-NetFirewallApplicationFilter | Select-Object InstanceID,Program; " +
                "$lookup = @{}; foreach ($f in $appFilters) { $lookup[$f.InstanceID] = $f.Program }; " +
                "$result = foreach ($r in $rules) { " +
                "  [PSCustomObject]@{ " +
                "    Name=$r.Name; DisplayName=$r.DisplayName; " +
                "    Direction=$r.Direction.ToString(); Action=$r.Action.ToString(); " +
                "    Enabled=$r.Enabled.ToString(); Profile=$r.Profile.ToString(); " +
                "    Program=if($lookup.ContainsKey($r.Name)){$lookup[$r.Name]}else{''} " +
                "  } " +
                "}; " +
                "$result | ConvertTo-Json -Depth 2 -Compress",
                TimeSpan.FromSeconds(60)));
            var output = result.Output;
            if (string.IsNullOrWhiteSpace(output)) return entries;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    TryAddRule(entries, el, orphanedOnly);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                TryAddRule(entries, root, orphanedOnly);
            }
        }
        catch { /* PowerShell / parse failure */ }

        return entries.OrderByDescending(e => e.IsOrphaned).ThenBy(e => e.DisplayName).ToList();
    }

    /// <summary>
    /// Delete the specified firewall rules using <c>Remove-NetFirewallRule</c>.
    /// </summary>
    public static int DeleteRules(IEnumerable<FirewallRuleEntry> rules)
        => DeleteRulesDetailed(rules).Count(result => result.Succeeded);

    public static IReadOnlyList<AdministrativeMutationResult> DeleteRulesDetailed(
        IEnumerable<FirewallRuleEntry> rules,
        bool dryRun = false)
        => rules
            .Where(rule => rule.IsSelected)
            .Select(rule => DeleteRuleDetailed(rule, dryRun))
            .ToList();

    public static bool DeleteRule(FirewallRuleEntry rule)
        => DeleteRuleDetailed(rule).Succeeded;

    public static AdministrativeMutationResult DeleteRuleDetailed(
        FirewallRuleEntry rule,
        bool dryRun = false)
    {
        const string operation = "firewall-rule-delete";
        var target = string.IsNullOrWhiteSpace(rule.DisplayName)
            ? rule.Name
            : rule.DisplayName;

        if (string.IsNullOrWhiteSpace(rule.Name))
            return AdministrativeMutationPolicy.Unsupported(
                operation,
                target,
                "The firewall rule has no stable rule name.");

        if (!SafetyGuard.IsFirewallRuleSafeToDelete(rule.DisplayName))
            return AdministrativeMutationPolicy.Skipped(
                operation,
                target,
                "Present",
                "Protected Windows firewall rule.");

        var before = JsonSerializer.Serialize(new
        {
            rule.Name,
            rule.DisplayName,
            rule.Direction,
            rule.Action,
            rule.Program,
            rule.Enabled,
            rule.Profile,
        });
        var rollback = BuildRollbackCommand(rule);

        if (dryRun)
            return AdministrativeMutationPolicy.Preview(
                operation,
                target,
                before,
                "Absent (planned)",
                rollback);

        try
        {
            var result = ExternalProcessRunner.Run(new ExternalProcessCommand("powershell.exe")
            {
                Arguments = new[]
                {
                    "-NoProfile",
                    "-EncodedCommand",
                    EncodePsCommand(
                        $"Remove-NetFirewallRule -Name '{EscapePs(rule.Name)}' -ErrorAction Stop; " +
                        $"if (Get-NetFirewallRule -Name '{EscapePs(rule.Name)}' -ErrorAction SilentlyContinue) " +
                        "{ throw 'The firewall rule still exists after removal.' }"),
                },
                Timeout = TimeSpan.FromSeconds(15),
            });
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
        catch (Exception ex)
        {
            return AdministrativeMutationPolicy.Failed(
                operation,
                target,
                before,
                ex.Message,
                rollback);
        }
    }

    // ===============================================================
    //  Helpers
    // ===============================================================

    private static void TryAddRule(List<FirewallRuleEntry> entries, JsonElement el, bool orphanedOnly)
    {
        try
        {
            var program = GetStr(el, "Program");
            var isOrphaned = IsOrphanedRule(program);

            if (orphanedOnly && !isOrphaned) return;

            entries.Add(new FirewallRuleEntry
            {
                Name = GetStr(el, "Name"),
                DisplayName = GetStr(el, "DisplayName"),
                Direction = GetStr(el, "Direction"),
                Action = GetStr(el, "Action"),
                Program = program,
                Enabled = GetStr(el, "Enabled"),
                Profile = GetStr(el, "Profile"),
                IsOrphaned = isOrphaned,
            });
        }
        catch { /* skip malformed entry */ }
    }

    /// <summary>
    /// A firewall rule is orphaned when its Program path references a
    /// specific executable that no longer exists on disk. Rules with no
    /// program ("Any") or system-path programs are never considered orphaned.
    /// </summary>
    private static bool IsOrphanedRule(string program)
    {
        if (string.IsNullOrWhiteSpace(program)) return false;

        // "Any" or "*" means all programs — not orphaned.
        if (program == "*" || program.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = Environment.ExpandEnvironmentVariables(program.Trim());

        // System paths are never considered orphaned.
        if (IsSystemPath(path)) return false;

        // Only flag fully-qualified paths we can actually check.
        if (!Path.IsPathRooted(path)) return false;

        return !File.Exists(path);
    }

    private static bool IsSystemPath(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.StartsWith(SystemRoot.ToLowerInvariant()) ||
               lower.Contains("system32") ||
               lower.Contains("syswow64") ||
               lower.Contains("svchost.exe");
    }

    private static string EscapePs(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\0", "").Replace("'", "''");

    private static string BuildRollbackCommand(FirewallRuleEntry rule)
    {
        var command =
            $"New-NetFirewallRule -Name '{EscapePs(rule.Name)}' " +
            $"-DisplayName '{EscapePs(rule.DisplayName)}' " +
            $"-Direction '{EscapePs(rule.Direction)}' " +
            $"-Action '{EscapePs(rule.Action)}' " +
            $"-Enabled '{EscapePs(rule.Enabled)}' " +
            $"-Profile '{EscapePs(rule.Profile)}'";
        if (!string.IsNullOrWhiteSpace(rule.Program) && rule.Program != "Any" && rule.Program != "*")
            command += $" -Program '{EscapePs(rule.Program)}'";
        return command;
    }

    public static string EncodePsCommand(string script) =>
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    private static ExternalProcessCommand PowerShellCommand(string script, TimeSpan timeout)
        => new("powershell.exe")
        {
            Arguments = new[] { "-NoProfile", "-Command", script },
            Timeout = timeout,
            OutputLimitChars = 512 * 1024,
            ErrorLimitChars = 64 * 1024,
            RedactAbsolutePaths = true,
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
}
