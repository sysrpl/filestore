using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

// Edit > Copy and Paste between the panes, and with the system file manager.
public partial class MainWindow
{
    // What Edit > Copy last copied. ClipboardText is what was put on the system clipboard,
    // so Paste can tell whether something newer (e.g. from another program) replaced it.
    private (BrowserPane Source, IReadOnlyList<BrowserItem> Items, string ClipboardText)? _copied;

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
}
