using Avalonia.Controls;
using Avalonia.Interactivity;
using filestore.Services;

namespace filestore.Views;

/// <summary>Edits the user's <see cref="AppSettings"/> (Edit > Preferences) and saves them on OK.</summary>
public partial class PreferencesWindow : DialogWindow
{
    private readonly SettingsService _settings = null!;

    // Needed by the XAML designer.
    public PreferencesWindow()
    {
        InitializeComponent();
    }

    public PreferencesWindow(SettingsService settings) : this()
    {
        _settings = settings;
        var current = settings.Settings;
        SwapPanesCheck.IsChecked = current.SwapPanes;
        ShowCaptionsCheck.IsChecked = current.ShowToolbarCaptions;
        PublicDefaultRadio.IsChecked = current.UploadPublicByDefault;
        PrivateDefaultRadio.IsChecked = !current.UploadPublicByDefault;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Save(new AppSettings
            {
                SwapPanes = SwapPanesCheck.IsChecked == true,
                ShowToolbarCaptions = ShowCaptionsCheck.IsChecked == true,
                UploadPublicByDefault = PublicDefaultRadio.IsChecked == true,
            });
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Couldn't save the preferences: {ex.Message}";
            ErrorText.IsVisible = true;
            return;
        }
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
