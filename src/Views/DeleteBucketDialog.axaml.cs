using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace filestore.Views;

/// <summary>
/// Warns about deleting a bucket and only enables "Delete bucket" once the bucket's name has been
/// typed, like the AWS console. Returns true to delete.
/// </summary>
public partial class DeleteBucketDialog : Window
{
    private string _bucket = "";

    public DeleteBucketDialog()
    {
        InitializeComponent();
        ConfirmBox.TextChanged += (_, _) => DeleteButton.IsEnabled = ConfirmBox.Text == _bucket;
    }

    /// <param name="warnings">One line per consequence (what's in it, what uses it, ...).</param>
    public static Task<bool> AskAsync(Window owner, string bucket, IEnumerable<string> warnings)
    {
        var dialog = new DeleteBucketDialog { _bucket = bucket };
        dialog.HeadingText.Text = $"Permanently delete the bucket \"{bucket}\" and everything in it?";
        foreach (var warning in warnings)
            dialog.WarningList.Children.Add(new TextBlock { Text = "• " + warning, TextWrapping = TextWrapping.Wrap });
        dialog.ConfirmPrompt.Text = $"To confirm, type the bucket name ({bucket}):";
        dialog.Opened += (_, _) => dialog.ConfirmBox.Focus();
        return dialog.ShowDialog<bool>(owner);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (ConfirmBox.Text == _bucket)
            Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
