using Avalonia.Interactivity;
using filestore.Helpers;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

// Public / private access for S3 files, and their links: Copy URL, Temporary Link, open in browser.
public partial class MainWindow
{
    private void PublicToggle_Changed(object? sender, RoutedEventArgs e) => UpdatePublicToggle();

    /// <summary>Locked icon for private uploads, unlocked for public.</summary>
    private void UpdatePublicToggle()
    {
        var isPublic = PublicToggle.IsChecked == true;
        PublicToggle.Content = isPublic ? Icons.LockOpenVariant : Icons.Lock;
        if (isPublic)
            Tip.Set(PublicToggle, "Uploads are public", "Anyone with the link can read files you upload (public-read ACL). Click to make uploads private.");
        else
            Tip.Set(PublicToggle, "Uploads are private", "Only people with your AWS keys can read files you upload. Click to make uploads public.");
    }

    private async void ChangeAccess_Click(object? sender, RoutedEventArgs e)
    {
        var files = SelectedS3Files();
        if (files.Count == 0)
            return;

        // Warn up front when the bucket's own settings make "public" impossible.
        string? publicBlocker = null;
        try
        {
            publicBlocker = await _s3Source.GetPublicAclBlockerAsync(
                S3BrowserSource.SplitPath(files[0].Path).Bucket, CancellationToken.None);
        }
        catch (Exception)
        {
            // Couldn't check; S3 will say so if making files public fails.
        }

        var choice = await AccessWindow.AskAsync(this, S3Pane.CurrentPath ?? "S3", files, publicBlocker);
        if (choice is not { } picked)
            return;

        var access = picked == AccessChoice.MakePrivate ? UploadAccess.Private : UploadAccess.PublicRead;
        await ChangeAccessAsync(files, access, allowPublicFirst: picked == AccessChoice.AllowPublicThenMakePublic);
    }

