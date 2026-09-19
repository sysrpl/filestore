using Avalonia.Interactivity;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

// New folder, rename and delete in either pane; new and delete bucket in the S3 bucket list.
public partial class MainWindow
{
    // Counting stops here, so the delete-bucket warning appears quickly even for huge buckets.
    private const int BucketCountLimit = 10000;

    /// <summary>One step of a file operation: a name for progress, a log message on success, and the work.</summary>
    private sealed record FileOperation(string Name, string DoneMessage, Func<CancellationToken, Task> Run);

    private void NewFolder_Click(object? sender, RoutedEventArgs e) => _ = NewFolderAsync();

    private void NewBucket_Click(object? sender, RoutedEventArgs e) => _ = NewBucketAsync();

    private void Rename_Click(object? sender, RoutedEventArgs e) => _ = RenameAsync();

    private void Delete_Click(object? sender, RoutedEventArgs e) => _ = DeleteAsync();

    private async Task NewFolderAsync()
    {
        if (ShowingBucketList())
        {
            await NewBucketAsync();
            return;
        }

        var pane = _activePane;
        if (pane.Source is not { } source || pane.CurrentPath is not { } folder)
            return;
        if (!source.CanModify(folder))
        {
            Log("Open a bucket first: folders can't be created in the bucket list.", isError: true);
            return;
        }

        var name = await TextInputDialog.AskAsync(this, "New Folder", $"Name of the new folder in {folder}:",
            "New folder", "Create", source.ValidateName);
        if (name is null)
            return;

        await RunOperationAsync(pane, "Creating folder",
        [
            new(name, $"Created folder {name} in {folder}", ct => source.CreateFolderAsync(folder, name, ct)),
        ]);
    }

    private async Task NewBucketAsync()
    {
        if (_profiles.ActiveProfile is null)
        {
            Log("Add an AWS profile first (Profiles > Manage Profiles...).", isError: true);
            return;
        }

        var choice = await NewBucketDialog.AskAsync(this, _s3Source.DefaultRegion ?? AwsRegions.Default);
        if (choice is not { } bucket)
            return;

        var how = bucket.AllowPublic ? " (public files allowed)" : "";
        await RunOperationAsync(S3Pane, "Creating bucket",
        [
            new(bucket.Name, $"Created bucket {bucket.Name} in {bucket.Region}{how}",
                ct => _s3Source.CreateBucketAsync(bucket.Name, bucket.Region, bucket.AllowPublic, ct)),
        ]);
    }

    private async Task RenameAsync()
    {
        var pane = _activePane;
        var selected = ModifiableSelection();
        if (pane.Source is not { } source || selected.Count != 1)
        {
            Log("Select one file or folder to rename.", isError: true);
            return;
        }

        var item = selected[0];
        // Select just the name, not the extension, like most file managers.
        var dot = item.Kind == BrowserItemKind.File ? item.Name.LastIndexOf('.') : -1;
        var newName = await TextInputDialog.AskAsync(this, "Rename", $"New name for {item.Name}:",
            item.Name, "Rename", source.ValidateName, dot > 0 ? dot : null);
        if (newName is null || newName == item.Name)
            return;

        await RunOperationAsync(pane, "Renaming",
        [
            new(item.Name, $"Renamed {item.Path} to {newName}", ct => source.RenameAsync(item, newName, ct)),
        ]);
    }

    private async Task DeleteAsync()
    {
        if (ShowingBucketList())
        {
            await DeleteBucketAsync();
            return;
        }

        var pane = _activePane;
        var items = ModifiableSelection();
        if (pane.Source is not { } source || items.Count == 0)
        {
            Log("Select files or folders to delete.", isError: true);
            return;
        }

        var what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"these {items.Count} items";
        var where = pane == S3Pane ? "from S3" : "from this computer (they are not moved to the trash)";
        var folders = items.Any(i => i.IsContainer) ? " Folders are deleted with everything in them." : "";
        var confirmed = await ConfirmDialog.AskAsync(this, "Delete",
            $"Permanently delete {what} {where}?{folders}\n\nThis can't be undone.");
        if (!confirmed)
            return;

        await RunOperationAsync(pane, "Deleting",
            items.Select(item => new FileOperation(item.Name, $"Deleted {item.Path}",
                ct => source.DeleteAsync(item, ct))).ToList());
    }

