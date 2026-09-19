using Avalonia.Controls;
using Avalonia.Interactivity;

namespace filestore.Views;

/// <summary>Asks for a single line of text (a file or folder name). Returns null when cancelled.</summary>
public partial class TextInputDialog : Window
{
    private Func<string, string?> _validate = _ => null;

    public TextInputDialog()
    {
        InitializeComponent();
    }

    /// <param name="validate">Returns an error message for text that can't be used, or null.</param>
    /// <param name="selectLength">How much of the initial text to select (e.g. the name without its extension).</param>
    public static Task<string?> AskAsync(
        Window owner, string title, string prompt, string initialText, string okText,
        Func<string, string?> validate, int? selectLength = null)
    {
        var dialog = new TextInputDialog { Title = title, _validate = validate };
        dialog.PromptText.Text = prompt;
        dialog.OkButton.Content = okText;
        dialog.InputBox.Text = initialText;
        dialog.Opened += (_, _) =>
        {
            dialog.InputBox.Focus();
            dialog.InputBox.SelectionStart = 0;
            dialog.InputBox.SelectionEnd = selectLength ?? initialText.Length;
        };
        return dialog.ShowDialog<string?>(owner);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (_validate(text) is { } error)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            InputBox.Focus();
            return;
        }
        Close(text);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
