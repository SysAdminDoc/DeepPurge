using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.InstallMonitor;

internal sealed record SysmonEventData(
    int EventId,
    DateTime TimeCreatedUtc,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Reads Sysmon by named XML fields and correlates registry events to the
/// launched installer's ProcessGuid tree. Positional EventRecord properties
/// are deliberately not used because their ordering changes with schema and
/// event version.
/// </summary>
public static class SysmonReader
{
    private const string SysmonLogName =
        "Microsoft-Windows-Sysmon/Operational";

    public static bool IsAvailable()
    {
        try
        {
            using var session = new EventLogSession();
            return session.GetLogNames().Any(name =>
                name.Equals(
                    SysmonLogName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static List<SysmonRegistryChange> ReadRegistryChangesSince(
        DateTime startTimeUtc)
    {
        var events = ReadEvents(
            startTimeUtc,
            DateTime.UtcNow,
            includeProcessEvents: false);
        return events
            .Where(e => e.EventId is 12 or 13 or 14)
            .Select(ToRegistryChange)
            .Where(change => change != null)
            .Cast<SysmonRegistryChange>()
            .ToList();
    }

    public static List<SysmonRegistryChange> ReadCorrelatedRegistryChanges(
        DateTime startTimeUtc,
        DateTime endTimeUtc,
        int installerProcessId,
        string installerPath,
        out bool processTreeCorrelated)
    {
        var events = ReadEvents(
            startTimeUtc,
            endTimeUtc,
            includeProcessEvents: true);
        return CorrelateRegistryChanges(
            events,
            installerProcessId,
            installerPath,
            startTimeUtc,
            endTimeUtc,
            out processTreeCorrelated);
    }

    internal static List<SysmonRegistryChange> CorrelateRegistryChanges(
        IReadOnlyList<SysmonEventData> events,
        int installerProcessId,
        string installerPath,
        DateTime startTimeUtc,
        DateTime endTimeUtc,
        out bool processTreeCorrelated)
    {
        processTreeCorrelated = false;
        if (installerProcessId <= 0 ||
            string.IsNullOrWhiteSpace(installerPath))
            return new List<SysmonRegistryChange>();

        var normalizedInstaller = NormalizeImagePath(installerPath);
        var bounded = events
            .Where(e => e.TimeCreatedUtc >= startTimeUtc &&
                        e.TimeCreatedUtc <= endTimeUtc)
            .OrderBy(e => e.TimeCreatedUtc)
            .ToList();
        var processes = bounded
            .Where(e => e.EventId == 1)
            .ToList();

        var trustedProcessGuids = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var rootEvents = processes
            .Where(e =>
                GetInt32(e.Fields, "ProcessId") == installerProcessId &&
                ImageMatches(e.Fields, normalizedInstaller))
            .ToList();
        foreach (var root in rootEvents)
        {
            var guid = Get(root.Fields, "ProcessGuid");
            if (!string.IsNullOrWhiteSpace(guid))
                trustedProcessGuids.Add(guid);
        }

        processTreeCorrelated = trustedProcessGuids.Count > 0;
        if (processTreeCorrelated)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var process in processes)
                {
                    var processGuid = Get(process.Fields, "ProcessGuid");
                    var parentGuid = Get(
                        process.Fields,
                        "ParentProcessGuid");
                    if (string.IsNullOrWhiteSpace(processGuid) ||
                        trustedProcessGuids.Contains(processGuid) ||
                        string.IsNullOrWhiteSpace(parentGuid) ||
                        !trustedProcessGuids.Contains(parentGuid))
                        continue;

                    trustedProcessGuids.Add(processGuid);
                    changed = true;
                }
            }
        }

        var results = new List<SysmonRegistryChange>();
        foreach (var item in bounded.Where(e => e.EventId is 12 or 13 or 14))
        {
            var processGuid = Get(item.Fields, "ProcessGuid");
            var exactRootFallback =
                GetInt32(item.Fields, "ProcessId") == installerProcessId &&
                ImageMatches(item.Fields, normalizedInstaller);
            if (!trustedProcessGuids.Contains(processGuid) &&
                !exactRootFallback)
                continue;

            var change = ToRegistryChange(item);
            if (change != null)
                results.Add(change);
        }

        return results;
    }

    public static List<string> ExtractRegistryPaths(
        IReadOnlyList<SysmonRegistryChange> changes)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            var target = NormalizeRegistryPath(change.TargetObject);
            if (string.IsNullOrWhiteSpace(target) ||
                !IsSoftwarePath(target))
                continue;

            if (change.EventType.Contains(
                    "SetValue",
                    StringComparison.OrdinalIgnoreCase))
            {
                var separator = target.LastIndexOf('\\');
                if (separator > 0)
                    target = target[..separator];
            }

            paths.Add(target);
        }
        return paths.ToList();
    }

    internal static SysmonEventData? ParseEventXml(
        string xml,
        DateTime fallbackTimeUtc)
    {
        try
        {
            var document = XDocument.Parse(
                xml,
                LoadOptions.PreserveWhitespace);
            var eventIdText = document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "EventID")
                ?.Value;
            if (!int.TryParse(
                    eventIdText,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var eventId))
                return null;

            var systemTime = document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "TimeCreated")
                ?.Attribute("SystemTime")
                ?.Value;
            var timestamp = DateTime.TryParse(
                systemTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal |
                DateTimeStyles.AssumeUniversal,
                out var parsedTime)
                ? parsedTime.ToUniversalTime()
                : fallbackTimeUtc.ToUniversalTime();

            var fields = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var data in document
                         .Descendants()
                         .Where(element =>
                             element.Name.LocalName == "Data"))
            {
                var name = data.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    fields[name] = data.Value;
            }

            return new SysmonEventData(eventId, timestamp, fields);
        }
        catch (Exception ex)
        {
            Log.Warn($"Sysmon event XML parse failed: {ex.Message}");
            return null;
        }
    }

    internal static string NormalizeRegistryPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";

        var normalized = target.Trim();
        normalized = ReplacePrefix(
            normalized,
            @"\REGISTRY\MACHINE\",
            @"HKLM\");
        normalized = ReplacePrefix(
            normalized,
            @"\REGISTRY\USER\",
            @"HKU\");
        normalized = ReplacePrefix(
            normalized,
            @"HKEY_LOCAL_MACHINE\",
            @"HKLM\");
        normalized = ReplacePrefix(
            normalized,
            @"HKEY_USERS\",
            @"HKU\");
        normalized = ReplacePrefix(
            normalized,
            @"HKEY_CURRENT_USER\",
            @"HKCU\");

        return normalized;
    }

    private static List<SysmonEventData> ReadEvents(
        DateTime startTimeUtc,
        DateTime endTimeUtc,
        bool includeProcessEvents)
    {
        var results = new List<SysmonEventData>();
        try
        {
            var eventClause = includeProcessEvents
                ? "(EventID=1 or EventID=12 or EventID=13 or EventID=14)"
                : "(EventID=12 or EventID=13 or EventID=14)";
            var start = startTimeUtc
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            var end = endTimeUtc
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            var query =
                $"*[System[{eventClause} and " +
                $"TimeCreated[@SystemTime >= '{start}' and " +
                $"@SystemTime <= '{end}']]]";
            var logQuery = new EventLogQuery(
                SysmonLogName,
                PathType.LogName,
                query);
            using var reader = new EventLogReader(logQuery);

            EventRecord? record;
            while ((record = reader.ReadEvent()) != null)
            {
                using (record)
                {
                    try
                    {
                        var parsed = ParseEventXml(
                            record.ToXml(),
                            record.TimeCreated?.ToUniversalTime() ??
                            DateTime.UtcNow);
                        if (parsed != null)
                            results.Add(parsed);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Sysmon event read failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Sysmon event log read failed: {ex.Message}");
        }
        return results;
    }

    private static SysmonRegistryChange? ToRegistryChange(
        SysmonEventData item)
    {
        var target = Get(item.Fields, "TargetObject");
        if (string.IsNullOrWhiteSpace(target)) return null;

        var fallbackType = item.EventId switch
        {
            12 => "CreateDelete",
            13 => "SetValue",
            14 => "Rename",
            _ => "Unknown",
        };
        var eventType = Get(item.Fields, "EventType");
        if (string.IsNullOrWhiteSpace(eventType))
            eventType = fallbackType;

        return new SysmonRegistryChange(
            eventType,
            target,
            Get(item.Fields, "Details"),
            item.TimeCreatedUtc,
            Get(item.Fields, "ProcessGuid"),
            GetInt32(item.Fields, "ProcessId"),
            Get(item.Fields, "Image"));
    }

    private static bool IsSoftwarePath(string target)
    {
        if (target.StartsWith(
                @"HKLM\SOFTWARE",
                StringComparison.OrdinalIgnoreCase))
            return true;
        if (!target.StartsWith(
                @"HKU\",
                StringComparison.OrdinalIgnoreCase))
            return target.StartsWith(
                @"HKCU\SOFTWARE",
                StringComparison.OrdinalIgnoreCase);

        var firstSeparator = target.IndexOf('\\', @"HKU\".Length);
        if (firstSeparator < 0) return false;
        var remainder = target[(firstSeparator + 1)..];
        return remainder.StartsWith(
                   "SOFTWARE",
                   StringComparison.OrdinalIgnoreCase) ||
               target[..firstSeparator].EndsWith(
                   "_Classes",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplacePrefix(
        string value,
        string prefix,
        string replacement)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? replacement + value[prefix.Length..]
            : value;

    private static bool ImageMatches(
        IReadOnlyDictionary<string, string> fields,
        string normalizedInstaller)
        => string.Equals(
            NormalizeImagePath(Get(fields, "Image")),
            normalizedInstaller,
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }

    private static string Get(
        IReadOnlyDictionary<string, string> fields,
        string name)
        => fields.TryGetValue(name, out var value) ? value : "";

    private static int GetInt32(
        IReadOnlyDictionary<string, string> fields,
        string name)
        => int.TryParse(
            Get(fields, name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
}