    /// <summary>Sets each file's ACL, with progress in the status bar and results in the log.</summary>
    /// <param name="allowPublicFirst">Change the bucket's settings so public ACLs work before starting.</param>
    private async Task ChangeAccessAsync(List<BrowserItem> files, UploadAccess access, bool allowPublicFirst)
    {
        if (IsBusy())
            return;

        var label = access == UploadAccess.PublicRead ? "public" : "private";
        var cts = BeginOperation();
        TransferProgressBar.IsIndeterminate = false;
        Log($"Making {Plural(files.Count, "file")} {label}");

        var changed = 0;
        var failed = 0;
        try
        {
            if (allowPublicFirst)
            {
                var bucket = S3BrowserSource.SplitPath(files[0].Path).Bucket;
                StatusText.Text = $"Allowing public files in {bucket}...";
                try
                {
                    await _s3Source.AllowPublicAclsAsync(bucket, cts.Token);
                    Log($"Allowed public files in bucket {bucket}: ACLs turned on, public ACLs no longer blocked");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"Couldn't change bucket {bucket}'s settings: {S3BrowserSource.DescribeError(ex)}", isError: true);
                    return;
                }
            }

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                TransferProgressBar.Value = (double)i / files.Count;
                StatusText.Text = $"Making {label} {i + 1} of {files.Count}: {file.Name}";
                try
                {
                    await _s3Source.SetObjectAccessAsync(file.Path, access, cts.Token);
                    changed++;
                    Log($"Made {label}: {file.Path}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    var reason = access == UploadAccess.PublicRead
                        ? S3BrowserSource.DescribePublicAclError(ex)
                        : S3BrowserSource.DescribeError(ex);
                    Log($"Couldn't make {file.Path} {label}: {reason}", isError: true);
                }
            }

            Log($"Finished: made {Plural(changed, "file")} {label}" + (failed > 0 ? $", {failed} failed" : ""),
                isError: failed > 0);
        }
        catch (OperationCanceledException)
        {
            Log($"Cancelled after making {Plural(changed, "file")} {label}.", isError: true);
        }
        finally
        {
            EndOperation(cts);
            // Show the real result: a public bucket policy, for example, overrides a private ACL.
            _ = S3Pane.RecheckDetailsAsync(files);
        }
    }

    /// <summary>
    /// Invalidates the selected files in every CloudFront distribution that serves the bucket, so
    /// CloudFront fetches them from S3 again.
    /// </summary>
    private async void InvalidateCache_Click(object? sender, RoutedEventArgs e)
    {
        var files = SelectedS3Files();
        if (files.Count == 0)
            return;

        InvalidateCacheToolButton.IsEnabled = false;
        StatusText.Text = $"Invalidating the CloudFront cache for {Plural(files.Count, "file")}...";
        try
        {
            var results = await _s3Source.InvalidateCacheAsync(files.Select(f => f.Path).ToList(), CancellationToken.None);
            if (results.Count == 0)
            {
                var reason = _s3Source.CloudFrontError is { } error
                    ? $"CloudFront couldn't be checked: {error}."
                    : "no CloudFront distribution serves these files.";
                Log($"Nothing to invalidate: {reason}", isError: true);
                return;
            }

            var what = files.Count == 1 ? files[0].Path : Plural(files.Count, "file");
            foreach (var result in results)
                Log($"Invalidating {what} on {result.Domain} (invalidation {result.Id}); it takes a minute or two to finish");
        }
        catch (Exception ex)
        {
            var reason = ex is Amazon.Runtime.AmazonServiceException { ErrorCode: "AccessDenied" }
                ? "no permission to create invalidations (cloudfront:CreateInvalidation)"
                : ex.Message;
            Log($"Couldn't invalidate the CloudFront cache: {reason}", isError: true);
        }
        finally
        {
            InvalidateCacheToolButton.IsEnabled = true;
        }
    }

    /// <summary>Puts the selected files' links on the clipboard, one per line.</summary>
    private async void CopyUrl_Click(object? sender, RoutedEventArgs e)
    {
        var files = SelectedS3Files();
        var urls = files.Select(f => f.ShareUrl).OfType<string>().ToList();
        if (urls.Count == 0 || !await SetClipboardTextAsync(string.Join("\n", urls)))
            return;

        Log(urls.Count == 1 ? $"Copied URL: {urls[0]}" : $"Copied {urls.Count} URLs, starting with {urls[0]}");

        var skipped = files.Count - urls.Count;
        if (skipped > 0)
        {
            var reason = _s3Source.CloudFrontError is { } error
                ? $" CloudFront couldn't be checked: {error}."
                : "";
            Log($"No URL for {Plural(skipped, "selected file")}: private in S3, and no CloudFront distribution serves this bucket with plain links.{reason}",
                isError: true);
        }
    }

    /// <summary>
    /// Asks how long the link should work, then creates a temporary (presigned) link for each
    /// selected file and copies them to the clipboard, one per line.
    /// </summary>
    private async void ShareLink_Click(object? sender, RoutedEventArgs e)
    {
        var files = SelectedS3Files();
        if (files.Count == 0)
            return;

        if (await ShareLinkDialog.AskAsync(this, files) is not { } validFor)
            return;

        var expires = DateTime.Now + validFor;
        var urls = new List<string>();
        foreach (var file in files)
        {
            try
            {
                urls.Add(await _s3Source.CreateTemporaryUrlAsync(file.Path, validFor, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Log($"Couldn't create a temporary link for {file.Path}: {S3BrowserSource.DescribeError(ex)}", isError: true);
            }
        }
        if (urls.Count == 0 || !await SetClipboardTextAsync(string.Join("\n", urls)))
            return;

        var until = $"{expires:ddd d MMM yyyy, HH:mm}";
        Log(urls.Count == 1
            ? $"Copied a temporary link to {files[0].Name}, valid until {until}: {urls[0]}"
            : $"Copied {urls.Count} temporary links, valid until {until}");
    }

    /// <summary>
    /// Opens an S3 file's link in the default browser - the same link Copy URL gives: CloudFront
    /// when a distribution serves the bucket, otherwise the S3 URL of a public file.
    /// </summary>
    private async Task OpenInBrowserAsync(BrowserItem file)
    {
        if (file.ShareUrl is not { } url)
        {
            var reason = file.Access == ObjectAccess.NotChecked
                ? "its access is still being checked; try again in a moment"
                : "it's private and no CloudFront distribution serves the bucket";
            Log($"Can't open {file.Name} in the browser: {reason}.", isError: true);
            return;
        }

        try
        {
            if (await Launcher.LaunchUriAsync(new Uri(url)))
                Log($"Opened in browser: {url}");
            else
                Log($"Couldn't open a browser for {url}", isError: true);
        }
        catch (Exception ex)
        {
            Log($"Couldn't open a browser for {url}: {ex.Message}", isError: true);
        }
    }
}
