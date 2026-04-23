using System.Xml;
using DeepPurge.Core.Diagnostics;

namespace DeepPurge.Core.Drivers;

public class DriverPackage
{
    public string PublishedName { get; set; } = "";
    public string OriginalName  { get; set; } = "";
    public string ProviderName  { get; set; } = "";
    public string ClassName     { get; set; } = "";
    public string ClassGuid     { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public Version? ParsedVersion { get; set; }
    public string SignerName    { get; set; } = "";
    public long   SizeBytes     { get; set; }
    public bool   IsOldVersion  { get; set; }
}

/// <summary>
/// Enumerates and removes third-party driver packages from the DriverStore
/// (<c>C:\Windows\System32\DriverStore\FileRepository</c>).
///
/// Shells out to <c>pnputil.exe /enum-drivers</c> and <c>pnputil.exe /delete-driver</c>.
/// Windows 11 22621+ supports <c>/format:xml</c> but older builds print help
/// for the unknown flag — we probe the XML branch by content sniffing and
/// fall back to the stable label-based text parser otherwise. The XML
/// schema also drifts across builds (<c>&lt;Driver&gt;</c>, <c>&lt;DriverPackage&gt;</c>,
/// or <c>&lt;PnpUtilInfo&gt;</c>) so we walk descendants by <c>LocalName</c>
/// instead of pinning a specific element name.
///
/// Inspired by <c>lostindark/DriverStoreExplorer</c> (RAPR).
/// </summary>
public class DriverStoreScanner
{
    public async Task<List<DriverPackage>> EnumerateAsync(CancellationToken ct = default)
    {
        var xmlResult = await RunPnpUtilAsync("/enum-drivers /format:xml", ct);
        List<DriverPackage> packages;
        if (!string.IsNullOrWhiteSpace(xmlResult) &&
            xmlResult.Contains("<?xml", StringComparison.Ordinal) &&
            xmlResult.IndexOf("PNPUTIL [", StringComparison.Ordinal) < 0)
        {
            packages = ParseXml(xmlResult);
            if (packages.Count == 0) packages = ParseText(await RunPnpUtilAsync("/enum-drivers", ct));
        }
        else
        {
            // Re-use the first call's output as the text input if it came back non-XML.
            packages = ParseText(
                (!string.IsNullOrWhiteSpace(xmlResult) && xmlResult.IndexOf("PNPUTIL [", StringComparison.Ordinal) < 0)
                    ? xmlResult
                    : await RunPnpUtilAsync("/enum-drivers", ct));
        }

        ComputeSizes(packages);
        FlagOldVersions(packages);
        return packages;
    }

    /// <summary>
    /// Delete a driver package via <c>pnputil /delete-driver {oemXX.inf}</c>.
    /// </summary>
    public async Task<(bool Ok, string Output)> DeleteAsync(string publishedName, bool force, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(publishedName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(publishedName,
                @"^oem\d+\.inf$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return (false, $"Refusing: '{publishedName}' is not a valid oem*.inf name.");
        }

        var args = force
            ? $"/delete-driver {publishedName} /uninstall /force"
            : $"/delete-driver {publishedName}";
        var output = await RunPnpUtilAsync(args, ct);
        var ok = output.IndexOf("deleted successfully", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 output.IndexOf("Driver package deleted", StringComparison.OrdinalIgnoreCase) >= 0;
        return (ok, output);
    }

    // ═══════════════════════════════════════════════════════

    private static async Task<string> RunPnpUtilAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow  = true,
            // pnputil writes using the OEM console code page; UTF-8 garbles box-drawing.
            StandardOutputEncoding = System.Text.Encoding.GetEncoding(System.Text.Encoding.Default.CodePage),
            StandardErrorEncoding  = System.Text.Encoding.GetEncoding(System.Text.Encoding.Default.CodePage),
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (await stdoutTask) + (await stderrTask);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"pnputil failed: {ex.Message}");
            return "";
        }
    }

