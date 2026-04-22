using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeepPurge.App.Converters;

/// <summary>
/// Converts a string to Visibility. Collapsed for null/empty/whitespace, Visible otherwise.
/// One-way only — ConvertBack is not meaningful for this use.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
