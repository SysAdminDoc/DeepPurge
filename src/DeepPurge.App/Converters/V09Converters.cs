using System.Globalization;
using System.Windows.Data;

namespace DeepPurge.App.Converters;

/// <summary>Format a byte count as a human-readable "1.2 MB" string.</summary>
public class BytesToSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long  l => l,
            int   i => i,
            double d => (long)d,
            _ => 0,
        };
        if (bytes <= 0) return "";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes; int i2 = 0;
        while (b >= 1024 && i2 < u.Length - 1) { b /= 1024; i2++; }
        return $"{b:F1} {u[i2]}";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"OLD" for true, empty for false — badge column on Drivers.</summary>
public class BoolToOldBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? "OLD" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Join a list of file paths into a newline-separated string for display.</summary>
public class PathListJoinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is System.Collections.IEnumerable e)
        {
            var list = e.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
            return string.Join("  |  ", list);
        }
        return value?.ToString() ?? "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
