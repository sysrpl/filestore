using Avalonia.Interactivity;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

// Uploads and downloads between the panes, with progress in the status bar.
public partial class MainWindow
{
    // "Uploading" or "Downloading", for progress in the status bar.
    private string _transferVerb = "";

    private async void Upload_Click(object? sender, RoutedEventArgs e) =>
        await RunTransferAsync(TransferDirection.Upload, LocalPane.SelectedItems);

    private async void Download_Click(object? sender, RoutedEventArgs e) =>
        await RunTransferAsync(TransferDirection.Download, S3Pane.SelectedItems);

    private async Task RunTransferAsync(TransferDirection direction, IReadOnlyList<BrowserItem> items)
    {
        var upload = direction == TransferDirection.Upload;
        var destination = upload ? S3Pane : LocalPane;

        if (IsBusy())
            return;
        if (items.Count == 0)
        {
            Log($"Select files or folders in the {(upload ? "Local" : "Amazon S3")} pane first.", isError: true);
            return;
        }
        if (destination.CurrentPath is not { } destinationPath)
        {
            Log("The destination pane hasn't loaded a folder yet.", isError: true);
            return;
        }

        var access = PublicToggle.IsChecked == true ? UploadAccess.PublicRead : UploadAccess.Private;
        _transferVerb = upload ? "Uploading" : "Downloading";
        var cts = BeginOperation();
        StatusText.Text = "Preparing...";

        try
        {
            var plan = upload
                ? await _transfers.PlanUploadAsync(items, destinationPath, cts.Token)
                : await _transfers.PlanDownloadAsync(items, destinationPath, cts.Token);

            if (plan.Files.Count == 0)
            {
                Log("Nothing to copy: the selected folders are empty.");
                return;
            }

            var overwrite = true;
            if (plan.ExistingCount > 0)
            {
                overwrite = await ConfirmDialog.AskAsync(this, "Files already exist",
                    $"{plan.ExistingCount} of the {plan.Files.Count} files already exist at the destination. " +
                    "Overwrite them?\n\nYes overwrites them. No skips them and copies the rest.");
            }

            var toCopy = overwrite ? plan.Files : plan.Files.Where(f => !f.Exists).ToList();
            Log($"{_transferVerb} {Plural(toCopy.Count, "file")} " +
                $"({BrowserItem.FormatSize(toCopy.Sum(f => f.Size))}) to {destinationPath}"
                + (upload ? (access == UploadAccess.PublicRead ? " as public" : " as private") : ""));
            if (!overwrite)
            {
                foreach (var file in plan.Files.Where(f => f.Exists))
                    Log($"Skipped (already exists): {Describe(plan, file)}");
            }

            var progress = new Progress<TransferProgress>(ShowProgress);
            var done = upload ? "Uploaded" : "Downloaded";
            var result = await _transfers.RunAsync(plan, overwrite, access, progress,
                file => Log($"{done} {Describe(plan, file)} ({BrowserItem.FormatSize(file.Size)})"),
                cts.Token);

            Log($"Finished: {done.ToLowerInvariant()} {Plural(result.Copied, "file")} " +
                $"({BrowserItem.FormatSize(result.Bytes)})"
                + (result.Skipped > 0 ? $", skipped {result.Skipped} existing" : ""));
        }
        catch (OperationCanceledException)
        {
            Log("Transfer cancelled.", isError: true);
        }
        catch (Exception ex)
        {
            Log($"Transfer failed: {ex.Message}", isError: true);
        }
        finally
        {
            EndOperation(cts);
            _ = destination.RefreshAsync();
        }
    }

    /// <summary>"local/path → s3://bucket/key" or the reverse, for the log.</summary>
    private static string Describe(TransferPlan plan, TransferFile file)
    {
        var s3Path = $"{S3BrowserSource.Root}{plan.Bucket}/{file.Key}";
        return plan.Direction == TransferDirection.Upload
            ? $"{file.LocalPath} → {s3Path}"
            : $"{s3Path} → {file.LocalPath}";
    }

    private void ShowProgress(TransferProgress p)
    {
        if (_transferCts is null)
            return; // a late report after the transfer finished

        TransferProgressBar.IsIndeterminate = false;
        TransferProgressBar.Value = p.BytesTotal > 0
            ? (double)p.BytesDone / p.BytesTotal
            : (double)(p.FileNumber - 1) / p.FileCount;

        StatusText.Text = $"{_transferVerb} {p.FileNumber} of {p.FileCount}: {p.FileName}  —  " +
            $"{BrowserItem.FormatSize(p.BytesDone)} of {BrowserItem.FormatSize(p.BytesTotal)}";
    }
}
