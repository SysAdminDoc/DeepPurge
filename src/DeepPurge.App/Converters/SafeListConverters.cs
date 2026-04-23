using System.Collections;
using System.Globalization;
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
