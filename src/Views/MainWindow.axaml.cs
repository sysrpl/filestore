using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

/// <summary>
/// The main window: the Local and Amazon S3 panes, toolbar, status bar and activity log.
/// The commands live in the other MainWindow.*.cs files: FileOperations (new folder, rename,
/// delete, buckets), Transfers (upload and download), Clipboard (copy and paste) and S3Links
/// (public / private, links).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ProfileService _profiles = null!;
    private readonly S3BrowserSource _s3Source = null!;
    private readonly TransferService _transfers = null!;
    private readonly SettingsService _settings = null!;
    private readonly SearchHistory _searchHistory = new();

    // The profile the S3 pane is showing; when the active profile changes, the pane reloads.
    private Profile? _s3Profile;

    // Set while a transfer or other operation is running (see BeginOperation).
    private CancellationTokenSource? _transferCts;

    // The pane Copy, Paste and Refresh act on; it has a highlight border.
    private BrowserPane _activePane = null!;

    // Activity log shown at the bottom; oldest entries are dropped past the limit.
    private const int MaxLogEntries = 5000;
    private readonly ObservableCollection<LogEntry> _log = new();

    // Needed by the XAML designer.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ProfileService profiles, SettingsService settings, string? loadError) : this()
    {
        _profiles = profiles;
        _settings = settings;
        _settings.Changed += (_, _) => ApplySettings();
        ApplySettings();
        _s3Source = new S3BrowserSource(profiles);
        _transfers = new TransferService(_s3Source);
        _s3Profile = profiles.ActiveProfile;

        LocalPane.Initialize(new LocalFileSource(), "Local", Icons.Harddisk, showS3Columns: false);
        S3Pane.Initialize(_s3Source, "Amazon S3", Icons.CloudOutline, showS3Columns: true);
        ActivityList.ItemsSource = _log;

        // Drag and drop: S3 rows onto the local pane download; local rows, or files from
        // the file manager, onto the S3 pane upload.
        LocalPane.AcceptsDropsFrom = S3Pane;
        S3Pane.AcceptsDropsFrom = LocalPane;
        S3Pane.AcceptsExternalFiles = true;
        LocalPane.ItemsDropped += async (_, items) => await RunTransferAsync(TransferDirection.Download, items);
        S3Pane.ItemsDropped += async (_, items) => await RunTransferAsync(TransferDirection.Upload, items);

        LocalPane.Activated += (_, _) => SetActivePane(LocalPane);
        S3Pane.Activated += (_, _) => SetActivePane(S3Pane);
        LocalPane.SelectionChanged += (_, _) => UpdateToolbar();
        S3Pane.SelectionChanged += (_, _) => UpdateToolbar();
        S3Pane.DetailsLoaded += (_, _) => UpdateToolbar();
        S3Pane.Navigated += (_, _) => UpdateToolbar();
        S3Pane.FileOpened += async (_, file) => await OpenInBrowserAsync(file);
        SetActivePane(LocalPane);
        UpdatePublicToggle();

        // Tunnel so Ctrl+C / Ctrl+V reach us before the file list's own copy handling.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

        // Handle after the current click finishes, since the click may come from the menu being rebuilt.
        _profiles.Changed += (_, _) => Dispatcher.UIThread.Post(OnProfilesChanged);
        RefreshProfiles();

        if (loadError is not null)
            Log(loadError, isError: true);

        Opened += async (_, _) =>
        {
            _ = LocalPane.GoHomeAsync();
            _ = S3Pane.GoHomeAsync();

            // First run: go straight to the profiles dialog so the user can enter their keys.
            if (loadError is null && _profiles.Profiles.Count == 0)
                await OpenProfilesDialog();
        };
        Closed += (_, _) =>
        {
            _transferCts?.Cancel();
            _s3Source.Dispose();
        };
    }

    // ---- Profiles, preferences, about ---------------------------------------

    private void OnProfilesChanged()
    {
        RefreshProfiles();

        // Editing a profile replaces the object, so this also catches changed keys or region.
        if (!ReferenceEquals(_profiles.ActiveProfile, _s3Profile))
        {
            _s3Profile = _profiles.ActiveProfile;
            _ = S3Pane.GoHomeAsync();
        }
    }

    private void RefreshProfiles()
    {
        var active = _profiles.ActiveProfile;
        ProfilesMenu.Items.Clear();

        foreach (var profile in _profiles.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var id = profile.Id;
            var item = new MenuItem
            {
                // A TextBlock header so underscores in names aren't treated as access keys.
                Header = new TextBlock { Text = profile.Name },
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "profiles",
                IsChecked = id == active?.Id,
            };
            item.Click += (_, _) => SwitchProfile(id);
            ProfilesMenu.Items.Add(item);
        }

        if (_profiles.Profiles.Count == 0)
            ProfilesMenu.Items.Add(new MenuItem { Header = "(no profiles)", IsEnabled = false });

        ProfilesMenu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "_Manage Profiles..." };
        manage.Click += async (_, _) => await OpenProfilesDialog();
        ProfilesMenu.Items.Add(manage);

        ProfileText.Text = active is null
            ? "No AWS profile selected"
            : $"Profile: {active.Name} ({active.Region})";
    }

    private void SwitchProfile(Guid id)
    {
        try
        {
            _profiles.SetActive(id);
        }
        catch (Exception ex)
        {
            Log($"Could not switch profile: {ex.Message}", isError: true);
        }
    }

    private Task OpenProfilesDialog() => new ProfilesWindow(_profiles).ShowModalAsync(this);

    private async void ManageProfiles_Click(object? sender, RoutedEventArgs e) => await OpenProfilesDialog();

    private async void Preferences_Click(object? sender, RoutedEventArgs e) =>
        await new PreferencesWindow(_settings).ShowModalAsync(this);

    private async void About_Click(object? sender, RoutedEventArgs e) =>
        await new AboutWindow().ShowModalAsync(this);

    /// <summary>Applies the preferences to the window (at startup and whenever they're saved).</summary>
    private void ApplySettings()
    {
        var settings = _settings.Settings;
        Toolbar.Classes.Set("noCaptions", !settings.ShowToolbarCaptions);

        // Swapped: S3 on the left, Local on the right.
        Grid.SetColumn(LocalPane, settings.SwapPanes ? 2 : 0);
        Grid.SetColumn(S3Pane, settings.SwapPanes ? 0 : 2);
        // The arrows always point from the source pane to the destination pane.
        UploadButton.Content = settings.SwapPanes ? Icons.ArrowLeftBold : Icons.ArrowRightBold;
        DownloadButton.Content = settings.SwapPanes ? Icons.ArrowRightBold : Icons.ArrowLeftBold;

        // The lock starts at the default; saving preferences resets it to the default too.
        PublicToggle.IsChecked = settings.UploadPublicByDefault;
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    // ---- Panes, selection and toolbar ---------------------------------------

    private void SetActivePane(BrowserPane pane)
    {
        _activePane = pane;
        LocalPane.IsActive = pane == LocalPane;
        S3Pane.IsActive = pane == S3Pane;
        UpdateToolbar();
    }

    private string PaneName(BrowserPane pane) => pane == LocalPane ? "Local" : "Amazon S3";

    private bool ShowingBucketList() =>
        _activePane == S3Pane && S3Pane.CurrentPath == S3BrowserSource.Root;

    private bool S3PaneInBucket() =>
        S3Pane.CurrentPath is { } path && path.Length > S3BrowserSource.Root.Length;

    /// <summary>The active pane's selection, minus buckets (which can't be renamed or deleted here).</summary>
    private List<BrowserItem> ModifiableSelection() =>
        _activePane.SelectedItems.Where(i => i.Kind != BrowserItemKind.Bucket).ToList();

    private List<BrowserItem> SelectedBuckets() =>
        S3Pane.SelectedItems.Where(i => i.Kind == BrowserItemKind.Bucket).ToList();

    private List<BrowserItem> SelectedS3Files() =>
        S3Pane.SelectedItems.Where(i => i.Kind == BrowserItemKind.File).ToList();

    /// <summary>
    /// Enables Rename (one item) and Delete (any items) for the active pane's selection. The
    /// Public / Private button only appears for files selected in the active S3 pane, and Copy URL
    /// only when at least one of them has a link anyone can open.
    /// </summary>
    private void UpdateToolbar()
    {
        // In the bucket list, New Folder becomes New Bucket.
        var bucketList = ShowingBucketList();
        NewFolderToolButton.IsVisible = NewFolderMenuItem.IsVisible = !bucketList;
        NewBucketToolButton.IsVisible = NewBucketMenuItem.IsVisible = bucketList;

        var selected = ModifiableSelection();
        RenameToolButton.IsEnabled = RenameMenuItem.IsEnabled = selected.Count == 1;
        // In the bucket list, Delete deletes one selected bucket.
        DeleteToolButton.IsEnabled = DeleteMenuItem.IsEnabled =
            bucketList ? SelectedBuckets().Count == 1 : selected.Count > 0;

        SearchToolButton.IsVisible = SearchMenuItem.IsEnabled = S3PaneInBucket();

        var files = _activePane == S3Pane ? SelectedS3Files() : new List<BrowserItem>();
        AccessToolGroup.IsVisible = files.Count > 0;
        CopyUrlToolButton.IsVisible = files.Any(f => f.ShareUrl is not null);
        ShareLinkToolButton.IsVisible = files.Any(f => f.Access != ObjectAccess.Public);
        UpdateShareLinkMenuItem();
    }

    private void UpdateShareLinkMenuItem() =>
        ShareLinkMenuItem.IsEnabled = AccessToolGroup.IsVisible && ShareLinkToolButton.IsVisible && ShareLinkToolButton.IsEnabled;

    private void Refresh_Click(object? sender, RoutedEventArgs e) => _ = _activePane.RefreshAsync();

    private void Search_Click(object? sender, RoutedEventArgs e) => _ = SearchAsync();

    /// <summary>
    /// Opens the Search dialog for the folder the S3 pane is in. Show in Explorer there opens the
    /// file's folder in the S3 pane and selects the file.
    /// </summary>
    private async Task SearchAsync()
    {
        if (!S3PaneInBucket() || S3Pane.CurrentPath is not { } folder)
            return;

        var file = await SearchWindow.ShowAsync(this, _s3Source, _searchHistory, folder, _profiles.ActiveProfile?.Id);
        if (file is null)
            return;

        SetActivePane(S3Pane);
        await S3Pane.NavigateAsync(file.Location);
        if (S3Pane.CurrentPath != file.Location || !S3Pane.SelectPath(file.Path))
            Log($"Couldn't show {file.Path}: it's no longer in {file.Location}.", isError: true);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // Leave the keys alone in text boxes (the path boxes), where they edit text.
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        Func<Task>? action = (e.KeyModifiers, e.Key) switch
        {
            (KeyModifiers.Control, Key.C) => CopyAsync,
            (KeyModifiers.Control, Key.V) => PasteAsync,
            (KeyModifiers.Control | KeyModifiers.Shift, Key.N) => NewFolderAsync,
            (KeyModifiers.Control, Key.F) => SearchAsync,
            (KeyModifiers.None, Key.F2) => RenameAsync,
            (KeyModifiers.None, Key.Delete) => DeleteAsync,
            _ => null,
        };
        if (action is null)
            return;

        e.Handled = true;
        _ = action();
    }

    // ---- Running operations -------------------------------------------------

    /// <summary>True, after logging why, when a transfer or other operation is already running.</summary>
    private bool IsBusy()
    {
        if (_transferCts is null)
            return false;
        Log("A transfer is already running. Wait for it to finish or cancel it.", isError: true);
        return true;
    }

    /// <summary>
    /// Starts an operation: shows progress and Cancel in the status bar and disables the commands
    /// that can't run alongside it. Check <see cref="IsBusy"/> first; pair with <see cref="EndOperation"/>.
    /// </summary>
    private CancellationTokenSource BeginOperation()
    {
        var cts = _transferCts = new CancellationTokenSource();
        SetTransferring(true);
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        _transferCts = null;
        cts.Dispose();
        SetTransferring(false);
    }

    private void SetTransferring(bool running)
    {
        UploadButton.IsEnabled = !running;
        DownloadButton.IsEnabled = !running;
        UploadToolButton.IsEnabled = !running;
        DownloadToolButton.IsEnabled = !running;
        AccessToolButton.IsEnabled = !running;
        ShareLinkToolButton.IsEnabled = !running;
        UpdateShareLinkMenuItem();
        // Switching profile mid-transfer would make the S3 pane show a different account.
        ProfilesMenu.IsEnabled = !running;
        ProfilesToolButton.IsEnabled = !running;

        TransferProgressBar.IsVisible = running;
        TransferProgressBar.IsIndeterminate = running;  // until the first progress report
        TransferProgressBar.Value = 0;
        CancelTransferButton.IsVisible = running;
        CancelTransferButton.IsEnabled = running;
    }

    private void CancelTransfer_Click(object? sender, RoutedEventArgs e)
    {
        _transferCts?.Cancel();
        CancelTransferButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }

    // ---- Log and helpers ----------------------------------------------------

    /// <summary>Shows a message in the status line and adds it to the activity log.</summary>
    private void Log(string message, bool isError = false)
    {
        StatusText.Text = message;
        var entry = new LogEntry { Time = DateTime.Now, Message = message, IsError = isError };
        _log.Add(entry);
        while (_log.Count > MaxLogEntries)
            _log.RemoveAt(0);
        ActivityList.ScrollIntoView(entry);
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e) => _log.Clear();

    /// <summary>Puts text on the system clipboard. Logs and returns false if it can't.</summary>
    private async Task<bool> SetClipboardTextAsync(string text)
    {
        try
        {
            if (Clipboard is { } clipboard)
                await ClipboardExtensions.SetTextAsync(clipboard, text);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Couldn't copy to the clipboard: {ex.Message}", isError: true);
            return false;
        }
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
