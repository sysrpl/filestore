using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

public partial class MainWindow : Window
{
    private readonly ProfileService _profiles = null!;
    private readonly S3BrowserSource _s3Source = null!;
    private readonly TransferService _transfers = null!;
    private readonly SettingsService _settings = null!;

    // The profile the S3 pane is showing; when the active profile changes, the pane reloads.
    private Profile? _s3Profile;

    // Set while a transfer is running.
    private CancellationTokenSource? _transferCts;
    private string _transferVerb = "";

    // The pane Copy, Paste and Refresh act on; it has a highlight border.
    private BrowserPane _activePane = null!;

    // What Edit > Copy last copied. ClipboardText is what was put on the system clipboard,
    // so Paste can tell whether something newer (e.g. from another program) replaced it.
    private (BrowserPane Source, IReadOnlyList<BrowserItem> Items, string ClipboardText)? _copied;

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

    private Task OpenProfilesDialog() => new ProfilesWindow(_profiles).ShowDialog(this);

    private async void Preferences_Click(object? sender, RoutedEventArgs e) =>
        await new PreferencesWindow(_settings).ShowDialog(this);

    private async void About_Click(object? sender, RoutedEventArgs e) =>
        await new AboutWindow().ShowDialog(this);

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

    private async void ManageProfiles_Click(object? sender, RoutedEventArgs e) => await OpenProfilesDialog();

    private void Refresh_Click(object? sender, RoutedEventArgs e) => _ = _activePane.RefreshAsync();

    private void PublicToggle_Changed(object? sender, RoutedEventArgs e) => UpdatePublicToggle();

    /// <summary>Locked icon for private uploads, unlocked for public.</summary>
    private void UpdatePublicToggle()
    {
        var isPublic = PublicToggle.IsChecked == true;
        PublicToggle.Content = isPublic ? Icons.LockOpenVariant : Icons.Lock;
        if (isPublic)
            Tip.Set(PublicToggle, "Uploads are public", "Anyone with the link can read files you upload (public-read ACL). Click to make uploads private.");
        else
            Tip.Set(PublicToggle, "Uploads are private", "Only people with your AWS keys can read files you upload. Click to make uploads public.");
    }

    private void SetActivePane(BrowserPane pane)
    {
        _activePane = pane;
        LocalPane.IsActive = pane == LocalPane;
        S3Pane.IsActive = pane == S3Pane;
        UpdateToolbar();
    }

    // ---- New folder, rename, delete -------------------------------------------

    /// <summary>The active pane's selection, minus buckets (which can't be renamed or deleted here).</summary>
    private List<BrowserItem> ModifiableSelection() =>
        _activePane.SelectedItems.Where(i => i.Kind != BrowserItemKind.Bucket).ToList();

    private void NewFolder_Click(object? sender, RoutedEventArgs e) => _ = NewFolderAsync();

    private void NewBucket_Click(object? sender, RoutedEventArgs e) => _ = NewBucketAsync();

    private bool ShowingBucketList() =>
        _activePane == S3Pane && S3Pane.CurrentPath == S3BrowserSource.Root;

    private async Task NewBucketAsync()
    {
        if (_profiles.ActiveProfile is null)
        {
            Log("Add an AWS profile first (Profiles > Manage Profiles...).", isError: true);
            return;
        }

        var choice = await NewBucketDialog.AskAsync(this, _s3Source.DefaultRegion ?? "us-east-1");
        if (choice is not { } bucket)
            return;

        var how = bucket.AllowPublic ? " (public files allowed)" : "";
        await RunOperationAsync(S3Pane, "Creating bucket",
        [
            new(bucket.Name, $"Created bucket {bucket.Name} in {bucket.Region}{how}",
                ct => _s3Source.CreateBucketAsync(bucket.Name, bucket.Region, bucket.AllowPublic, ct)),
        ]);
    }

    private void Rename_Click(object? sender, RoutedEventArgs e) => _ = RenameAsync();

    private void Delete_Click(object? sender, RoutedEventArgs e) => _ = DeleteAsync();

