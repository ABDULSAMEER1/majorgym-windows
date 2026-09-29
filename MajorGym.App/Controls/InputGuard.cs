using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MajorGym.App.Controls;

public enum InputMode { None, Digits, AsciiLettersAndDigits }

/// <summary>
/// Attached behaviour that reproduces Android's <c>onValueChange = { it.filter { ... } }</c>
/// input filtering on a WPF TextBox: Phone / Due Amount / Fee / Amount Paid accept digits only,
/// ID Proof accepts ASCII letters and digits only (Screens.kt: <c>it.isLetterOrDigit() &amp;&amp;
/// it.code &lt; 128</c>). A character that isn't allowed is never inserted at all (typed or pasted
/// — a paste is filtered down to its allowed characters, like Android's
/// <c>.filter</c>), and <see cref="RejectedCommandProperty"/> fires so a ViewModel can show
/// Android's "Only letters and numbers are allowed." message. Filtering on the input events —
/// rather than by rewriting the bound string inside the ViewModel setter — is deliberate: a
/// TextBox using UpdateSourceTrigger=PropertyChanged does not reliably re-display a value the
/// setter changed while it was still being set.
/// </summary>
public static class InputGuard
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(InputMode), typeof(InputGuard), new PropertyMetadata(InputMode.None, OnModeChanged));

    public static readonly DependencyProperty RejectedCommandProperty = DependencyProperty.RegisterAttached(
        "RejectedCommand", typeof(ICommand), typeof(InputGuard), new PropertyMetadata(null));

    public static InputMode GetMode(DependencyObject d) => (InputMode)d.GetValue(ModeProperty);
    public static void SetMode(DependencyObject d, InputMode v) => d.SetValue(ModeProperty, v);
    public static ICommand? GetRejectedCommand(DependencyObject d) => (ICommand?)d.GetValue(RejectedCommandProperty);
    public static void SetRejectedCommand(DependencyObject d, ICommand? v) => d.SetValue(RejectedCommandProperty, v);

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.PreviewTextInput -= OnPreviewTextInput;
        tb.PreviewKeyDown -= OnPreviewKeyDown;
        DataObject.RemovePastingHandler(tb, OnPaste);
        if ((InputMode)e.NewValue == InputMode.None) return;
        tb.PreviewTextInput += OnPreviewTextInput;
        tb.PreviewKeyDown += OnPreviewKeyDown;
        DataObject.AddPastingHandler(tb, OnPaste);
    }

    private static bool IsAllowed(InputMode mode, char c) => mode switch
    {
        InputMode.Digits => c is >= '0' and <= '9',
        InputMode.AsciiLettersAndDigits => c < 128 && char.IsLetterOrDigit(c),
        _ => true
    };

    private static string Filter(InputMode mode, string text) => new(text.Where(c => IsAllowed(mode, c)).ToArray());

    private static void Reject(TextBox tb)
    {
        var cmd = GetRejectedCommand(tb);
        if (cmd is not null && cmd.CanExecute(null)) cmd.Execute(null);
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var tb = (TextBox)sender;
        var mode = GetMode(tb);
        if (Filter(mode, e.Text).Length != e.Text.Length)
        {
            e.Handled = true;
            Reject(tb);
        }
    }

    // The space bar never raises PreviewTextInput on a TextBox, so it has to be blocked here.
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var tb = (TextBox)sender;
        if (GetMode(tb) == InputMode.None) return;
        e.Handled = true;
        Reject(tb);
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var tb = (TextBox)sender;
        var mode = GetMode(tb);
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "";
        var filtered = Filter(mode, text);
        e.CancelCommand();
        if (filtered.Length != text.Length) Reject(tb);
        if (filtered.Length == 0) return;
        if (tb.MaxLength > 0)
        {
            var room = tb.MaxLength - (tb.Text.Length - tb.SelectionLength);
            if (room <= 0) return;
            if (filtered.Length > room) filtered = filtered[..room];
        }
        var caret = tb.SelectionStart + filtered.Length;
        tb.SelectedText = filtered;
        tb.CaretIndex = caret;
        tb.SelectionLength = 0;
    }
}
