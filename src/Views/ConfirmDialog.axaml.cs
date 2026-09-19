using Avalonia.Controls;
using Avalonia.Interactivity;

namespace filestore.Views;

/// <summary>A small Yes/No dialog. No, or closing the window, returns false.</summary>
public partial class ConfirmDialog : DialogWindow
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static Task<bool> AskAsync(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        return dialog.ShowModalAsync<bool>(owner);
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => CloseWith(true);

    private void No_Click(object? sender, RoutedEventArgs e) => Close();
}
