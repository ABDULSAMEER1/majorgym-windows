using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MajorGym.Data;

namespace MajorGym.App.Controls;

/// <summary>Android's <c>DatePickerField</c>: a rounded card showing the date as
/// "dd MMM yyyy" (the app's own <see cref="DateUtils.FormatDate"/>); tapping it opens a date
/// picker. On Windows that is a calendar popup. <see cref="Date"/> is two-way bindable and holds
/// a date-only value (time-of-day is always midnight), so the ViewModel can convert it with
/// <see cref="DateUtils.ToMillis"/> — the same "start of local day" millis Android stores.</summary>
public partial class DatePickerField : UserControl
{
    public static readonly DependencyProperty DateProperty = DependencyProperty.Register(
        nameof(Date), typeof(DateTime), typeof(DatePickerField),
        new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((DatePickerField)d).Refresh()));

    public DateTime Date { get => (DateTime)GetValue(DateProperty); set => SetValue(DateProperty, value.Date); }

    public DatePickerField()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        if (DateText is null) return;
        var d = DateOnly.FromDateTime(Date);
        DateText.Text = DateUtils.FormatDate(DateUtils.ToMillis(d));
        Picker.SelectedDate = Date;
        Picker.DisplayDate = Date;
    }

    private void Field_Click(object sender, MouseButtonEventArgs e) => CalendarPopup.IsOpen = !CalendarPopup.IsOpen;

    private void Picker_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Picker.SelectedDate is { } picked && picked.Date != Date)
        {
            Date = picked;
        }
        CalendarPopup.IsOpen = false;
    }

    // WPF's Calendar keeps mouse capture after a date is picked, which otherwise swallows the
    // next click on a month/year header button. Standard workaround: release it.
    private void Picker_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured is CalendarItem) Mouse.Capture(null);
    }
}
