using System.Text;
using System.Text.Json;
using DeepPurge.Core.Drivers;
using DeepPurge.Core.FileSystem;
using DeepPurge.Core.Shortcuts;
using DeepPurge.Core.Startup;

namespace DeepPurge.Core.Export;

public static class GridExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ExportDrivers(IEnumerable<DriverPackage> items, string filePath, ExportFormat format)
    {
        var list = items.ToList();
        if (format == ExportFormat.Json)
        {
            var data = list.Select(d => new
            {
                d.PublishedName, d.OriginalName, d.ProviderName, d.ClassName,
                d.DriverVersion, DriverDate = d.DriverDate?.ToString("yyyy-MM-dd"),
                SizeBytes = d.SizeBytes, SizeMB = Math.Round(d.SizeBytes / 1048576.0, 2),
                d.IsOldVersion
            });
            File.WriteAllText(filePath, JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"Published Name\",\"Original Name\",\"Provider\",\"Class\",\"Version\",\"Date\",\"Size (MB)\",\"Old Version\"");
            foreach (var d in list)
                sb.AppendLine($"\"{Esc(d.PublishedName)}\",\"{Esc(d.OriginalName)}\",\"{Esc(d.ProviderName)}\",\"{Esc(d.ClassName)}\",\"{Esc(d.DriverVersion)}\",\"{d.DriverDate:yyyy-MM-dd}\",\"{Math.Round(d.SizeBytes / 1048576.0, 2)}\",\"{d.IsOldVersion}\"");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
        return filePath;
    }

    public static string ExportShortcuts(IEnumerable<ShortcutEntry> items, string filePath, ExportFormat format)
    {
        var list = items.ToList();
        if (format == ExportFormat.Json)
        {
            var data = list.Select(s => new
            {
                s.Path, s.TargetPath, Status = s.Status.ToString(),
                s.Arguments, s.WorkingDir, s.Description, s.SizeBytes
            });
            File.WriteAllText(filePath, JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"Shortcut Path\",\"Target\",\"Status\",\"Arguments\",\"Working Dir\",\"Size (bytes)\"");
            foreach (var s in list)
                sb.AppendLine($"\"{Esc(s.Path)}\",\"{Esc(s.TargetPath)}\",\"{s.Status}\",\"{Esc(s.Arguments)}\",\"{Esc(s.WorkingDir)}\",\"{s.SizeBytes}\"");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
        return filePath;
    }

    public static string ExportDuplicates(IEnumerable<DuplicateGroup> items, string filePath, ExportFormat format)
    {
        var list = items.ToList();
        if (format == ExportFormat.Json)
        {
            var data = list.Select(g => new
            {
                g.FileSize,
                g.WastedBytes,
                FileCount = g.Paths.Count,
                Keeper = g.KeeperPath,
                ContentHash = g.ContentHash.ToString("X16"),
                g.Paths,
            });
            File.WriteAllText(filePath, JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"Group\",\"File Size\",\"Wasted Bytes\",\"Keeper\",\"Content Hash\",\"Path\"");
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                foreach (var p in g.Paths)
                    sb.AppendLine($"\"{i + 1}\",\"{g.FileSize}\",\"{g.WastedBytes}\",\"{Esc(g.KeeperPath)}\",\"{g.ContentHash:X16}\",\"{Esc(p)}\"");
            }
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
        return filePath;
    }

    public static string ExportStartupImpact(IEnumerable<StartupImpactEntry> items, string filePath, ExportFormat format)
    {
        var list = items.ToList();
        if (format == ExportFormat.Json)
        {
            var data = list.Select(e => new
            {
                e.ProcessName, e.CommandLine, e.ImagePath,
                e.DiskBytes, e.CpuMs, Impact = e.Impact.ToString(),
                SampleTime = e.SampleTime.ToString("o")
            });
            File.WriteAllText(filePath, JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"Process\",\"Command Line\",\"Image Path\",\"Disk (bytes)\",\"CPU (ms)\",\"Impact\"");
            foreach (var e in list)
                sb.AppendLine($"\"{Esc(e.ProcessName)}\",\"{Esc(e.CommandLine)}\",\"{Esc(e.ImagePath)}\",\"{e.DiskBytes}\",\"{e.CpuMs}\",\"{e.Impact}\"");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
        return filePath;
    }

    private static string Esc(string s) => (s ?? "").Replace("\"", "\"\"");
}

public enum ExportFormat { Csv, Json }
