using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MajorGym.App.Views;

public partial class MembersView : UserControl
{
    public MembersView()
    {
        InitializeComponent();
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ICommand command } && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