    private async Task NewFolderAsync()
    {
        if (ShowingBucketList())
        {
            await NewBucketAsync();
            return;
        }

        var pane = _activePane;
        if (pane.Source is not { } source || pane.CurrentPath is not { } folder)
            return;
        if (!source.CanModify(folder))
        {
            Log("Open a bucket first: folders can't be created in the bucket list.", isError: true);
            return;
        }

        var name = await TextInputDialog.AskAsync(this, "New Folder", $"Name of the new folder in {folder}:",
            "New folder", "Create", source.ValidateName);
        if (name is null)
            return;

        await RunOperationAsync(pane, "Creating folder",
        [
            new(name, $"Created folder {name} in {folder}", ct => source.CreateFolderAsync(folder, name, ct)),
        ]);
    }

    private async Task RenameAsync()
    {
        var pane = _activePane;
        var selected = ModifiableSelection();
        if (pane.Source is not { } source || selected.Count != 1)
        {
            Log("Select one file or folder to rename.", isError: true);
            return;
        }

        var item = selected[0];
        // Select just the name, not the extension, like most file managers.
        var dot = item.Kind == BrowserItemKind.File ? item.Name.LastIndexOf('.') : -1;
        var newName = await TextInputDialog.AskAsync(this, "Rename", $"New name for {item.Name}:",
            item.Name, "Rename", source.ValidateName, dot > 0 ? dot : null);
        if (newName is null || newName == item.Name)
            return;

        await RunOperationAsync(pane, "Renaming",
        [
            new(item.Name, $"Renamed {item.Path} to {newName}", ct => source.RenameAsync(item, newName, ct)),
        ]);
    }

    private List<BrowserItem> SelectedBuckets() =>
        S3Pane.SelectedItems.Where(i => i.Kind == BrowserItemKind.Bucket).ToList();

    private async Task DeleteAsync()
    {
        if (ShowingBucketList())
        {
            await DeleteBucketAsync();
            return;
        }

        var pane = _activePane;
        var items = ModifiableSelection();
        if (pane.Source is not { } source || items.Count == 0)
        {
            Log("Select files or folders to delete.", isError: true);
            return;
        }

        var what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"these {items.Count} items";
        var where = pane == S3Pane ? "from S3" : "from this computer (they are not moved to the trash)";
        var folders = items.Any(i => i.IsContainer) ? " Folders are deleted with everything in them." : "";
        var confirmed = await ConfirmDialog.AskAsync(this, "Delete",
            $"Permanently delete {what} {where}?{folders}\n\nThis can't be undone.");
        if (!confirmed)
            return;

        await RunOperationAsync(pane, "Deleting",
            items.Select(item => new FileOperation(item.Name, $"Deleted {item.Path}",
                ct => source.DeleteAsync(item, ct))).ToList());
    }

    // Counting stops here, so the warning appears quickly even for huge buckets.
    private const int BucketCountLimit = 10000;

