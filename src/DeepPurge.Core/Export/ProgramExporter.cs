using System.Text;
using DeepPurge.Core.Models;

namespace DeepPurge.Core.Export;

public static class ProgramExporter
{
    public static string ExportToCsv(IEnumerable<InstalledProgram> programs, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"Name\",\"Version\",\"Publisher\",\"Install Date\",\"Size (KB)\",\"Install Location\",\"Uninstall String\",\"Source\"");

        foreach (var p in programs)
        {
            sb.AppendLine($"\"{Esc(p.DisplayName)}\",\"{Esc(p.DisplayVersion)}\",\"{Esc(p.Publisher)}\",\"{p.InstallDateDisplay}\",\"{p.EstimatedSizeKB}\",\"{Esc(p.InstallLocation)}\",\"{Esc(p.UninstallString)}\",\"{p.SourceDisplay}\"");
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return filePath;
    }

    public static string ExportToHtml(IEnumerable<InstalledProgram> programs, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
        sb.AppendLine("<title>Installed Programs - DeepPurge Export</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("*{margin:0;padding:0;box-sizing:border-box}");
        sb.AppendLine("body{font-family:'Segoe UI',sans-serif;background:#1e1e2e;color:#cdd6f4;padding:32px}");
        sb.AppendLine("h1{color:#89b4fa;margin-bottom:8px;font-size:24px}");
        sb.AppendLine(".meta{color:#7f849c;margin-bottom:24px;font-size:13px}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;background:#181825;border-radius:8px;overflow:hidden}");
        sb.AppendLine("th{background:#313244;color:#a6adc8;padding:12px 16px;text-align:left;font-size:11px;text-transform:uppercase;letter-spacing:.5px}");
        sb.AppendLine("td{padding:10px 16px;border-bottom:1px solid #313244;font-size:13px}");
        sb.AppendLine("tr:hover td{background:#313244}");
        sb.AppendLine(".size{text-align:right;white-space:nowrap}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<h1>Installed Programs</h1>");
        sb.AppendLine($"<p class='meta'>Exported by DeepPurge on {DateTime.Now:yyyy-MM-dd HH:mm} | {programs.Count()} programs</p>");
        sb.AppendLine("<table><thead><tr><th>Program</th><th>Version</th><th>Publisher</th><th>Installed</th><th class='size'>Size</th><th>Source</th></tr></thead><tbody>");

        foreach (var p in programs.OrderBy(p => p.DisplayName))
        {
            sb.AppendLine($"<tr><td>{H(p.DisplayName)}</td><td>{H(p.DisplayVersion)}</td><td>{H(p.Publisher)}</td><td>{p.InstallDateDisplay}</td><td class='size'>{p.EstimatedSizeDisplay}</td><td>{p.SourceDisplay}</td></tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return filePath;
    }

    public static string ExportToJson(IEnumerable<InstalledProgram> programs, string filePath)
    {
        var data = programs.Select(p => new
        {
            p.DisplayName, p.DisplayVersion, p.Publisher,
            InstallDate = p.InstallDateDisplay,
            SizeKB = p.EstimatedSizeKB,
            Size = p.EstimatedSizeDisplay,
            p.InstallLocation, p.UninstallString,
            Source = p.SourceDisplay,
        });

        var json = System.Text.Json.JsonSerializer.Serialize(data,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json, Encoding.UTF8);
        return filePath;
    }

    private static string Esc(string s) => s.Replace("\"", "\"\"");
    private static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
}
