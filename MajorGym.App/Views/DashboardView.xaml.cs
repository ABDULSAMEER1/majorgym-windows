using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MajorGym.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// WPF's Border has no built-in Command property (unlike Compose's Modifier.clickable
    /// on any element) — this shared handler is the standard WPF workaround: the command
    /// to run is stashed in each Border's Tag and invoked here on click. Used by every
    /// clickable stat card / row in this view.
    /// </summary>
    private void StatCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ICommand command } && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
