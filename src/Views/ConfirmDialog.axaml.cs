using Avalonia.Controls;
using Avalonia.Interactivity;

namespace filestore.Views;

/// <summary>A small Yes/No dialog. Closing the window counts as No.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static Task<bool> AskAsync(Window owner, string title, string message)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        return dialog.ShowDialog<bool>(owner);
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void No_Click(object? sender, RoutedEventArgs e) => Close(false);
}
