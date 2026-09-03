using GeoAutoDocs.Core;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class AsyncLoadingTests
{
    [Fact]
    public void ContextSelectionCannotOpenMedia()
    {
        Assert.False(MediaLoadPolicy.CanOpen(MediaLoadIntent.ContextSelection));
        Assert.True(MediaLoadPolicy.CanOpen(MediaLoadIntent.ExplicitVideoSelection));
        Assert.True(MediaLoadPolicy.CanOpen(MediaLoadIntent.Reload));
        Assert.True(MediaLoadPolicy.CanOpen(MediaLoadIntent.NavigateToEvidence));
    }

    [Fact]
    public async Task BoundedExecutorLimitsConcurrentThumbnailWork()
    {
        var executor = new BoundedAsyncExecutor(3);
        var active = 0;
        var maximum = 0;
        var tasks = Enumerable.Range(0, 30).Select(_ => executor.RunAsync(async cancellationToken =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            try { await Task.Delay(15, cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
            return true;
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(3, maximum);
    }

    [Fact]
    public async Task BoundedExecutorCancelsQueuedWork()
    {
        var executor = new BoundedAsyncExecutor(1);
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = executor.RunAsync(async _ =>
        {
            await blocker.Task;
            return true;
        });
        using var cancellation = new CancellationTokenSource();
        var queued = executor.RunAsync(_ => Task.FromResult(true), cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        blocker.SetResult();
        await first;
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed || Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
                return;
        }
    }
}
