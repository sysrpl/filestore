using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using filestore.Services;
using filestore.Views;

namespace filestore;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var profiles = new ProfileService(new CredentialStore());
            string? loadError = null;
            try
            {
                profiles.Load();
            }
            catch (Exception ex)
            {
                loadError = $"Could not load AWS profiles: {ex.Message}";
            }

            desktop.MainWindow = new MainWindow(profiles, new SettingsService(), loadError);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
