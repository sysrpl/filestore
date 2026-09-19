using Avalonia.Platform.Storage;
using filestore.Models;

namespace filestore.Helpers;

/// <summary>Converts files and folders from other programs (drag and drop, clipboard) into pane items.</summary>
public static class StorageItems
{
    /// <summary>A local item for the file or folder, or null if it has no local path.</summary>
    public static BrowserItem? ToLocalItem(IStorageItem storageItem)
    {
        if (storageItem.TryGetLocalPath() is not { } path)
            return null;

        var isFolder = storageItem is IStorageFolder;
        return new BrowserItem
        {
            Name = storageItem.Name,
            Path = path,
            Kind = isFolder ? BrowserItemKind.Folder : BrowserItemKind.File,
            Size = isFolder ? null : new FileInfo(path).Length,
        };
    }

    public static List<BrowserItem> ToLocalItems(IEnumerable<IStorageItem>? storageItems) =>
        (storageItems ?? []).Select(ToLocalItem).OfType<BrowserItem>().ToList();
}