    private static List<DriverPackage> ParseXml(string xml)
    {
        var list = new List<DriverPackage>();
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.LoadXml(xml);
            if (doc.DocumentElement == null) return list;

            // Schema-agnostic: every node with LocalName Driver / DriverPackage.
            foreach (var node in DescendantsByLocalName(doc.DocumentElement, "Driver", "DriverPackage"))
            {
                var p = new DriverPackage
                {
                    PublishedName = ReadField(node, "PublishedName", "Published Name"),
                    OriginalName  = ReadField(node, "OriginalName",  "Original Name"),
                    ProviderName  = ReadField(node, "ProviderName",  "Provider Name", "DriverPackageProvider"),
                    ClassName     = ReadField(node, "ClassName",     "Class Name", "Class"),
                    ClassGuid     = ReadField(node, "ClassGuid",     "Class GUID"),
                    DriverVersion = ReadField(node, "DriverVersion", "Driver Version", "Driver date and version"),
                    SignerName    = ReadField(node, "SignerName",    "Signer Name"),
                };
                ExtractDateAndVersion(p);
                if (!string.IsNullOrEmpty(p.PublishedName)) list.Add(p);
            }
        }
        catch (Exception ex) { Log.Warn($"ParseXml: {ex.Message}"); }
        return list;
    }

    // pnputil text output has stable field labels; blank lines delimit records.
    private static List<DriverPackage> ParseText(string text)
    {
        var list = new List<DriverPackage>();
        DriverPackage? cur = null;

        void Flush()
        {
            if (cur != null && !string.IsNullOrEmpty(cur.PublishedName))
            {
                ExtractDateAndVersion(cur);
                list.Add(cur);
            }
            cur = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();

            // Recognise both modern ("/enum-drivers") and legacy ("-e") label sets.
            if      (Label(key, "Published Name", "Published name"))                     { cur ??= new(); cur.PublishedName = val; }
            else if (Label(key, "Original Name",  "Original name"))                      { cur ??= new(); cur.OriginalName  = val; }
            else if (Label(key, "Provider Name",  "Provider name", "Driver package provider")) { cur ??= new(); cur.ProviderName = val; }
            else if (Label(key, "Class Name",     "Class name", "Class"))                { cur ??= new(); cur.ClassName = val; }
            else if (Label(key, "Class GUID",     "Class Guid"))                         { cur ??= new(); cur.ClassGuid = val; }
            else if (Label(key, "Driver Version", "Driver date and version"))            { cur ??= new(); cur.DriverVersion = val; }
            else if (Label(key, "Signer Name",    "Signer name"))                        { cur ??= new(); cur.SignerName = val; }
        }
        Flush();
        return list;
    }

    // Version strings come in two flavours:
    //   "31.0.15.3623"                        (just a Version)
    //   "12/15/2023 31.0.15.3623"             (date + Version)
    //   "15.12.2023 31.0.15.3623"             (locale-specific date)
    // Parse date with InvariantCulture AND CurrentCulture; accept the first win.
    private static void ExtractDateAndVersion(DriverPackage p)
    {
        if (string.IsNullOrWhiteSpace(p.DriverVersion)) return;
        var tokens = p.DriverVersion.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (p.DriverDate == null &&
                (DateTime.TryParse(token, System.Globalization.CultureInfo.InvariantCulture,
                                   System.Globalization.DateTimeStyles.None, out var dtInv) ||
                 DateTime.TryParse(token, System.Globalization.CultureInfo.CurrentCulture,
                                   System.Globalization.DateTimeStyles.None, out dtInv)))
            {
                p.DriverDate = dtInv;
            }
            else if (p.ParsedVersion == null && Version.TryParse(token, out var v))
            {
                p.ParsedVersion = v;
            }
        }
    }

    private static void ComputeSizes(List<DriverPackage> packages)
    {
        var repo = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!Directory.Exists(repo)) return;

        // FileRepository dirs are named inf_arch_hash (e.g. nvlt.inf_amd64_abc123).
        // Map by the prefix before the first underscore so lookups are O(n).
        Dictionary<string, List<string>> repoMap;
        try
        {
            repoMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(repo))
            {
                var leaf = Path.GetFileName(dir);
                var underscore = leaf.IndexOf('_');
                if (underscore <= 0) continue;
                var prefix = leaf[..underscore];
                if (!repoMap.TryGetValue(prefix, out var bucket))
                    repoMap[prefix] = bucket = new List<string>();
                bucket.Add(dir);
            }
        }
        catch (Exception ex) { Log.Warn($"ComputeSizes: repo scan failed: {ex.Message}"); return; }

        foreach (var p in packages)
        {
            if (string.IsNullOrEmpty(p.OriginalName)) continue;
            if (!repoMap.TryGetValue(p.OriginalName, out var candidates)) continue;
            long total = 0;
            foreach (var dir in candidates)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { total += new FileInfo(f).Length; }
                        catch { /* ACL / long-path: skip */ }
                    }
                }
                catch { /* permission denied on some sub-dir: skip */ }
            }
            p.SizeBytes = total;
        }
    }

    private static void FlagOldVersions(List<DriverPackage> packages)
    {
        var families = packages
            .Where(p => !string.IsNullOrEmpty(p.OriginalName))
            .GroupBy(p => p.OriginalName, StringComparer.OrdinalIgnoreCase);

        foreach (var family in families)
        {
            if (family.Count() < 2) continue;
            var newest = family
                .OrderByDescending(p => p.ParsedVersion ?? new Version(0, 0))
                .ThenByDescending(p => p.DriverDate ?? DateTime.MinValue)
                .First();
            foreach (var p in family)
                if (!ReferenceEquals(p, newest)) p.IsOldVersion = true;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Namespace-independent XML helpers (shared patterns with StartupImpactCalculator)
    // ═══════════════════════════════════════════════════════

    private static IEnumerable<XmlNode> DescendantsByLocalName(XmlNode root, params string[] localNames)
    {
        var targets = new HashSet<string>(localNames, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<XmlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (XmlNode child in cur.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (targets.Contains(child.LocalName)) yield return child;
                stack.Push(child);
            }
        }
    }

    private static string ReadField(XmlNode node, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            var attr = node.Attributes?[name];
            if (attr != null && !string.IsNullOrEmpty(attr.Value)) return attr.Value;
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

    private static bool Label(string key, params string[] labels)
    {
        foreach (var l in labels)
            if (string.Equals(key, l, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
