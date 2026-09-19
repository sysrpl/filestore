using Avalonia.Controls;
using Avalonia.Interactivity;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

/// <summary>What the Public / Private dialog asked for.</summary>
public enum AccessChoice
{
    MakePrivate,
    MakePublic,

    /// <summary>Change the bucket's settings so public ACLs work, then make the files public.</summary>
    AllowPublicThenMakePublic,
}

/// <summary>
/// Lists S3 files with their current public/private state and asks which to change them to.
/// Returns null when cancelled.
/// </summary>
public partial class AccessWindow : DialogWindow
{
    public AccessWindow()
    {
        InitializeComponent();
    }

    /// <param name="publicBlocker">
    /// Why this bucket's settings stop files being made public, or null. Replaces "Make public" with
    /// an option to change those settings first.
    /// </param>
    public static Task<AccessChoice?> AskAsync(
        Window owner, string folder, IReadOnlyList<BrowserItem> files, string? publicBlocker)
    {
        var dialog = new AccessWindow();
        if (publicBlocker is not null)
        {
            dialog.WarningText.Text = publicBlocker;
            dialog.WarningText.IsVisible = true;
            dialog.MakePublicButton.IsVisible = false;
            dialog.AllowPublicButton.IsVisible = true;
        }
        var count = files.Count == 1 ? "1 selected file" : $"{files.Count} selected files";
        dialog.HeaderText.Text = $"Make the {count} in {folder} public or private?";
        dialog.FileList.ItemsSource = files;
        return dialog.ShowModalAsync<AccessChoice?>(owner);
    }

    private void MakePrivate_Click(object? sender, RoutedEventArgs e) => CloseWith(AccessChoice.MakePrivate);

    private void MakePublic_Click(object? sender, RoutedEventArgs e) => CloseWith(AccessChoice.MakePublic);

    private void AllowPublic_Click(object? sender, RoutedEventArgs e) =>
        CloseWith(AccessChoice.AllowPublicThenMakePublic);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
