using System.Collections;
using System.ComponentModel;
using filestore.Helpers;

namespace filestore.Models;

public enum BrowserItemKind
{
    Bucket,
    Folder,
    File,
}

/// <summary>Who can read an S3 object. Only filled in for S3 files, after the list has loaded.</summary>
public enum ObjectAccess
{
    /// <summary>Not applicable (local items, folders) or not checked yet.</summary>
    NotChecked,

    /// <summary>Couldn't be worked out, e.g. no permission to read the ACL.</summary>
    Unknown,

    Private,
    Public,
}

/// <summary>One row in a file pane: a local file or folder, an S3 bucket, folder (prefix) or object.</summary>
/// <remarks>Everything is fixed except <see cref="Access"/>, which is filled in later and raises PropertyChanged.</remarks>
public sealed class BrowserItem : INotifyPropertyChanged
{
    private ObjectAccess _access;
    private string? _accessNote;
    private string? _shareUrl;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name { get; init; }

    /// <summary>Full path: a local path, or s3://bucket/key.</summary>
    public required string Path { get; init; }

    public required BrowserItemKind Kind { get; init; }
    public long? Size { get; init; }

    /// <summary>Last modified time (creation time for buckets), in local time.</summary>
    public DateTime? Modified { get; init; }

    /// <summary>The folder holding a file: its path up to the name. Shown for search results.</summary>
    public string Location => Path[..^Name.Length];

    /// <summary>True for buckets and folders, which can be opened.</summary>
    public bool IsContainer => Kind != BrowserItemKind.File;

    public string Icon => Kind switch
    {
        BrowserItemKind.Bucket => Icons.Bucket,
        BrowserItemKind.Folder => Icons.Folder,
        _ => Icons.File,
    };

    public ObjectAccess Access => _access;

    /// <summary>Explains <see cref="Access"/>; shown as the icon's tooltip.</summary>
    public string? AccessNote => _accessNote;

    public bool IsPublic => _access == ObjectAccess.Public;

    public string AccessIcon => _access switch
    {
        ObjectAccess.Private => Icons.LockOutline,
        ObjectAccess.Public => Icons.Earth,
        ObjectAccess.Unknown => Icons.HelpCircleOutline,
        _ => "",
    };

    /// <summary>
    /// A link anyone can open: the CloudFront URL when the bucket has a distribution, else the S3 URL
    /// for public files. Null when there is no such link. Filled in with <see cref="Access"/>.
    /// </summary>
    public string? ShareUrl
    {
        get => _shareUrl;
        set
        {
            if (_shareUrl == value)
                return;
            _shareUrl = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShareUrl)));
        }
    }

    public string AccessText => _access switch
    {
        ObjectAccess.Private => "Private",
        ObjectAccess.Public => "Public",
        ObjectAccess.Unknown => "Unknown",
        _ => "Checking...",
    };

    public void SetAccess(ObjectAccess access, string? note)
    {
        _access = access;
        _accessNote = note;
        foreach (var name in new[] { nameof(Access), nameof(AccessNote), nameof(IsPublic), nameof(AccessIcon), nameof(AccessText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public string SizeText => Kind == BrowserItemKind.File && Size is long size ? FormatSize(size) : "";

    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// Sorts rows for the DataGrid columns: buckets and folders before files, then by
/// the column's value, then by name.
/// </summary>
public sealed class BrowserItemComparer : IComparer, IComparer<BrowserItem>
{
    public static readonly BrowserItemComparer ByName = new((a, b) => 0);
    public static readonly BrowserItemComparer BySize = new((a, b) => Nullable.Compare(a.Size, b.Size));
    public static readonly BrowserItemComparer ByModified = new((a, b) => Nullable.Compare(a.Modified, b.Modified));
    public static readonly BrowserItemComparer ByLocation = new((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Location, b.Location));
    public static readonly BrowserItemComparer ByAccess = new((a, b) => a.Access.CompareTo(b.Access));

    private readonly Comparison<BrowserItem> _compare;

    private BrowserItemComparer(Comparison<BrowserItem> compare)
    {
        _compare = compare;
    }

    public int Compare(object? x, object? y) =>
        x is BrowserItem a && y is BrowserItem b ? Compare(a, b) : 0;

    public int Compare(BrowserItem? a, BrowserItem? b)
    {
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : -1) : 1;

        var containersFirst = b.IsContainer.CompareTo(a.IsContainer);
        if (containersFirst != 0)
            return containersFirst;

        var result = _compare(a, b);
        return result != 0 ? result : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
    }
}
