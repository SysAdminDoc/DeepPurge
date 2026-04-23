using System.Xml;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Startup;

public enum StartupImpact { Unknown, None, Low, Medium, High }

public class StartupImpactEntry
{
    public string ProcessName { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string ImagePath   { get; set; } = "";
    public long   DiskBytes   { get; set; }
    public long   CpuMs       { get; set; }
    public StartupImpact Impact { get; set; }
    public DateTime SampleTime { get; set; }
}

/// <summary>
/// Parses the Windows Diagnostic Infrastructure boot traces in
/// <c>%SystemRoot%\System32\wdi\LogFiles\StartupInfo\Startup{SID}_*.xml</c>
/// and attributes disk / CPU / "started at" metrics to each autorun process.
///
/// Task Manager's "Startup impact" column is derived from the same files.
/// This directory is admin-only; under the CLI's <c>asInvoker</c> manifest
/// the caller must supply elevation.
///
/// <para>
/// Schema robustness: real WDI XMLs carry a default namespace
/// (<c>xmlns="..."</c>) on the root element. An unqualified
/// <c>SelectNodes("//Process")</c> therefore returns zero nodes. We walk
/// descendants manually and match on <see cref="XmlNode.LocalName"/> so
/// namespace churn across Windows versions never silently empties the
/// result.
/// </para>
///
/// Thresholds (matching MS Compatibility Cookbook guidance):
///   High   → DiskBytes &gt; 3 MB  OR CpuMs &gt; 1000 ms
///   Medium → DiskBytes &gt; 300 KB OR CpuMs &gt; 300 ms
///   Low    → below Medium but non-zero
///   None   → both zero
/// </summary>
public class StartupImpactCalculator
{
    private const long MediumDiskBytes = 300L * 1024;
    private const long HighDiskBytes   = 3L * 1024 * 1024;
    private const long MediumCpuMs     = 300;
    private const long HighCpuMs       = 1000;

    private static readonly string StartupInfoDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "wdi", "LogFiles", "StartupInfo");

    /// <summary>
    /// Aggregate impact per unique process name across the N most-recent
    /// boot traces. Returns an empty dict if the WDI provider has no data
    /// or the directory is not readable.
    /// </summary>
    public Dictionary<string, StartupImpactEntry> CalculateForCurrentUser(int traceCount = 3)
    {
        var result = new Dictionary<string, StartupImpactEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(StartupInfoDir)) return result;

        List<FileInfo> traces;
        try
        {
            traces = new DirectoryInfo(StartupInfoDir)
                .EnumerateFiles("Startup*.xml")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(1, traceCount))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"StartupImpact: cannot enumerate WDI dir: {ex.Message}");
            return result;
        }

        foreach (var trace in traces)
        {
            try
            {
                var doc = new XmlDocument { PreserveWhitespace = false };
                doc.Load(trace.FullName);
                if (doc.DocumentElement == null) continue;

                foreach (var processNode in DescendantsByLocalName(doc.DocumentElement, "Process"))
                {
                    var name = ReadField(processNode, "Name", "ImageName", "ProcessName");
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var imagePath   = ReadField(processNode, "ImagePath", "ImageFileName");
                    var commandLine = ReadField(processNode, "CommandLine");
                    long disk = ReadLong(processNode, "DiskUsage", "Bytes", "BytesRead");
                    long cpu  = ReadLong(processNode, "CpuUsage",  "CpuTime", "CpuMs");

                    if (!result.TryGetValue(name, out var entry))
                    {
                        entry = new StartupImpactEntry
                        {
                            ProcessName = name,
                            CommandLine = commandLine,
                            ImagePath   = imagePath,
                            SampleTime  = trace.LastWriteTimeUtc,
                        };
                        result[name] = entry;
                    }
                    // "Worst-case across the most-recent N boots" matches
                    // the intuition users have when glancing at Task Manager.
                    entry.DiskBytes = Math.Max(entry.DiskBytes, disk);
                    entry.CpuMs     = Math.Max(entry.CpuMs, cpu);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"StartupImpact: bad trace {trace.Name}: {ex.Message}");
            }
        }

        foreach (var e in result.Values) e.Impact = Classify(e.DiskBytes, e.CpuMs);
        return result;
    }

    public static StartupImpact Classify(long diskBytes, long cpuMs)
    {
        if (diskBytes == 0 && cpuMs == 0) return StartupImpact.None;
        if (diskBytes > HighDiskBytes || cpuMs > HighCpuMs) return StartupImpact.High;
        if (diskBytes > MediumDiskBytes || cpuMs > MediumCpuMs) return StartupImpact.Medium;
        return StartupImpact.Low;
    }

    // ═══════════════════════════════════════════════════════
    //  Namespace-independent XML helpers
    // ═══════════════════════════════════════════════════════

    private static IEnumerable<XmlNode> DescendantsByLocalName(XmlNode root, string localName)
    {
        var stack = new Stack<XmlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (XmlNode child in cur.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    if (string.Equals(child.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                        yield return child;
                    stack.Push(child);
                }
            }
        }
    }

    private static string ReadField(XmlNode node, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            // Attribute first (some WDI schemas keep Name/ImagePath as attrs).
            var attr = node.Attributes?[name];
            if (attr != null && !string.IsNullOrEmpty(attr.Value)) return attr.Value;
            // Then direct child element.
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element &&
                    string.Equals(child.LocalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    var text = child.InnerText?.Trim();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
        }
        return "";
    }

    private static long ReadLong(XmlNode node, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            var raw = ReadField(node, name);
            if (string.IsNullOrEmpty(raw)) continue;
            // WDI sometimes writes "123.45" (seconds); truncate decimal for ms-class fields.
            var dot = raw.IndexOf('.');
            var intPart = dot >= 0 ? raw[..dot] : raw;
            if (long.TryParse(intPart, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
        }
        return 0;
    }
}
