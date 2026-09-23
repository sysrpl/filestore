using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

/// <summary>
/// A file list with Back / Up / Home / Refresh and an editable path.
/// The same control is used for the local pane and the S3 pane; the
/// <see cref="IBrowserSource"/> decides what it shows.
/// </summary>
public partial class BrowserPane : UserControl
{
    // Marks a drag that started in one of our panes. The items themselves are kept in
    // s_activeDrag, since drag data can only carry strings, bytes or files.
    private static readonly DataFormat<string> PaneItemsFormat =
        DataFormat.CreateStringApplicationFormat("filestore-pane-items");
    private static (BrowserPane Source, IReadOnlyList<BrowserItem> Items)? s_activeDrag;

    // How far the pointer must move with the button down before a drag starts.
    private const double DragThreshold = 5;

    private IBrowserSource? _source;
    private string? _currentPath;
    private readonly Stack<string> _back = new();
    private CancellationTokenSource? _loadCts;

    private Point? _dragStart;
    private BrowserItem? _pendingSelect;
    private bool _dragging;

    public BrowserPane()
    {
        InitializeComponent();
        // Tunnel so we see Enter/Backspace before the DataGrid handles them.
        FileGrid.AddHandler(KeyDownEvent, FileGrid_KeyDown, RoutingStrategies.Tunnel);

        // Dragging rows out of the list.
        FileGrid.AddHandler(PointerPressedEvent, FileGrid_PointerPressed, RoutingStrategies.Tunnel);
        FileGrid.AddHandler(PointerMovedEvent, FileGrid_PointerMoved, RoutingStrategies.Tunnel);
        FileGrid.AddHandler(PointerReleasedEvent, FileGrid_PointerReleased, RoutingStrategies.Tunnel);

        // Dropping onto the pane.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Pane_DragOver);
        AddHandler(DragDrop.DropEvent, Pane_Drop);

        FileGrid.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);

