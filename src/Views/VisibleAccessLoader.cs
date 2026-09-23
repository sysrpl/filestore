using System.Collections.Concurrent;
using System.Threading.Channels;
using Avalonia.Threading;
using filestore.Models;
using filestore.Services;

namespace filestore.Views;

/// <summary>
/// Checks the access of S3 files as their rows scroll into view, so a long list only costs requests
/// for the rows someone looks at. Rows report in with <see cref="RowShown"/> and <see cref="RowHidden"/>
/// (on the UI thread); a background task checks them a few at a time, skipping rows that have
/// scrolled away before their turn, and applies each result on the UI thread.
/// </summary>
public sealed class VisibleAccessLoader : IDisposable
{
    // How many files are checked at once.
    private const int Concurrency = 8;

    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<BrowserItem> _queue = Channel.CreateUnbounded<BrowserItem>();

    // Rows on screen now, and files queued or already checked. Used from both threads.
    private readonly ConcurrentDictionary<BrowserItem, byte> _shown = new();
    private readonly ConcurrentDictionary<BrowserItem, byte> _queued = new();

    /// <summary>Starts checking files in <paramref name="bucket"/>. Create on the UI thread.</summary>
    public VisibleAccessLoader(S3BrowserSource source, string bucket)
    {
        // Created here, on the UI thread, since finding the bucket's region and distribution uses
        // the source's caches; the checks themselves then run in the background.
        var checker = source.CreateAccessCheckerAsync(bucket, _cts.Token);
        _ = Task.Run(() => RunAsync(checker, _cts.Token));
    }

    /// <summary>Raised on the UI thread when the checks can't start (e.g. the bucket can't be reached).</summary>
    public event EventHandler<string>? Failed;

    /// <summary>A row for <paramref name="item"/> came into view.</summary>
    public void RowShown(BrowserItem item)
    {
        _shown[item] = 0;
        if (item.Kind == BrowserItemKind.File && item.Access == ObjectAccess.NotChecked && _queued.TryAdd(item, 0))
            _queue.Writer.TryWrite(item);
    }

    /// <summary>The row for <paramref name="item"/> scrolled out of view (or was reused for another file).</summary>
    public void RowHidden(BrowserItem item) => _shown.TryRemove(item, out _);

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }

    private async Task RunAsync(Task<S3BrowserSource.AccessChecker> checkerTask, CancellationToken cancellationToken)
    {
        S3BrowserSource.AccessChecker checker;
        try
        {
            checker = await checkerTask;
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => Failed?.Invoke(this, $"Couldn't check file access: {ex.Message}"));
            return;
        }

        // Not disposed: checks still running when the loop ends release it afterwards.
        var throttle = new SemaphoreSlim(Concurrency);
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (!StillShown(item))
                    continue;

                await throttle.WaitAsync(cancellationToken);
                _ = CheckAsync(checker, item, throttle, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed, or replaced by a new search.
        }
    }

    /// <summary>
    /// True if the row is still on screen. If it isn't, the file is let go so it's queued again when
    /// it comes back into view - unless it came back just now, in which case it's kept.
    /// </summary>
    private bool StillShown(BrowserItem item)
    {
        if (_shown.ContainsKey(item))
            return true;
        _queued.TryRemove(item, out _);
        return _shown.ContainsKey(item) && _queued.TryAdd(item, 0);
    }

    private async Task CheckAsync(
        S3BrowserSource.AccessChecker checker, BrowserItem item, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        try
        {
            var (access, note) = await checker.CheckAsync(item, cancellationToken);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    checker.Apply(item, access, note);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    checker.Apply(item, ObjectAccess.Unknown, $"Couldn't check: {ex.Message}");
            });
        }
        finally
        {
            throttle.Release();
        }
    }
}
