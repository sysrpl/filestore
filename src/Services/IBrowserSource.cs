using filestore.Models;

namespace filestore.Services;

/// <summary>Something a file pane can browse: the local disk or S3.</summary>
public interface IBrowserSource
{
    /// <summary>Where the pane starts.</summary>
    string HomePath { get; }

    /// <summary>Cleans up a path the user typed. Throws if it can't be used.</summary>
    string NormalizePath(string path);

    /// <summary>The parent of a path, or null at the top.</summary>
    string? GetParent(string path);

    /// <summary>Lists the folders and files directly inside a path.</summary>
    Task<IReadOnlyList<BrowserItem>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Fills in slower details (e.g. S3 access) after the list is shown. Must be called on the UI
    /// thread; items are updated there as results arrive.
    /// </summary>
    Task LoadDetailsAsync(IReadOnlyList<BrowserItem> items, CancellationToken cancellationToken);

    /// <summary>
    /// Whether folders can be created, and items renamed or deleted, in this folder.
    /// False for the S3 bucket list.
    /// </summary>
    bool CanModify(string folderPath);

    /// <summary>Why a file or folder name can't be used, or null if it's fine.</summary>
    string? ValidateName(string name);

    /// <summary>Creates an empty folder. Fails if something with that name already exists.</summary>
    Task CreateFolderAsync(string folderPath, string name, CancellationToken cancellationToken);

    /// <summary>Renames a file or folder in place. Fails if the new name is already taken.</summary>
    Task RenameAsync(BrowserItem item, string newName, CancellationToken cancellationToken);

    /// <summary>Permanently deletes a file, or a folder with everything in it.</summary>
    Task DeleteAsync(BrowserItem item, CancellationToken cancellationToken);
}
