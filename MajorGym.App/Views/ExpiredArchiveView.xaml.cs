using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MajorGym.App.ViewModels;

namespace MajorGym.App.Views;

public partial class ExpiredArchiveView : UserControl
{
    public ExpiredArchiveView()
    {
        InitializeComponent();
        // Keyboard shortcuts (Ctrl+A / Esc / Delete) need the view to hold focus when nothing else does.
        Loaded += (_, _) => { if (!IsKeyboardFocusWithin) Focus(); };
    }

    private static bool Shift => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
    private static bool Ctrl => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private static ArchivedMemberRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ArchivedMemberRow;

    /// <summary>Click on the card body: open the member, or (Shift/Ctrl held, or a selection under way) select.</summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (RowOf(sender) is { } row && DataContext is ExpiredArchiveViewModel vm)
        {
            vm.ClickCard(row, Shift, Ctrl);
            e.Handled = true;
            Focus(); // keep Ctrl+A / Delete working after a click
        }
    }

    /// <summary>Click on the tick box: always selects (toggle, or a Shift range) and never opens the member.</summary>
    private void OnTickClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { } row && DataContext is ExpiredArchiveViewModel vm)
        {
            vm.ClickRow(row, Shift, toggle: !Shift || Ctrl);
            e.Handled = true;
            Focus();
        }
    }
}
