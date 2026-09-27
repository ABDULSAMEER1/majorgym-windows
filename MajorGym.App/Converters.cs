using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MajorGym.App;

/// <summary>Null/empty-string -&gt; Collapsed, anything else -&gt; Visible. Used for
/// "only show this element while an optional status/error message is set" bindings
/// (e.g. RegisteredView's ShareStatus, RenewView's validation messages).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        (value is null || (value is string s && string.IsNullOrEmpty(s))) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverse of <see cref="System.Windows.Controls.BoolToVisibilityConverter"/> —
/// true -&gt; Collapsed, false -&gt; Visible. Used where a card should show only while a
/// flag is FALSE (e.g. "no members match" empty-state text hidden once results exist).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
