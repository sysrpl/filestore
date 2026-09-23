using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

/// <summary>
/// Finds S3 files by name in a folder and its subfolders. Shows the last search again when
/// reopened. Returns the file to show in the S3 pane (Show in Explorer), or null.
/// </summary>
public partial class SearchWindow : DialogWindow
{
    private readonly S3BrowserSource _source = null!;
    private readonly SearchHistory _history = null!;
    private readonly string _folder = "";
    private readonly Guid? _profileId;

    // Set while a search runs; Stop and closing the window cancel it.
    private CancellationTokenSource? _searchCts;

    // Checks the access of the results as they scroll into view; replaced by each search.
    private VisibleAccessLoader? _accessLoader;

    // Needed by the XAML designer.
    public SearchWindow()
    {
        InitializeComponent();
    }

    private SearchWindow(S3BrowserSource source, SearchHistory history, string folder, Guid? profileId) : this()
    {
        _source = source;
        _history = history;
        _folder = folder;
        _profileId = profileId;
        FolderText.Text = $"In {folder} and its subfolders";

        if (history.Last is { } last)
        {
            PatternBox.Text = last.Pattern;
            // Results found with another profile belong to a different AWS account.
            if (last.ProfileId == profileId)
            {
                StartAccessChecks(last.Folder);
                ResultGrid.ItemsSource = last.Results;
                SetStatus(Describe(last), isError: false);
            }
        }

        ResultGrid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is BrowserItem item)
                _accessLoader?.RowShown(item);
        };
        ResultGrid.UnloadingRow += (_, e) =>
        {
            if (e.Row.DataContext is BrowserItem item)
                _accessLoader?.RowHidden(item);
        };

        Opened += (_, _) =>
        {
            PatternBox.Focus();
            PatternBox.SelectAll();
        };
    }

    /// <summary>Shows the dialog for searching <paramref name="folder"/> (an S3 folder inside a bucket).</summary>
    public static Task<BrowserItem?> ShowAsync(
        Window owner, S3BrowserSource source, SearchHistory history, string folder, Guid? profileId) =>
        new SearchWindow(source, history, folder, profileId).ShowModalAsync<BrowserItem?>(owner);

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        _accessLoader?.Dispose();
        base.OnClosed(e);
    }

    private async Task SearchAsync()
    {
        var pattern = PatternBox.Text?.Trim() ?? "";
        if (pattern.Length == 0)
            return;

        var cts = _searchCts = new CancellationTokenSource();
        StartAccessChecks(_folder);
        SetSearching(true);
        SetStatus("Searching...", isError: false);

        var results = new ObservableCollection<BrowserItem>();
        ResultGrid.ItemsSource = results;
        var complete = false;
        string? error = null;
        try
        {
            void OnChecked(int count) =>
                SetStatus($"Searching... looked at {count:N0} files, found {results.Count:N0}", isError: false);

            await foreach (var item in _source.SearchAsync(_folder, new Wildcard(pattern), OnChecked, cts.Token))
                results.Add(item);
            complete = true;
        }
        catch (OperationCanceledException)
        {
            // Stopped: keep what was found so far.
        }
        catch (Exception ex)
        {
            error = S3BrowserSource.DescribeError(ex);
        }
        finally
        {
            _searchCts = null;
            cts.Dispose();
            SetSearching(false);
        }

        var search = new LastSearch
        {
            ProfileId = _profileId,
            Folder = _folder,
            Pattern = pattern,
            Complete = complete,
            Results = results.ToList(),
        };
        _history.Save(search);

        if (error is not null)
            SetStatus($"Search failed: {error}", isError: true);
        else
            SetStatus(Describe(search), isError: false);
    }

    /// <summary>Starts checking access, for the results about to be shown, as their rows come into view.</summary>
    private void StartAccessChecks(string folder)
    {
        _accessLoader?.Dispose();
        _accessLoader = new VisibleAccessLoader(_source, S3BrowserSource.SplitPath(folder).Bucket);
        _accessLoader.Failed += (_, message) => SetStatus(message, isError: true);
    }

    private static string Describe(LastSearch search)
    {
        var count = search.Results.Count;
        var found = count == 1 ? "1 file" : $"{count:N0} files";
        var note = search.Complete ? "" : " (search stopped early)";
        return $"{found} matching \"{search.Pattern}\" in {search.Folder}{note}";
    }

    private void SetSearching(bool searching)
    {
        StopButton.IsVisible = searching;
        SearchProgress.IsVisible = searching;
        PatternBox.IsEnabled = !searching;
        UpdateSearchButton();
    }

    private void UpdateSearchButton()
    {
        // Called during InitializeComponent too, before every control exists.
        if (SearchButton is null || PatternBox is null)
            return;
        SearchButton.IsEnabled = _searchCts is null && !string.IsNullOrWhiteSpace(PatternBox.Text);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Classes.Set("error", isError);
        ToolTip.SetTip(StatusText, text);
    }

    private void Pattern_Changed(object? sender, TextChangedEventArgs e) => UpdateSearchButton();

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        if (_searchCts is null)
            _ = SearchAsync();
    }

    private void Stop_Click(object? sender, RoutedEventArgs e) => _searchCts?.Cancel();

    private void ResultGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ShowInExplorerButton.IsEnabled = ResultGrid.SelectedItem is BrowserItem;

    /// <summary>
    /// Opens a result's link in the default browser - the same link as double-clicking it in the
    /// S3 pane: CloudFront when a distribution serves the bucket, otherwise the S3 URL of a public file.
    /// </summary>
    private async void ResultGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-clicks on the column headers.
        if ((e.Source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is null
            || ResultGrid.SelectedItem is not BrowserItem file)
            return;

        if (file.ShareUrl is not { } url)
        {
            SetStatus(file.Access == ObjectAccess.NotChecked
                ? $"Can't open {file.Name} yet: its access is still being checked."
                : $"Can't open {file.Name} in the browser: it's private and no CloudFront distribution serves the bucket.",
                isError: true);
            return;
        }

        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(url)))
                SetStatus($"Couldn't open a browser for {url}", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open a browser for {url}: {ex.Message}", isError: true);
        }
    }

    private void ShowInExplorer_Click(object? sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is BrowserItem file)
            CloseWith(file);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
