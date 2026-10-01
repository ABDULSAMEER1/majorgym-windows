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

/// <summary>Photo path -&gt; <see cref="System.Windows.Media.ImageSource"/> (null when the
/// path is blank or the file is gone) via <see cref="BitmapImageUtils.LoadFromFile"/>.</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        BitmapImageUtils.LoadFromFile(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the referenced photo file exists on disk, Collapsed otherwise —
/// Android's <c>path.takeIf { it.isNotBlank() }?.let(::File)?.takeIf { it.exists() }</c>
/// gate. ConverterParameter="Invert" flips it (show the placeholder while there's no file).</summary>
public sealed class PathExistsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var exists = value is string s && !string.IsNullOrWhiteSpace(s) && System.IO.File.Exists(s);
        if (parameter is "Invert") exists = !exists;
        return exists ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Epoch millis -&gt; "dd MMM yyyy" via <see cref="MajorGym.Data.DateUtils.FormatDate"/>.
/// Used by the History list on Profile (Android: <c>formatDate(h.dateMillis)</c>).</summary>
public sealed class MillisToDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is long l ? MajorGym.Data.DateUtils.FormatDate(l) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A rupee amount -&gt; Android's Indian-grouped money text via
/// <see cref="MajorGym.Data.DateUtils.FormatMoney"/> (History list's fee column).</summary>
public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? MajorGym.Data.DateUtils.FormatMoney(d) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>value.ToString() == ConverterParameter — used by the bottom navigation to light the
/// item whose key matches <c>NavigationViewModel.SelectedNav</c>.</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