    /// <summary>
    /// Deletes one selected bucket after a warning that lists what's in it and what uses it,
    /// and asks for the bucket name to be typed.
    /// </summary>
    private async Task DeleteBucketAsync()
    {
        var buckets = SelectedBuckets();
        if (buckets.Count != 1)
        {
            Log("Select one bucket to delete.", isError: true);
            return;
        }
        if (_transferCts is not null)
        {
            Log("A transfer is already running. Wait for it to finish or cancel it.", isError: true);
            return;
        }

        var bucket = buckets[0].Name;
        StatusText.Text = $"Checking what's in {bucket}...";
        var warnings = new List<string>();
        try
        {
            var contents = await _s3Source.SummarizeBucketAsync(bucket, BucketCountLimit, CancellationToken.None);
            var more = contents.Incomplete ? "at least " : "";
            warnings.Add(contents.Files == 0 && contents.OlderVersions == 0 && !contents.Incomplete
                ? "The bucket is empty."
                : $"It holds {more}{Plural(contents.Files, "file")} ({more}{BrowserItem.FormatSize(contents.Bytes)}); all of them will be deleted.");
            if (contents.OlderVersions > 0)
                warnings.Add($"{more}{Plural(contents.OlderVersions, "older version")} of files will be deleted too, so nothing can be restored.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Couldn't check what's in it ({S3BrowserSource.DescribeError(ex)}). Everything in it will be deleted.");
        }

        try
        {
            if (await _s3Source.GetDistributionDomainAsync(bucket, CancellationToken.None) is { } domain)
                warnings.Add($"The CloudFront distribution {domain} serves this bucket; it will stop working.");
        }
        catch (Exception)
        {
            // Couldn't check CloudFront; not worth blocking the warning for.
        }

        warnings.Add("Websites, apps and links that use this bucket will stop working.");
        warnings.Add("Once deleted, the bucket name can be taken by any other AWS account.");
        warnings.Add("This can't be undone.");
        StatusText.Text = "";

        if (!await DeleteBucketDialog.AskAsync(this, bucket, warnings))
            return;

        await RunOperationAsync(S3Pane, "Deleting bucket",
        [
            new(bucket, $"Deleted bucket {bucket} and everything in it", ct => _s3Source.DeleteBucketAsync(bucket,
                deleted => StatusText.Text = $"Deleting bucket {bucket}: {deleted:N0} objects deleted", ct)),
        ]);
    }

    /// <summary>One step of a file operation: a name for progress, a log message on success, and the work.</summary>
    private sealed record FileOperation(string Name, string DoneMessage, Func<CancellationToken, Task> Run);

    /// <summary>
    /// Runs file operation steps one after another, with progress and Cancel in the status bar and
    /// each result in the log. A failed step is logged and the rest still run. The pane reloads after.
    /// </summary>
    private async Task RunOperationAsync(BrowserPane pane, string verb, IReadOnlyList<FileOperation> steps)
    {
        if (_transferCts is not null)
        {
            Log("A transfer is already running. Wait for it to finish or cancel it.", isError: true);
            return;
        }

        var cts = _transferCts = new CancellationTokenSource();
        SetTransferring(true);
        TransferProgressBar.IsIndeterminate = steps.Count == 1;
        var failed = 0;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                TransferProgressBar.Value = (double)i / steps.Count;
                StatusText.Text = steps.Count == 1 ? $"{verb}: {step.Name}" : $"{verb} {i + 1} of {steps.Count}: {step.Name}";
                try
                {
                    await step.Run(cts.Token);
                    Log(step.DoneMessage);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    Log($"{verb} {step.Name} failed: {S3BrowserSource.DescribeError(ex)}", isError: true);
                }
            }

