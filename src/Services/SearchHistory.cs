using System.Text.Json;
using filestore.Models;

namespace filestore.Services;

/// <summary>A search from the Search dialog and the files it found.</summary>
public sealed class LastSearch
{
    /// <summary>The profile the search ran with; the results belong to that AWS account.</summary>
    public required Guid? ProfileId { get; init; }

    /// <summary>The S3 folder searched, e.g. "s3://bucket/photos/".</summary>
    public required string Folder { get; init; }

    public required string Pattern { get; init; }

    /// <summary>False when the search was stopped, or failed, before looking at every file.</summary>
    public required bool Complete { get; init; }

    public required IReadOnlyList<BrowserItem> Results { get; init; }
}

/// <summary>
/// Remembers the last search, so the Search dialog shows it again when reopened, also after a
/// restart. Saved as search.json in the app data folder, next to settings.json.
/// </summary>
public sealed class SearchHistory
{
    private readonly string _path;

    public SearchHistory(string? folder = null)
    {
        folder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "filestore");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "search.json");
        Last = Load();
    }

    /// <summary>The last search, or null if there hasn't been one.</summary>
    public LastSearch? Last { get; private set; }

    /// <summary>Makes <paramref name="search"/> the last search and saves it. Saving is best effort.</summary>
    public void Save(LastSearch search)
    {
        Last = search;
        var file = new SearchFile
        {
            ProfileId = search.ProfileId,
            Folder = search.Folder,
            Pattern = search.Pattern,
            Complete = search.Complete,
            Results = search.Results.Select(r => new SearchFileResult { Path = r.Path, Size = r.Size, Modified = r.Modified }).ToList(),
        };
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still remembered until the app closes.
        }
    }

    private LastSearch? Load()
    {
        try
        {
            if (!File.Exists(_path) || JsonSerializer.Deserialize<SearchFile>(File.ReadAllText(_path)) is not { } file)
                return null;

            return new LastSearch
            {
                ProfileId = file.ProfileId,
                Folder = file.Folder,
                Pattern = file.Pattern,
                Complete = file.Complete,
                Results = file.Results.Select(r => new BrowserItem
                {
                    Name = r.Path[(r.Path.LastIndexOf('/') + 1)..],
                    Path = r.Path,
                    Kind = BrowserItemKind.File,
                    Size = r.Size,
                    Modified = r.Modified,
                }).ToList(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // search.json. Access isn't stored; it's checked again as the results scroll into view.
    private sealed class SearchFile
    {
        public Guid? ProfileId { get; set; }
        public string Folder { get; set; } = "";
        public string Pattern { get; set; } = "";
        public bool Complete { get; set; }
        public List<SearchFileResult> Results { get; set; } = new();
    }

    private sealed class SearchFileResult
    {
        public string Path { get; set; } = "";
        public long? Size { get; set; }
        public DateTime? Modified { get; set; }
    }
}
