using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeepPurge.App.Converters;

/// <summary>
/// Returns the first element of an <see cref="IEnumerable"/> (or empty
/// string when the list is null / empty). Used in place of unsafe
/// <c>{Binding Warning[0]}</c> XAML bindings that throw
/// <see cref="IndexOutOfRangeException"/> at binding time on empty lists.
/// </summary>
public class FirstOrEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable e)
        {
            foreach (var item in e) return item?.ToString() ?? "";
        }
        return "";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns the count of items in an <see cref="IEnumerable"/> as a string.
/// Used to avoid relying on .NET's <c>List{T}.Count</c> binding — which works
/// one-way but doesn't react to mutation. For static snapshots (scan
/// results) that's fine; for reactive scenarios use an ObservableCollection.
/// </summary>
public class CountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ICollection c) return c.Count.ToString();
        if (value is IEnumerable e)
        {
            int n = 0;
            foreach (var _ in e) n++;
            return n.ToString();
        }
        return "0";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Shows an empty-state element when a collection has no items. Pass
/// ConverterParameter="Invert" to show content only when the collection has
/// entries.
/// </summary>
[ValueConversion(typeof(IEnumerable), typeof(Visibility))]
public class CollectionEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasItems = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count > 0,
            IEnumerable e => HasAny(e),
            _ => true,
        };

        var invert = parameter?.ToString()?.Equals("Invert", StringComparison.OrdinalIgnoreCase) == true;
        var visible = invert ? hasItems : !hasItems;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static bool HasAny(IEnumerable source)
    {
        foreach (var _ in source) return true;
        return false;
    }
}

/// <summary>
/// Shows an empty state only when the requested navigation panel is active and
/// the bound collection/string has no content.
/// </summary>
public class PanelEmptyStateVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return Visibility.Collapsed;

        var requestedPanel = parameter?.ToString();
        var currentPanel = values[0]?.ToString();
        if (string.IsNullOrWhiteSpace(requestedPanel) ||
            !string.Equals(currentPanel, requestedPanel, StringComparison.OrdinalIgnoreCase))
        {
            return Visibility.Collapsed;
        }

        return HasContent(values[1]) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        var values = new object[targetTypes.Length];
        Array.Fill(values, Binding.DoNothing);
        return values;
    }

    private static bool HasContent(object? value)
    {
        return value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count > 0,
            IEnumerable e => HasAny(e),
            _ => true,
        };
    }

    private static bool HasAny(IEnumerable source)
    {
        foreach (var _ in source) return true;
        return false;
    }
}
