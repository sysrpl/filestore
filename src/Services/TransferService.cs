using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using filestore.Models;

namespace filestore.Services;

public enum TransferDirection
{
    Upload,
    Download,
}

/// <summary>Who can read uploaded objects.</summary>
public enum UploadAccess
{
    /// <summary>No ACL is sent, so the object gets the bucket's default (private).</summary>
    Private,

    /// <summary>Sends the "public-read" canned ACL.</summary>
    PublicRead,
}

/// <summary>One file to copy. <see cref="Exists"/> is true when the destination already has it.</summary>
public sealed record TransferFile(string LocalPath, string Key, long Size, bool Exists);

/// <summary>Every file a transfer will copy, worked out before anything is copied.</summary>
public sealed class TransferPlan
{
    public required TransferDirection Direction { get; init; }
    public required string Bucket { get; init; }
    public required IReadOnlyList<TransferFile> Files { get; init; }

    public int ExistingCount => Files.Count(f => f.Exists);
}

public readonly record struct TransferProgress(
    int FileNumber, int FileCount, string FileName, long BytesDone, long BytesTotal);

public sealed record TransferResult(int Copied, int Skipped, long Bytes);

/// <summary>
/// Copies files and folders between the local disk and S3, one file at a time.
/// TransferUtility switches to multipart transfers for large files by itself.
/// </summary>
public sealed class TransferService
{
    private const int ProgressIntervalMs = 100;

    private readonly S3BrowserSource _s3;

    public TransferService(S3BrowserSource s3)
    {
        _s3 = s3;
    }

    /// <summary>Plans an upload of local files and folders (recursively) into an S3 folder.</summary>
    public async Task<TransferPlan> PlanUploadAsync(
        IReadOnlyList<BrowserItem> localItems, string s3Folder, CancellationToken cancellationToken)
    {
        var (bucket, prefix) = S3BrowserSource.SplitPath(s3Folder);
        if (bucket.Length == 0)
            throw new InvalidOperationException("Open a bucket in the S3 pane to upload into.");

        // Walk the local folders on a background thread; they can be large.
        var files = await Task.Run(() =>
        {
            var list = new List<(string LocalPath, string Key, long Size)>();
            foreach (var item in localItems)
            {
                if (item.Kind == BrowserItemKind.File)
                {
                    list.Add((item.Path, prefix + item.Name, new FileInfo(item.Path).Length));
                    continue;
                }

                // Default options skip hidden/system files and folders we can't read, like the pane does.
                var options = new EnumerationOptions { RecurseSubdirectories = true };
                foreach (var file in new DirectoryInfo(item.Path).EnumerateFiles("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(item.Path, file.FullName)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    list.Add((file.FullName, $"{prefix}{item.Name}/{relative}", file.Length));
                }
            }
            return list;
        }, cancellationToken);

        // Find which keys already exist: one listing per selected item.
        using var client = await _s3.CreateClientForBucketAsync(bucket, cancellationToken);
        var existing = new HashSet<string>();
        foreach (var item in localItems)
        {
            var itemPrefix = prefix + item.Name + (item.Kind == BrowserItemKind.File ? "" : "/");
            foreach (var obj in await S3BrowserSource.ListAllAsync(client, bucket, itemPrefix, cancellationToken))
                existing.Add(obj.Key);
        }

        return new TransferPlan
        {
            Direction = TransferDirection.Upload,
            Bucket = bucket,
            Files = files.Select(f => new TransferFile(f.LocalPath, f.Key, f.Size, existing.Contains(f.Key))).ToList(),
        };
    }

    /// <summary>Plans a download of S3 files and folders (recursively) into a local folder.</summary>
    public async Task<TransferPlan> PlanDownloadAsync(
        IReadOnlyList<BrowserItem> s3Items, string localFolder, CancellationToken cancellationToken)
    {
        if (s3Items.Count == 0)
            throw new InvalidOperationException("Nothing is selected.");
        if (s3Items.Any(i => i.Kind == BrowserItemKind.Bucket))
            throw new InvalidOperationException("Open a bucket and select the files or folders inside it to download.");

        // All selected items come from the folder the S3 pane is showing, so they share a bucket.
        var bucket = S3BrowserSource.SplitPath(s3Items[0].Path).Bucket;
        var root = Path.GetFullPath(localFolder);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var files = new List<TransferFile>();

        void Add(string key, string relativePath, long size)
        {
            var localPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            // A key containing ".." could point outside the target folder; skip it.
            if (!localPath.StartsWith(rootPrefix, StringComparison.Ordinal))
                return;
            files.Add(new TransferFile(localPath, key, size, File.Exists(localPath)));
        }

        using var client = await _s3.CreateClientForBucketAsync(bucket, cancellationToken);
        foreach (var item in s3Items)
        {
            var key = S3BrowserSource.SplitPath(item.Path).Key;
            if (item.Kind == BrowserItemKind.File)
            {
                Add(key, item.Name, item.Size ?? 0);
                continue;
            }

            foreach (var obj in await S3BrowserSource.ListAllAsync(client, bucket, key, cancellationToken))
            {
                if (obj.Key.EndsWith('/'))
                    continue; // empty "folder/" marker object
                Add(obj.Key, item.Name + "/" + obj.Key[key.Length..], SizeOf(obj.Size));
            }
        }

        return new TransferPlan { Direction = TransferDirection.Download, Bucket = bucket, Files = files };
    }

    /// <summary>
    /// Copies the planned files. When <paramref name="overwrite"/> is false, files that already
    /// exist at the destination are skipped. Stops at the first error.
    /// <paramref name="fileCompleted"/> is called after each file, on the caller's thread
    /// (in order, before this method returns).
    /// </summary>
    public async Task<TransferResult> RunAsync(
        TransferPlan plan,
        bool overwrite,
        UploadAccess access,
        IProgress<TransferProgress> progress,
        Action<TransferFile> fileCompleted,
        CancellationToken cancellationToken)
    {
        var files = overwrite ? plan.Files : plan.Files.Where(f => !f.Exists).ToList();
        var skipped = plan.Files.Count - files.Count;
        var totalBytes = files.Sum(f => f.Size);
        long doneBytes = 0;

        using var client = await _s3.CreateClientForBucketAsync(plan.Bucket, cancellationToken);
        using var transfer = new TransferUtility(client);

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var number = i + 1;
            var name = Path.GetFileName(file.LocalPath);
            var bytesBefore = doneBytes;
            progress.Report(new TransferProgress(number, files.Count, name, bytesBefore, totalBytes));

            // TransferUtility raises progress very often, from background threads; pass on at most every 100ms.
            long lastReport = 0;
            void OnProgress(long transferred)
            {
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastReport) < ProgressIntervalMs)
                    return;
                Interlocked.Exchange(ref lastReport, now);
                progress.Report(new TransferProgress(number, files.Count, name, bytesBefore + transferred, totalBytes));
            }

