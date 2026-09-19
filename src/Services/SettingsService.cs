using System.Text.Json;

namespace filestore.Services;

/// <summary>User preferences from the Preferences dialog (Edit > Preferences).</summary>
public sealed class AppSettings
{
    /// <summary>Show text captions next to the toolbar icons.</summary>
    public bool ShowToolbarCaptions { get; set; } = true;

    /// <summary>Show S3 on the left and the local files on the right.</summary>
    public bool SwapPanes { get; set; }

    /// <summary>Start with the upload lock unlocked, so uploads are public.</summary>
    public bool UploadPublicByDefault { get; set; }
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as settings.json in the app data folder
/// (next to the encrypted profiles). Nothing secret is stored here.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsService(string? folder = null)
    {
        folder ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "filestore");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
        Settings = Load();
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Raised after <see cref="Save"/> stores new settings.</summary>
    public event EventHandler? Changed;

    public void Save(AppSettings settings)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        Settings = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The saved settings, or the defaults if there are none or the file can't be read.</summary>
    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall back to the defaults; saving from the Preferences dialog writes a good file again.
        }
        return new AppSettings();
    }
}
