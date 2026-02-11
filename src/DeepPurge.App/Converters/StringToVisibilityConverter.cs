using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeepPurge.App.Converters;

/// <summary>
/// Converts a string to Visibility. Returns Visible if non-empty, Collapsed if null/empty.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