    /// <summary>
    /// Deletes one selected bucket after a warning that lists what's in it and what uses it,
    /// and asks for the bucket name to be typed.
    /// </summary>
    private async Task DeleteBucketAsync()
    {
        var buckets = SelectedBuckets();
        if (buckets.Count != 1)
        {
            Log("Select one bucket to delete.", isError: true);
            return;
        }
        if (IsBusy())
            return;

        var bucket = buckets[0].Name;
        StatusText.Text = $"Checking what's in {bucket}...";
        var warnings = await DeleteBucketWarningsAsync(bucket);
        StatusText.Text = "";

        if (!await DeleteBucketDialog.AskAsync(this, bucket, warnings))
            return;

        await RunOperationAsync(S3Pane, "Deleting bucket",
        [
            new(bucket, $"Deleted bucket {bucket} and everything in it", ct => _s3Source.DeleteBucketAsync(bucket,
                deleted => StatusText.Text = $"Deleting bucket {bucket}: {deleted:N0} objects deleted", ct)),
        ]);
    }

    /// <summary>What deleting the bucket would destroy or break, one line each.</summary>
    private async Task<List<string>> DeleteBucketWarningsAsync(string bucket)
    {
        var warnings = new List<string>();
        try
        {
            var contents = await _s3Source.SummarizeBucketAsync(bucket, BucketCountLimit, CancellationToken.None);
            var more = contents.Incomplete ? "at least " : "";
            warnings.Add(contents.Files == 0 && contents.OlderVersions == 0 && !contents.Incomplete
                ? "The bucket is empty."
                : $"It holds {more}{Plural(contents.Files, "file")} ({more}{BrowserItem.FormatSize(contents.Bytes)}); all of them will be deleted.");
            if (contents.OlderVersions > 0)
                warnings.Add($"{more}{Plural(contents.OlderVersions, "older version")} of files will be deleted too, so nothing can be restored.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Couldn't check what's in it ({S3BrowserSource.DescribeError(ex)}). Everything in it will be deleted.");
        }

        try
        {
            if (await _s3Source.GetDistributionDomainAsync(bucket, CancellationToken.None) is { } domain)
                warnings.Add($"The CloudFront distribution {domain} serves this bucket; it will stop working.");
        }
        catch (Exception)
        {
            // Couldn't check CloudFront; not worth blocking the warning for.
        }

        warnings.Add("Websites, apps and links that use this bucket will stop working.");
        warnings.Add("Once deleted, the bucket name can be taken by any other AWS account.");
        warnings.Add("This can't be undone.");
        return warnings;
    }

    /// <summary>
    /// Runs file operation steps one after another, with progress and Cancel in the status bar and
    /// each result in the log. A failed step is logged and the rest still run. The pane reloads after.
    /// </summary>
    private async Task RunOperationAsync(BrowserPane pane, string verb, IReadOnlyList<FileOperation> steps)
    {
        if (IsBusy())
            return;

        var cts = BeginOperation();
        TransferProgressBar.IsIndeterminate = steps.Count == 1;
        var failed = 0;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                TransferProgressBar.Value = (double)i / steps.Count;
                StatusText.Text = steps.Count == 1 ? $"{verb}: {step.Name}" : $"{verb} {i + 1} of {steps.Count}: {step.Name}";
                try
                {
                    await step.Run(cts.Token);
                    Log(step.DoneMessage);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    Log($"{verb} {step.Name} failed: {S3BrowserSource.DescribeError(ex)}", isError: true);
                }
            }

            if (steps.Count > 1)
            {
                Log($"Finished: {steps.Count - failed} of {steps.Count} done" + (failed > 0 ? $", {failed} failed" : ""),
                    isError: failed > 0);
            }
        }
        catch (OperationCanceledException)
        {
            Log($"{verb} cancelled.", isError: true);
        }
        finally
        {
            EndOperation(cts);
            _ = pane.RefreshAsync();
        }
    }
}
