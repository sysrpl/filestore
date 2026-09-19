using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

/// <summary>Dialog for adding, editing, deleting and activating AWS profiles.</summary>
public partial class ProfilesWindow : DialogWindow
{
    private readonly ProfileService _profiles = null!;
    private readonly ObservableCollection<Profile> _items = new();

    // Id of the profile shown in the form. A fresh Id means a new, unsaved profile.
    private Guid _editingId = Guid.NewGuid();

    // Needed by the XAML designer.
    public ProfilesWindow()
    {
        InitializeComponent();
    }

    public ProfilesWindow(ProfileService profiles) : this()
    {
        _profiles = profiles;
        RegionBox.ItemsSource = AwsRegions.Names;
        ProfileList.ItemsSource = _items;
        Reload(profiles.ActiveProfile?.Id);
    }

    private void Reload(Guid? selectId)
    {
        _items.Clear();
        foreach (var profile in _profiles.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            _items.Add(profile);

        var selected = _items.FirstOrDefault(p => p.Id == selectId);
        if (selected is null)
            StartNew();
        else
            ProfileList.SelectedItem = selected;
    }

    private void StartNew()
    {
        ProfileList.SelectedItem = null;
        _editingId = Guid.NewGuid();
        NameBox.Text = "";
        AccessKeyBox.Text = "";
        SecretKeyBox.Text = "";
        RegionBox.SelectedItem = AwsRegions.Default;
        ShowError(null);
        UpdateButtons();
        NameBox.Focus();
    }

    private void ShowProfile(Profile profile)
    {
        _editingId = profile.Id;
        NameBox.Text = profile.Name;
        AccessKeyBox.Text = profile.AccessKeyId;
        SecretKeyBox.Text = profile.SecretAccessKey;
        RegionBox.SelectedItem = profile.Region;
        ShowError(null);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var saved = _profiles.Profiles.Any(p => p.Id == _editingId);
        DeleteButton.IsEnabled = saved;
        SetActiveButton.IsEnabled = saved && _profiles.ActiveProfile?.Id != _editingId;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = message is not null;
    }

    private void ProfileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is Profile profile)
            ShowProfile(profile);
    }

    private void ShowSecret_Changed(object? sender, RoutedEventArgs e)
    {
        SecretKeyBox.RevealPassword = ShowSecretCheck.IsChecked == true;
    }

    private void New_Click(object? sender, RoutedEventArgs e) => StartNew();

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var accessKey = AccessKeyBox.Text?.Trim() ?? "";
        var secretKey = SecretKeyBox.Text?.Trim() ?? "";

        var error =
            name.Length == 0 ? "Enter a friendly name."
            : _profiles.IsNameTaken(name, _editingId) ? $"A profile named \"{name}\" already exists."
            : accessKey.Length == 0 ? "Enter the access key ID."
            : secretKey.Length == 0 ? "Enter the secret access key."
            : null;

        if (error is not null)
        {
            ShowError(error);
            return;
        }

        try
        {
            _profiles.Save(new Profile
            {
                Id = _editingId,
                Name = name,
                AccessKeyId = accessKey,
                SecretAccessKey = secretKey,
                Region = RegionBox.SelectedItem as string ?? AwsRegions.Default,
            });
        }
        catch (Exception ex)
        {
            ShowError($"Could not save profiles: {ex.Message}");
            return;
        }

        Reload(_editingId);
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var profile = _profiles.Profiles.FirstOrDefault(p => p.Id == _editingId);
        if (profile is null)
            return;

        var confirmed = await ConfirmDialog.AskAsync(this, "Delete profile",
            $"Delete the profile \"{profile.Name}\"? This cannot be undone.");
        if (!confirmed)
            return;

        try
        {
            _profiles.Delete(profile.Id);
        }
        catch (Exception ex)
        {
            ShowError($"Could not delete profile: {ex.Message}");
            return;
        }

        Reload(_profiles.ActiveProfile?.Id);
    }

    private void SetActive_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _profiles.SetActive(_editingId);
        }
        catch (Exception ex)
        {
            ShowError($"Could not switch profile: {ex.Message}");
            return;
        }

        UpdateButtons();
    }

    /// <summary>Switches to the profile selected in the list (if it's saved), then closes.</summary>
    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is Profile profile)
        {
            try
            {
                _profiles.SetActive(profile.Id);
            }
            catch (Exception ex)
            {
                ShowError($"Could not switch profile: {ex.Message}");
                return;
            }
        }

        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