        // Clicking or tabbing anywhere in the pane makes it the active pane.
        AddHandler(GotFocusEvent, (_, _) => Activated?.Invoke(this, EventArgs.Empty));
        AddHandler(PointerPressedEvent, (_, _) => Activated?.Invoke(this, EventArgs.Empty),
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Raised when the selected rows change (including when a new folder loads).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when a file (not a folder) is double-clicked or opened with Enter.</summary>
    public event EventHandler<BrowserItem>? FileOpened;

    /// <summary>Raised when the pane has finished opening a folder (successfully or not).</summary>
    public event EventHandler? Navigated;

    /// <summary>Raised when slower details (S3 access, share URLs) have been filled in.</summary>
    public event EventHandler? DetailsLoaded;

    /// <summary>Raised when the user clicks in or moves focus into this pane.</summary>
    public event EventHandler? Activated;

    /// <summary>The active pane is where Copy, Paste and Refresh act; it gets a highlight border.</summary>
    public bool IsActive
    {
        get => Frame.Classes.Contains("active");
        set => Frame.Classes.Set("active", value);
    }

    /// <summary>The pane whose rows may be dropped here (the other pane), or null for none.</summary>
    public BrowserPane? AcceptsDropsFrom { get; set; }

    /// <summary>Whether files dragged in from other programs (e.g. the file manager) may be dropped here.</summary>
    public bool AcceptsExternalFiles { get; set; }

    /// <summary>
    /// Raised when items are dropped on this pane. The first argument is the pane they came from,
    /// or null for files from another program.
    /// </summary>
    public event Action<BrowserPane?, IReadOnlyList<BrowserItem>>? ItemsDropped;

    public string? CurrentPath => _currentPath;

    /// <summary>What this pane browses (local disk or S3).</summary>
    public IBrowserSource? Source => _source;

    public IReadOnlyList<BrowserItem> SelectedItems =>
        FileGrid.SelectedItems.OfType<BrowserItem>().ToList();

    /// <summary>Reloads the current folder (keeps history).</summary>
    public Task RefreshAsync() =>
        _currentPath is null ? Task.CompletedTask : NavigateAsync(_currentPath, addToHistory: false);

    public void Initialize(IBrowserSource source, string title, string icon, bool showS3Columns)
    {
        _source = source;
        TitleText.Text = title;
        TitleIcon.Text = icon;
        foreach (var column in FileGrid.Columns)
        {
            if (column.Header is "Access")
                column.IsVisible = showS3Columns;
        }
        UpdateButtons();
    }

    /// <summary>Clears history and opens the source's home (home folder, or the bucket list).</summary>
    public Task GoHomeAsync() => _source is null ? Task.CompletedTask : ResetAsync(_source.HomePath);

    /// <summary>Clears history and the current list, then opens <paramref name="path"/>.</summary>
    public Task ResetAsync(string path)
    {
        _back.Clear();
        _currentPath = null;
        FileGrid.ItemsSource = null;
        return NavigateAsync(path, addToHistory: false);
    }

    public async Task NavigateAsync(string path, bool addToHistory = true)
    {
        if (_source is null)
            return;

        string target;
        try
        {
            target = _source.NormalizePath(path);
        }
        catch (Exception ex)
        {
            ShowFooterError($"Invalid path: {ex.Message}");
            PathBox.Text = _currentPath;
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        SetFooter("Loading...", isError: false);

        try
        {
            var items = await _source.ListAsync(target, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            if (addToHistory && _currentPath is not null && _currentPath != target)
                _back.Push(_currentPath);
            _currentPath = target;

            FileGrid.ItemsSource = items.Order(BrowserItemComparer.ByName).ToList();
            ShowMessage(items.Count == 0 ? "This folder is empty." : null);
            SetFooter(items.Count == 1 ? "1 item" : $"{items.Count} items", isError: false);
            _ = LoadDetailsAsync(items, cts.Token);
        }
        catch (Exception ex)
        {
            // A newer navigation replaced this one; its result is what counts.
            if (cts.IsCancellationRequested)
                return;

            if (_currentPath is null)
            {
                // Nothing loaded yet (e.g. no profile): show the error in the list area.
                _currentPath = target;
                FileGrid.ItemsSource = null;
                ShowMessage(ex.Message);
                SetFooter("", isError: false);
            }
            else
            {
                // Stay where we are and report the error underneath.
                ShowFooterError($"Could not open {target}: {ex.Message}");
            }
        }
        finally
        {
            if (_loadCts == cts)
            {
                PathBox.Text = _currentPath;
                UpdateButtons();
                Navigated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Selects the row for <paramref name="path"/> in the current folder, scrolls to it and focuses
    /// the list. False if the folder doesn't have it.
    /// </summary>
    public bool SelectPath(string path)
    {
        if (FileGrid.ItemsSource?.OfType<BrowserItem>().FirstOrDefault(i => i.Path == path) is not { } item)
            return false;

        FileGrid.SelectedItem = item;
        FileGrid.ScrollIntoView(item, null);
        FileGrid.Focus();
        return true;
    }

    /// <summary>Checks the details (S3 access) of these items again, e.g. after changing them.</summary>
    public Task RecheckDetailsAsync(IReadOnlyList<BrowserItem> items)
    {
        foreach (var item in items)
            item.SetAccess(ObjectAccess.NotChecked, null);
        return LoadDetailsAsync(items, _loadCts?.Token ?? CancellationToken.None);
    }

    /// <summary>Fills in slower details (S3 access icons) after the list is on screen.</summary>
    private async Task LoadDetailsAsync(IReadOnlyList<BrowserItem> items, CancellationToken cancellationToken)
    {
        try
        {
            await _source!.LoadDetailsAsync(items, cancellationToken);
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                ShowFooterError($"Couldn't check file access: {ex.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                DetailsLoaded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateButtons()
    {
        BackButton.IsEnabled = _back.Count > 0;
        UpButton.IsEnabled = _currentPath is not null && _source?.GetParent(_currentPath) is not null;
    }

    private void ShowMessage(string? message)
    {
        MessageText.Text = message;
        MessageText.IsVisible = message is not null;
    }

    private void ShowFooterError(string message) => SetFooter(message, isError: true);

    private void SetFooter(string text, bool isError)
    {
        FooterText.Text = text;
        FooterText.Classes.Set("error", isError);
    }

    /// <summary>Opens the selected folder or bucket; for a file, raises <see cref="FileOpened"/>.</summary>
    private void OpenSelected()
    {
        if (FileGrid.SelectedItem is not BrowserItem item)
            return;
        if (item.IsContainer)
            _ = NavigateAsync(item.Path);
        else
            FileOpened?.Invoke(this, item);
    }

    private void GoUp()
    {
        if (_currentPath is not null && _source?.GetParent(_currentPath) is { } parent)
            _ = NavigateAsync(parent);
    }

    private void FileGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-clicks on the column headers.
        if ((e.Source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null)
            OpenSelected();
    }

    private void FileGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Back:
                GoUp();
                e.Handled = true;
                break;
        }
    }

    private void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _ = NavigateAsync(PathBox.Text ?? "");
                e.Handled = true;
                break;
            case Key.Escape:
                PathBox.Text = _currentPath;
                e.Handled = true;
                break;
        }
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_back.TryPop(out var previous))
            _ = NavigateAsync(previous, addToHistory: false);
    }

    private void Up_Click(object? sender, RoutedEventArgs e) => GoUp();

    private void Home_Click(object? sender, RoutedEventArgs e)
    {
        if (_source is not null)
            _ = NavigateAsync(_source.HomePath);
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => _ = RefreshAsync();

    // ---- Drag source -------------------------------------------------------

    private void FileGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        _pendingSelect = null;

        var point = e.GetCurrentPoint(FileGrid);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        if ((e.Source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is not BrowserItem item)
            return;

        _dragStart = point.Position;

        // Pressing on a row that's part of a multi-selection would normally reduce the selection to
        // that row. Hold off until release, so the whole selection can be dragged.
        if (e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1
            && FileGrid.SelectedItems.Count > 1 && FileGrid.SelectedItems.Contains(item))
        {
            _pendingSelect = item;
            e.Handled = true;
        }
    }

    private async void FileGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragging)
            return;

        var point = e.GetCurrentPoint(FileGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragStart = null;
            return;
        }

        var delta = point.Position - start;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _dragStart = null;
        _pendingSelect = null;
        var items = SelectedItems;
        if (items.Count == 0)
            return;

        _dragging = true;
        try
        {
            s_activeDrag = (this, items);
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(PaneItemsFormat, "items"));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }
        finally
        {
            s_activeDrag = null;
            _dragging = false;
        }
    }

    private void FileGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A click (no drag) on a row in a multi-selection: now select just that row.
        if (_pendingSelect is not null)
            FileGrid.SelectedItem = _pendingSelect;
        _pendingSelect = null;
        _dragStart = null;
    }

    // ---- Drop target -------------------------------------------------------

    private bool CanAccept(DragEventArgs e)
    {
        if (e.DataTransfer.Contains(PaneItemsFormat))
            return AcceptsDropsFrom is not null && s_activeDrag?.Source == AcceptsDropsFrom;
        return AcceptsExternalFiles && e.DataTransfer.Contains(DataFormat.File);
    }

    private void Pane_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Pane_Drop(object? sender, DragEventArgs e)
    {
        if (!CanAccept(e))
            return;
        e.Handled = true;

        if (s_activeDrag is { } drag && e.DataTransfer.Contains(PaneItemsFormat))
        {
            ItemsDropped?.Invoke(drag.Source, drag.Items);
            return;
        }

        var items = StorageItems.ToLocalItems(e.DataTransfer.TryGetFiles());
        if (items.Count > 0)
            ItemsDropped?.Invoke(null, items);
    }
}