            try
            {
                if (plan.Direction == TransferDirection.Upload)
                    await UploadAsync(transfer, plan.Bucket, file, access, OnProgress, cancellationToken);
                else
                    await DownloadAsync(transfer, plan.Bucket, file, OnProgress, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string Describe(Exception e) => plan.Direction == TransferDirection.Upload && access == UploadAccess.PublicRead
                    ? S3BrowserSource.DescribePublicAclError(e)
                    : S3BrowserSource.DescribeError(e);

                throw new InvalidOperationException(
                    $"{name}: {Describe(ex)} ({i} of {files.Count} files were copied before this.)", ex);
            }

            doneBytes += file.Size;
            fileCompleted(file);
        }

        progress.Report(new TransferProgress(files.Count, files.Count, "", totalBytes, totalBytes));
        return new TransferResult(files.Count, skipped, totalBytes);
    }

    private static async Task UploadAsync(
        TransferUtility transfer, string bucket, TransferFile file, UploadAccess access,
        Action<long> onProgress, CancellationToken cancellationToken)
    {
        var request = new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = file.Key,
            FilePath = file.LocalPath,
        };
        if (access == UploadAccess.PublicRead)
            request.CannedACL = S3CannedACL.PublicRead;
        request.UploadProgressEvent += (_, e) => onProgress(e.TransferredBytes);

        await transfer.UploadAsync(request, cancellationToken);
    }

    private static async Task DownloadAsync(
        TransferUtility transfer, string bucket, TransferFile file,
        Action<long> onProgress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath)!);
        var request = new TransferUtilityDownloadRequest
        {
            BucketName = bucket,
            Key = file.Key,
            FilePath = file.LocalPath,
        };
        request.WriteObjectProgressEvent += (_, e) => onProgress(e.TransferredBytes);

        try
        {
            await transfer.DownloadAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Don't leave a half-downloaded file behind.
            try { File.Delete(file.LocalPath); } catch (IOException) { }
            throw;
        }
    }

    // Takes long? so it works whether the SDK declares Size as long or long?.
    private static long SizeOf(long? size) => size ?? 0;
}
