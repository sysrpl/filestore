using Avalonia.Controls;
using Avalonia.Interactivity;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

/// <summary>Asks how long a temporary (presigned) link should work. Returns null when cancelled.</summary>
public partial class ShareLinkDialog : DialogWindow
{
    private const int MinutesIndex = 0;
    private const int HoursIndex = 1;
    private const int DaysIndex = 2;

    public ShareLinkDialog()
    {
        InitializeComponent();
        UnitBox.SelectedIndex = DaysIndex;
        UpdateExpiry();
    }

    public static Task<TimeSpan?> AskAsync(Window owner, IReadOnlyList<BrowserItem> files)
    {
        var dialog = new ShareLinkDialog();
        dialog.HeaderText.Text = files.Count == 1
            ? "Create a temporary link to download this file:"
            : $"Create temporary links to download these {files.Count} files:";
        dialog.FileNamesText.Text = string.Join("\n", files.Select(f => f.Name));
        return dialog.ShowModalAsync<TimeSpan?>(owner);
    }

    /// <summary>The chosen duration, or null if it isn't set.</summary>
    private TimeSpan? Duration()
    {
        if (AmountBox.Value is not { } amount)
            return null;
        var value = (double)amount;
        return UnitBox.SelectedIndex switch
        {
            MinutesIndex => TimeSpan.FromMinutes(value),
            HoursIndex => TimeSpan.FromHours(value),
            DaysIndex => TimeSpan.FromDays(value),
            _ => null,
        };
    }

    private string? Validate(TimeSpan? duration) => duration switch
    {
        null => "Enter how long the link should work.",
        { } d when d < TimeSpan.FromMinutes(1) => "The link must work for at least 1 minute.",
        { } d when d > S3BrowserSource.MaxTemporaryUrlLifetime => "S3 allows temporary links of up to 7 days.",
        _ => null,
    };

    private void UpdateExpiry()
    {
        // Called during InitializeComponent too, before every control exists.
        if (ExpiresText is null || ErrorText is null || CreateButton is null)
            return;

        var duration = Duration();
        var error = Validate(duration);
        ErrorText.Text = error;
        ErrorText.IsVisible = error is not null;
        CreateButton.IsEnabled = error is null;
        ExpiresText.Text = error is null
            ? $"Expires {DateTime.Now + duration!.Value:ddd d MMM yyyy, HH:mm}"
            : "";
    }

    private void Amount_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => UpdateExpiry();

    private void Unit_Changed(object? sender, SelectionChangedEventArgs e) => UpdateExpiry();

    private void Create_Click(object? sender, RoutedEventArgs e)
    {
        var duration = Duration();
        if (Validate(duration) is null)
            CloseWith(duration);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
