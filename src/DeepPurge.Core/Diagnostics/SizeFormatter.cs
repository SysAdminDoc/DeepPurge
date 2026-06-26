namespace DeepPurge.Core.Diagnostics;

public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F1} MB";
        return $"{mb / 1024.0:F2} GB";
    }
}
