namespace DeepPurge.Core.Diagnostics;

public record HealthScore(string Category, int Score, string Summary, string Action);

public record HealthReport(
    int OverallScore,
    string Grade,
    List<HealthScore> Categories);

public static class HealthScorer
{
    public static HealthReport Assess()
    {
        var cats = new List<HealthScore>
        {
            AssessJunk(),
            AssessPrivacy(),
            AssessStartup(),
            AssessDisk(),
        };

        var overall = cats.Count > 0 ? (int)Math.Round(cats.Average(c => c.Score)) : 100;
        var grade = overall switch
        {
            >= 90 => "A",
            >= 75 => "B",
            >= 60 => "C",
            >= 40 => "D",
            _ => "F",
        };

        return new HealthReport(overall, grade, cats);
    }

    private static HealthScore AssessJunk()
    {
        try
        {
            var junk = FileSystem.JunkFilesCleaner.ScanForJunk();
            var totalBytes = junk.Sum(c => c.TotalSize);
            var totalMb = totalBytes / (1024.0 * 1024);

            var score = totalMb switch
            {
                < 50 => 100,
                < 200 => 85,
                < 500 => 70,
                < 1000 => 50,
                < 2000 => 30,
                _ => 10,
            };

            return new HealthScore("Junk Files", score,
                $"{totalMb:F0} MB across {junk.Sum(c => c.Files.Count)} items",
                score < 80 ? "Run Junk Cleaner" : "Clean");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health junk assessment: {ex.Message}");
            return new HealthScore("Junk Files", 50, "Could not assess", "Run Junk Cleaner");
        }
    }

    private static HealthScore AssessPrivacy()
    {
        try
        {
            var traces = Privacy.EvidenceRemover.ScanAllTraces();
            var totalItems = traces.Sum(c => c.ItemCount);
            var totalBytes = traces.Sum(c => c.TotalSize);
            var totalMb = totalBytes / (1024.0 * 1024);

            var score = totalItems switch
            {
                < 10 => 100,
                < 50 => 85,
                < 200 => 70,
                < 500 => 50,
                _ => 30,
            };

            return new HealthScore("Privacy", score,
                $"{totalItems} traces ({totalMb:F0} MB)",
                score < 80 ? "Run Evidence Remover" : "Clean");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health privacy assessment: {ex.Message}");
            return new HealthScore("Privacy", 50, "Could not assess", "Run Evidence Remover");
        }
    }

    private static HealthScore AssessStartup()
    {
        try
        {
            var autoruns = Startup.AutorunScanner.GetAllAutoruns();
            var enabled = autoruns.Count(a => a.IsEnabled && a.Type is Startup.AutorunType.RegistryRun or Startup.AutorunType.RegistryRunOnce);

            var score = enabled switch
            {
                <= 5 => 100,
                <= 10 => 85,
                <= 20 => 70,
                <= 30 => 50,
                _ => 30,
            };

            return new HealthScore("Startup Impact", score,
                $"{enabled} enabled autorun entries",
                score < 80 ? "Review Autorun Manager" : "Optimized");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health startup assessment: {ex.Message}");
            return new HealthScore("Startup Impact", 50, "Could not assess", "Check Autorun Manager");
        }
    }

    private static HealthScore AssessDisk()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
            var driveInfo = new DriveInfo(systemDrive);
            var freePercent = 100.0 * driveInfo.AvailableFreeSpace / driveInfo.TotalSize;

            var score = freePercent switch
            {
                >= 30 => 100,
                >= 20 => 85,
                >= 15 => 70,
                >= 10 => 50,
                >= 5 => 30,
                _ => 10,
            };

            var freeGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            return new HealthScore("Disk Space", score,
                $"{freeGb:F1} GB free ({freePercent:F0}%)",
                score < 80 ? "Free up disk space" : "Healthy");
        }
        catch (Exception ex)
        {
            Log.Warn($"Health disk assessment: {ex.Message}");
            return new HealthScore("Disk Space", 50, "Could not assess", "Check Disk Analyzer");
        }
    }
}
