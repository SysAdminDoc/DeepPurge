using System.Diagnostics;
using System.Management;
using DeepPurge.Core.Diagnostics;
using DeepPurge.Core.Execution;

namespace DeepPurge.Core.Safety;

public class RestorePointInfo : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    public int SequenceNumber { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreationTime { get; set; }
    public string Type { get; set; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string CreationTimeDisplay => CreationTime.ToString("yyyy-MM-dd HH:mm");
    public string AgeDisplay
    {
        get
        {
            var age = DateTime.Now - CreationTime;
            if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d ago";
            if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h ago";
            return $"{(int)age.TotalMinutes}m ago";
        }
    }
}

public static class SystemRestoreManager
{
    public static List<RestorePointInfo> GetRestorePoints()
    {
        var points = new List<RestorePointInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\default",
                "SELECT * FROM SystemRestorePoint");
            foreach (ManagementBaseObject baseObj in searcher.Get())
            {
                using var obj = baseObj;
                var desc = obj["Description"]?.ToString() ?? "";
                var seq = Convert.ToInt32(obj["SequenceNumber"]);
                var creationStr = obj["CreationTime"]?.ToString() ?? "";
                var type = Convert.ToInt32(obj["RestorePointType"]);

                DateTime created = DateTime.MinValue;
                if (creationStr.Length >= 14)
                {
                    try
                    {
                        created = ManagementDateTimeConverter.ToDateTime(creationStr);
                    }
                    catch
                    {
                        var yr = creationStr[..4]; var mo = creationStr[4..6];
                        var dy = creationStr[6..8]; var hr = creationStr[8..10];
                        var mn = creationStr[10..12]; var sc = creationStr[12..14];
                        DateTime.TryParse($"{yr}-{mo}-{dy} {hr}:{mn}:{sc}", out created);
                    }
                }

                var typeStr = type switch
                {
                    0 => "Application Install",
                    1 => "Application Uninstall",
                    6 => "Restore",
                    7 => "Checkpoint",
                    10 => "Device Driver",
                    11 => "First Run",
                    12 => "Modify Settings",
                    13 => "Cancelled Operation",
                    _ => $"Type {type}",
                };

                points.Add(new RestorePointInfo
                {
                    SequenceNumber = seq,
                    Description = desc,
                    CreationTime = created,
                    Type = typeStr,
                });
            }
        }
        catch (Exception ex) { Log.Warn($"WMI restore point query failed: {ex.Message}"); }

        return points.OrderByDescending(p => p.CreationTime).ToList();
    }

    public static bool CreateRestorePoint(string description = "DeepPurge Manual Checkpoint")
    {
        try
        {
            var script = $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'MODIFY_SETTINGS'";
            var result = ExternalProcessRunner.Run(new ExternalProcessCommand("powershell.exe")
            {
                Arguments = new[] { "-NoProfile", "-Command", script },
                Timeout = TimeSpan.FromMinutes(2),
            });
            return result.Success;
        }
        catch (Exception ex) { Log.Warn($"Create restore point failed: {ex.Message}"); return false; }
    }

    public static bool DeleteRestorePoint(int sequenceNumber)
    {
        try
        {
            var result = ExternalProcessRunner.Run(new ExternalProcessCommand("vssadmin.exe")
            {
                Arguments = new[] { "delete", "shadows", $"/Shadow={{sequence:{sequenceNumber}}}", "/quiet" },
                Timeout = TimeSpan.FromSeconds(30),
            });
            return result.Success;
        }
        catch (Exception ex) { Log.Warn($"Delete restore point failed: {ex.Message}"); return false; }
    }

    public static bool IsRestoreEnabled()
    {
        try
        {
            var result = ExternalProcessRunner.Run(new ExternalProcessCommand("powershell.exe")
            {
                Arguments = new[] { "-NoProfile", "-Command", "(Get-ComputerRestorePoint -ErrorAction SilentlyContinue) -ne $null; $?" },
                Timeout = TimeSpan.FromSeconds(15),
            });
            return result.Output.Contains("True", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn($"Check restore enabled failed: {ex.Message}"); return false; }
    }
}
