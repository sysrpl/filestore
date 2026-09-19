using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using filestore.Helpers;

namespace filestore.Views;

/// <summary>Help > About: the program's name, what it does, and build information.</summary>
public partial class AboutWindow : DialogWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        NameText.Text = BuildInfo.ProductName;
        VersionText.Text = $"Version {BuildInfo.Version}";

        var details = BuildInfo.Details();
        for (var row = 0; row < details.Count; row++)
        {
            DetailsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var name = new TextBlock { Text = details[row].Name, Margin = new(0, 1, 16, 1) };
            name.Classes.Add("name");
            Grid.SetRow(name, row);

            var value = new SelectableTextBlock
            {
                Text = details[row].Value,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new(0, 1),
            };
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);

            DetailsGrid.Children.Add(name);
            DetailsGrid.Children.Add(value);
        }
    }

    private async void CopyDetails_Click(object? sender, RoutedEventArgs e)
    {
        var text = $"{BuildInfo.ProductName}\n" +
            string.Join("\n", BuildInfo.Details().Select(d => $"{d.Name}: {d.Value}"));
        try
        {
            if (Clipboard is { } clipboard)
                await ClipboardExtensions.SetTextAsync(clipboard, text);
        }
        catch (Exception)
        {
            // Clipboard unavailable; the details can still be selected and copied by hand.
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