            if (steps.Count > 1)
            {
                Log($"Finished: {steps.Count - failed} of {steps.Count} done" + (failed > 0 ? $", {failed} failed" : ""),
                    isError: failed > 0);
            }
        }
        catch (OperationCanceledException)
        {
            Log($"{verb} cancelled.", isError: true);
        }
        finally
        {
            _transferCts = null;
            cts.Dispose();
            SetTransferring(false);
            _ = pane.RefreshAsync();
        }
    }

    // ---- Public / private ----------------------------------------------------

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

        var files = _activePane == S3Pane ? SelectedS3Files() : new List<BrowserItem>();
        AccessToolGroup.IsVisible = files.Count > 0;
        CopyUrlToolButton.IsVisible = files.Any(f => f.ShareUrl is not null);
        ShareLinkToolButton.IsVisible = files.Any(f => f.Access != ObjectAccess.Public);
    }

    /// <summary>
    /// Opens an S3 file's link in the default browser - the same link Copy URL gives: CloudFront
    /// when a distribution serves the bucket, otherwise the S3 URL of a public file.
    /// </summary>
    private async Task OpenInBrowserAsync(BrowserItem file)
    {
        if (file.ShareUrl is not { } url)
        {
            var reason = file.Access == ObjectAccess.NotChecked
                ? "its access is still being checked; try again in a moment"
                : "it's private and no CloudFront distribution serves the bucket";
            Log($"Can't open {file.Name} in the browser: {reason}.", isError: true);
            return;
        }

        try
        {
            if (await Launcher.LaunchUriAsync(new Uri(url)))
                Log($"Opened in browser: {url}");
            else
                Log($"Couldn't open a browser for {url}", isError: true);
        }
        catch (Exception ex)
        {
            Log($"Couldn't open a browser for {url}: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Asks how long the link should work, then creates a temporary (presigned) link for each
    /// selected file and copies them to the clipboard, one per line.
    /// </summary>
    private async void ShareLink_Click(object? sender, RoutedEventArgs e)
    {
        var files = SelectedS3Files();
        if (files.Count == 0)
            return;

        if (await ShareLinkDialog.AskAsync(this, files) is not { } validFor)
            return;

        var expires = DateTime.Now + validFor;
        var urls = new List<string>();
        foreach (var file in files)
        {
            try
            {
                urls.Add(await _s3Source.CreateTemporaryUrlAsync(file.Path, validFor, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Log($"Couldn't create a temporary link for {file.Path}: {S3BrowserSource.DescribeError(ex)}", isError: true);
            }
        }
        if (urls.Count == 0)
            return;

        try
        {
            if (Clipboard is { } clipboard)
                await ClipboardExtensions.SetTextAsync(clipboard, string.Join("\n", urls));
        }
        catch (Exception ex)
        {
            Log($"Couldn't copy to the clipboard: {ex.Message}", isError: true);
            return;
        }

        var until = $"{expires:ddd d MMM yyyy, HH:mm}";
        Log(urls.Count == 1
            ? $"Copied a temporary link to {files[0].Name}, valid until {until}: {urls[0]}"
            : $"Copied {urls.Count} temporary links, valid until {until}");
    }

    /// <summary>Puts the selected files' links on the clipboard, one per line.</summary>
    private async void CopyUrl_Click(object? sender, RoutedEventArgs e)
    {
        var files = SelectedS3Files();
        var urls = files.Select(f => f.ShareUrl).OfType<string>().ToList();
        if (urls.Count == 0)
            return;

        try
        {
            if (Clipboard is { } clipboard)
                await ClipboardExtensions.SetTextAsync(clipboard, string.Join("\n", urls));
        }
        catch (Exception ex)
        {
            Log($"Couldn't copy to the clipboard: {ex.Message}", isError: true);
            return;
        }

        Log(urls.Count == 1 ? $"Copied URL: {urls[0]}" : $"Copied {urls.Count} URLs, starting with {urls[0]}");

        var skipped = files.Count - urls.Count;
        if (skipped > 0)
        {
            var reason = _s3Source.CloudFrontError is { } error
                ? $" CloudFront couldn't be checked: {error}."
                : "";
            Log($"No URL for {Plural(skipped, "selected file")}: private in S3, and no CloudFront distribution serves this bucket with plain links.{reason}",
                isError: true);
        }
    }

    private async void ChangeAccess_Click(object? sender, RoutedEventArgs e)
    {
        var files = SelectedS3Files();
        if (files.Count == 0)
            return;

        // Warn up front when the bucket's own settings make "public" impossible.
        string? publicBlocker = null;
        try
        {
            publicBlocker = await _s3Source.GetPublicAclBlockerAsync(
                S3BrowserSource.SplitPath(files[0].Path).Bucket, CancellationToken.None);
        }
        catch (Exception)
        {
            // Couldn't check; S3 will say so if making files public fails.
        }

        var choice = await AccessWindow.AskAsync(this, S3Pane.CurrentPath ?? "S3", files, publicBlocker);
        if (choice is not { } picked)
            return;

        var access = picked == AccessChoice.MakePrivate ? UploadAccess.Private : UploadAccess.PublicRead;
        await ChangeAccessAsync(files, access, allowPublicFirst: picked == AccessChoice.AllowPublicThenMakePublic);
    }

    /// <summary>Sets each file's ACL, with progress in the status bar and results in the log.</summary>
    /// <param name="allowPublicFirst">Change the bucket's settings so public ACLs work before starting.</param>
    private async Task ChangeAccessAsync(List<BrowserItem> files, UploadAccess access, bool allowPublicFirst)
    {
        if (_transferCts is not null)
        {
            Log("A transfer is already running. Wait for it to finish or cancel it.", isError: true);
            return;
        }

        var label = access == UploadAccess.PublicRead ? "public" : "private";
        var cts = _transferCts = new CancellationTokenSource();
        SetTransferring(true);
        TransferProgressBar.IsIndeterminate = false;
        Log($"Making {Plural(files.Count, "file")} {label}");

        var changed = 0;
        var failed = 0;
        try
        {
            if (allowPublicFirst)
            {
                var bucket = S3BrowserSource.SplitPath(files[0].Path).Bucket;
                StatusText.Text = $"Allowing public files in {bucket}...";
                try
                {
                    await _s3Source.AllowPublicAclsAsync(bucket, cts.Token);
                    Log($"Allowed public files in bucket {bucket}: ACLs turned on, public ACLs no longer blocked");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"Couldn't change bucket {bucket}'s settings: {S3BrowserSource.DescribeError(ex)}", isError: true);
                    return;
                }
            }

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                TransferProgressBar.Value = (double)i / files.Count;
                StatusText.Text = $"Making {label} {i + 1} of {files.Count}: {file.Name}";
                try
                {
                    await _s3Source.SetObjectAccessAsync(file.Path, access, cts.Token);
                    changed++;
                    Log($"Made {label}: {file.Path}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    var reason = access == UploadAccess.PublicRead
                        ? S3BrowserSource.DescribePublicAclError(ex)
                        : S3BrowserSource.DescribeError(ex);
                    Log($"Couldn't make {file.Path} {label}: {reason}", isError: true);
                }
            }

            Log($"Finished: made {Plural(changed, "file")} {label}" + (failed > 0 ? $", {failed} failed" : ""),
                isError: failed > 0);
        }
        catch (OperationCanceledException)
        {
            Log($"Cancelled after making {Plural(changed, "file")} {label}.", isError: true);
        }
        finally
        {
            _transferCts = null;
            cts.Dispose();
            SetTransferring(false);
            // Show the real result: a public bucket policy, for example, overrides a private ACL.
            _ = S3Pane.RecheckDetailsAsync(files);
        }
    }

    private string PaneName(BrowserPane pane) => pane == LocalPane ? "Local" : "Amazon S3";

    // ---- Copy and paste ----------------------------------------------------

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
            (KeyModifiers.None, Key.F2) => RenameAsync,
            (KeyModifiers.None, Key.Delete) => DeleteAsync,
            _ => null,
        };
        if (action is null)
            return;

        e.Handled = true;
        _ = action();
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => _ = CopyAsync();

    private void Paste_Click(object? sender, RoutedEventArgs e) => _ = PasteAsync();

    /// <summary>
    /// Remembers the active pane's selection for Paste. Local files are also put on the system
    /// clipboard as files (so they can be pasted into the file manager); otherwise the paths go
    /// on as text.
    /// </summary>
    private async Task CopyAsync()
    {
        var pane = _activePane;
        var items = pane.SelectedItems;
        if (items.Count == 0)
        {
            Log($"Select files or folders in the {PaneName(pane)} pane to copy.", isError: true);
            return;
        }

        var text = string.Join("\n", items.Select(i => i.Path));
        _copied = (pane, items, text);

        if (Clipboard is { } clipboard)
        {
            try
            {
                var storageItems = pane == LocalPane ? await ToStorageItemsAsync(items) : null;
                if (storageItems is not null)
                    await clipboard.SetFilesAsync(storageItems);
                else
                    await ClipboardExtensions.SetTextAsync(clipboard, text);
            }
            catch (Exception)
            {
                // The system clipboard isn't available; copy and paste inside this window still works.
            }
        }

        var other = pane == LocalPane ? "Amazon S3" : "Local";
        Log($"Copied {Plural(items.Count, "item")} from the {PaneName(pane)} pane. " +
            $"Click in the {other} pane and paste (Ctrl+V) to {(pane == LocalPane ? "upload" : "download")}.");
    }

    /// <summary>
    /// Pastes into the active pane's folder: into S3, files from the system clipboard (copied here
    /// or in the file manager) are uploaded; into Local, S3 items copied here are downloaded.
    /// </summary>
    private async Task PasteAsync()
    {
        var target = _activePane;

        if (target == S3Pane)
        {
            var files = new List<BrowserItem>();
            try
            {
                if (Clipboard is { } clipboard)
                    files = StorageItems.ToLocalItems(await clipboard.TryGetFilesAsync());
            }
            catch (Exception)
            {
                // Clipboard unreadable; fall back to what was copied in this window.
            }

            if (files.Count > 0)
            {
                await RunTransferAsync(TransferDirection.Upload, files);
                return;
            }
            if (_copied is { } copied && copied.Source == LocalPane && await ClipboardStillHoldsAsync(copied.ClipboardText))
            {
                await RunTransferAsync(TransferDirection.Upload, copied.Items);
                return;
            }
        }
        else if (_copied is { } copied && copied.Source == S3Pane && await ClipboardStillHoldsAsync(copied.ClipboardText))
        {
            await RunTransferAsync(TransferDirection.Download, copied.Items);
            return;
        }

        Log(target == S3Pane
            ? "Nothing to paste. Copy files in the Local pane or your file manager, then paste here to upload."
            : "Nothing to paste. Copy files in the Amazon S3 pane, then paste here to download.",
            isError: true);
    }

    /// <summary>True unless something else was copied to the system clipboard since our Copy.</summary>
    private async Task<bool> ClipboardStillHoldsAsync(string text)
    {
        if (Clipboard is not { } clipboard)
            return true;
        try
        {
            var current = await ClipboardExtensions.TryGetTextAsync(clipboard);
            return current is not null && Normalize(current) == Normalize(text);
        }
        catch (Exception)
        {
            return true;
        }

        static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();
    }

    /// <summary>The local items as storage items for the clipboard, or null if any can't be found.</summary>
    private async Task<List<IStorageItem>?> ToStorageItemsAsync(IReadOnlyList<BrowserItem> items)
    {
        var result = new List<IStorageItem>();
        foreach (var item in items)
        {
            var uri = new Uri(item.Path, UriKind.Absolute);
            IStorageItem? storageItem = item.IsContainer
                ? await StorageProvider.TryGetFolderFromPathAsync(uri)
                : await StorageProvider.TryGetFileFromPathAsync(uri);
            if (storageItem is null)
                return null;
            result.Add(storageItem);
        }
        return result;
    }

    private async void Upload_Click(object? sender, RoutedEventArgs e) =>
        await RunTransferAsync(TransferDirection.Upload, LocalPane.SelectedItems);

    private async void Download_Click(object? sender, RoutedEventArgs e) =>
        await RunTransferAsync(TransferDirection.Download, S3Pane.SelectedItems);

    private async Task RunTransferAsync(TransferDirection direction, IReadOnlyList<BrowserItem> items)
    {
        var upload = direction == TransferDirection.Upload;
        var destination = upload ? S3Pane : LocalPane;

        if (_transferCts is not null)
        {
            Log("A transfer is already running. Wait for it to finish or cancel it.", isError: true);
            return;
        }
        if (items.Count == 0)
        {
            Log($"Select files or folders in the {(upload ? "Local" : "Amazon S3")} pane first.", isError: true);
            return;
        }
        if (destination.CurrentPath is not { } destinationPath)
        {
            Log("The destination pane hasn't loaded a folder yet.", isError: true);
            return;
        }

        var access = PublicToggle.IsChecked == true ? UploadAccess.PublicRead : UploadAccess.Private;
        _transferVerb = upload ? "Uploading" : "Downloading";
        var cts = _transferCts = new CancellationTokenSource();
        SetTransferring(true);
        StatusText.Text = "Preparing...";

        try
        {
            var plan = upload
                ? await _transfers.PlanUploadAsync(items, destinationPath, cts.Token)
                : await _transfers.PlanDownloadAsync(items, destinationPath, cts.Token);

            if (plan.Files.Count == 0)
            {
                Log("Nothing to copy: the selected folders are empty.");
                return;
            }

            var overwrite = true;
            if (plan.ExistingCount > 0)
            {
                overwrite = await ConfirmDialog.AskAsync(this, "Files already exist",
                    $"{plan.ExistingCount} of the {plan.Files.Count} files already exist at the destination. " +
                    "Overwrite them?\n\nYes overwrites them. No skips them and copies the rest.");
            }

            var toCopy = overwrite ? plan.Files : plan.Files.Where(f => !f.Exists).ToList();
            Log($"{_transferVerb} {Plural(toCopy.Count, "file")} " +
                $"({BrowserItem.FormatSize(toCopy.Sum(f => f.Size))}) to {destinationPath}"
                + (upload ? (access == UploadAccess.PublicRead ? " as public" : " as private") : ""));
            if (!overwrite)
            {
                foreach (var file in plan.Files.Where(f => f.Exists))
                    Log($"Skipped (already exists): {Describe(plan, file)}");
            }

            var progress = new Progress<TransferProgress>(ShowProgress);
            var done = upload ? "Uploaded" : "Downloaded";
            var result = await _transfers.RunAsync(plan, overwrite, access, progress,
                file => Log($"{done} {Describe(plan, file)} ({BrowserItem.FormatSize(file.Size)})"),
                cts.Token);

            Log($"Finished: {done.ToLowerInvariant()} {Plural(result.Copied, "file")} " +
                $"({BrowserItem.FormatSize(result.Bytes)})"
                + (result.Skipped > 0 ? $", skipped {result.Skipped} existing" : ""));
        }
        catch (OperationCanceledException)
        {
            Log("Transfer cancelled.", isError: true);
        }
        catch (Exception ex)
        {
            Log($"Transfer failed: {ex.Message}", isError: true);
        }
        finally
        {
            _transferCts = null;
            cts.Dispose();
            SetTransferring(false);
            _ = destination.RefreshAsync();
        }
    }

    /// <summary>"local/path → s3://bucket/key" or the reverse, for the log.</summary>
    private static string Describe(TransferPlan plan, TransferFile file)
    {
        var s3Path = $"{S3BrowserSource.Root}{plan.Bucket}/{file.Key}";
        return plan.Direction == TransferDirection.Upload
            ? $"{file.LocalPath} → {s3Path}"
            : $"{s3Path} → {file.LocalPath}";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

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

    private void SetTransferring(bool running)
    {
        UploadButton.IsEnabled = !running;
        DownloadButton.IsEnabled = !running;
        UploadToolButton.IsEnabled = !running;
        DownloadToolButton.IsEnabled = !running;
        AccessToolButton.IsEnabled = !running;
        ShareLinkToolButton.IsEnabled = !running;
        // Switching profile mid-transfer would make the S3 pane show a different account.
        ProfilesMenu.IsEnabled = !running;
        ProfilesToolButton.IsEnabled = !running;

        TransferProgressBar.IsVisible = running;
        TransferProgressBar.IsIndeterminate = running;  // until the first progress report
        TransferProgressBar.Value = 0;
        CancelTransferButton.IsVisible = running;
        CancelTransferButton.IsEnabled = running;
    }

    private void ShowProgress(TransferProgress p)
    {
        if (_transferCts is null)
            return; // a late report after the transfer finished

        TransferProgressBar.IsIndeterminate = false;
        TransferProgressBar.Value = p.BytesTotal > 0
            ? (double)p.BytesDone / p.BytesTotal
            : (double)(p.FileNumber - 1) / p.FileCount;

        StatusText.Text = $"{_transferVerb} {p.FileNumber} of {p.FileCount}: {p.FileName}  —  " +
            $"{BrowserItem.FormatSize(p.BytesDone)} of {BrowserItem.FormatSize(p.BytesTotal)}";
    }

    private void CancelTransfer_Click(object? sender, RoutedEventArgs e)
    {
        _transferCts?.Cancel();
        CancelTransferButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
}
