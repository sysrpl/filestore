using filestore.Models;

namespace filestore.Services;

/// <summary>Browses the local file system. Hidden and system files are skipped.</summary>
public sealed class LocalFileSource : IBrowserSource
{
    public string HomePath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string NormalizePath(string path)
    {
        path = path.Trim();
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = HomePath + path[1..];

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public string? GetParent(string path) => Directory.GetParent(path)?.FullName;

    public Task<IReadOnlyList<BrowserItem>> ListAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<BrowserItem>>(() =>
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                throw new DirectoryNotFoundException($"Folder not found: {path}");

            // IgnoreInaccessible = false so a folder we can't read reports an error instead of looking empty.
            var options = new EnumerationOptions { IgnoreInaccessible = false };
            var items = new List<BrowserItem>();
            foreach (var info in directory.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(ToItem(info));
            }
            return items;
        }, cancellationToken);

    public Task LoadDetailsAsync(IReadOnlyList<BrowserItem> items, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public bool CanModify(string folderPath) => true;

    public string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        if (name is "." or "..")
            return "That name isn't allowed.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Names can't contain / or other special characters.";
        return null;
    }

    public Task CreateFolderAsync(string folderPath, string name, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var path = Path.Combine(folderPath, name);
            if (Path.Exists(path))
                throw new IOException($"\"{name}\" already exists.");
            Directory.CreateDirectory(path);
        }, cancellationToken);

    public Task RenameAsync(BrowserItem item, string newName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var target = Path.Combine(Path.GetDirectoryName(item.Path)!, newName);
            // Allow changing only the case of a name (same path on case-insensitive file systems).
            var caseOnly = string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Path.Exists(target))
                throw new IOException($"\"{newName}\" already exists.");

            if (item.Kind == BrowserItemKind.Folder)
                Directory.Move(item.Path, target);
            else
                File.Move(item.Path, target);
        }, cancellationToken);

    public Task DeleteAsync(BrowserItem item, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            // Permanent: .NET has no cross-platform way to move things to the trash.
            if (item.Kind == BrowserItemKind.Folder)
                Directory.Delete(item.Path, recursive: true);
            else
                File.Delete(item.Path);
        }, cancellationToken);

    private static BrowserItem ToItem(FileSystemInfo info)
    {
        long? size = null;
        if (info is FileInfo file)
        {
            try
            {
                size = file.Length;
            }
            catch (IOException)
            {
                // e.g. a broken symbolic link: show it without a size.
            }
        }

        return new BrowserItem
        {
            Name = info.Name,
            Path = info.FullName,
            Kind = info is DirectoryInfo ? BrowserItemKind.Folder : BrowserItemKind.File,
            Size = size,
            Modified = info.LastWriteTime,
        };
    }
}
