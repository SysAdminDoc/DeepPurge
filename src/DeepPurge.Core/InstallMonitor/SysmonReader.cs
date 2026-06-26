using System.Diagnostics.Eventing.Reader;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.InstallMonitor;

public record SysmonRegistryChange(
    string EventType,
    string TargetObject,
    string Details,
    DateTime TimeCreated);

public static class SysmonReader
{
    private const string SysmonLogName = "Microsoft-Windows-Sysmon/Operational";

    public static bool IsAvailable()
    {
        try
        {
            using var session = new EventLogSession();
            var logNames = session.GetLogNames();
            foreach (var name in logNames)
                if (name.Equals(SysmonLogName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch { return false; }
    }

    public static List<SysmonRegistryChange> ReadRegistryChangesSince(DateTime startTimeUtc)
    {
        var results = new List<SysmonRegistryChange>();
        try
        {
            var startLocal = startTimeUtc.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
            var query = $"*[System[(EventID=12 or EventID=13 or EventID=14) and TimeCreated[@SystemTime >= '{startTimeUtc:O}']]]";

            var logQuery = new EventLogQuery(SysmonLogName, PathType.LogName, query);
            using var reader = new EventLogReader(logQuery);

            EventRecord? record;
            while ((record = reader.ReadEvent()) != null)
            {
                using (record)
                {
                    try
                    {
                        var eventType = record.Id switch
                        {
                            12 => "CreateDelete",
                            13 => "SetValue",
                            14 => "Rename",
                            _ => "Unknown",
                        };

                        string targetObject = "", details = "";
                        if (record.Properties.Count > 4)
                        {
                            targetObject = record.Properties[4]?.Value?.ToString() ?? "";
                        }
                        if (record.Properties.Count > 5)
                        {
                            details = record.Properties[5]?.Value?.ToString() ?? "";
                        }

                        if (!string.IsNullOrEmpty(targetObject))
                        {
                            results.Add(new SysmonRegistryChange(
                                eventType,
                                targetObject,
                                details,
                                record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow));
                        }
                    }
                    catch (Exception ex) { Log.Warn($"Sysmon event parse failed: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Sysmon event log read failed: {ex.Message}");
        }
        return results;
    }

    public static List<string> ExtractRegistryPaths(List<SysmonRegistryChange> changes)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            var target = c.TargetObject;
            if (string.IsNullOrEmpty(target)) continue;

            target = target
                .Replace(@"HKLM\", @"HKLM\", StringComparison.OrdinalIgnoreCase)
                .Replace(@"HKU\", @"HKCU\", StringComparison.OrdinalIgnoreCase)
                .Replace(@"\REGISTRY\MACHINE\", @"HKLM\", StringComparison.OrdinalIgnoreCase)
                .Replace(@"\REGISTRY\USER\", @"HKCU\", StringComparison.OrdinalIgnoreCase);

            if (target.StartsWith(@"HKLM\SOFTWARE", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(@"HKCU\SOFTWARE", StringComparison.OrdinalIgnoreCase))
            {
                var keyPath = target;
                var lastBackslash = keyPath.LastIndexOf('\\');
                if (lastBackslash > 0 && c.EventType == "SetValue")
                    keyPath = keyPath[..lastBackslash];
                paths.Add(keyPath);
            }
        }
        return paths.ToList();
    }
}
