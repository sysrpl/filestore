using Avalonia.Controls;
using Avalonia.Interactivity;
using filestore.Services;
using RegionEndpoint = Amazon.RegionEndpoint;

namespace filestore.Views;

/// <summary>Asks for a new bucket's name and region. Returns null when cancelled.</summary>
public partial class NewBucketDialog : Window
{
    public NewBucketDialog()
    {
        InitializeComponent();
        RegionBox.ItemsSource = RegionEndpoint.EnumerableAllRegions
            .Select(r => r.SystemName)
            .Order()
            .ToList();
    }

    public static Task<(string Name, string Region, bool AllowPublic)?> AskAsync(Window owner, string defaultRegion)
    {
        var dialog = new NewBucketDialog();
        dialog.RegionBox.SelectedItem = defaultRegion;
        dialog.Opened += (_, _) => dialog.NameBox.Focus();
        return dialog.ShowDialog<(string Name, string Region, bool AllowPublic)?>(owner);
    }

    private void Create_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var region = RegionBox.SelectedItem as string;
        var error = S3BrowserSource.ValidateBucketName(name)
            ?? (region is null ? "Choose a region." : null);

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            NameBox.Focus();
            return;
        }
        Close(((string Name, string Region, bool AllowPublic)?)(name, region!, AllowPublicCheck.IsChecked == true));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
