using GeoAutoDocs.Core;
using Xunit;

namespace GeoAutoDocs.Tests;

public sealed class AsyncLoadingTests
{
    [Fact]
    public void MainPlayerUsesSoftwareDecodingForNativeStability()
    {
        Assert.Contains("--avcodec-hw=none", MediaPlaybackSafety.LibVlcOptions);
        Assert.DoesNotContain("--quiet", MediaPlaybackSafety.LibVlcOptions);
    }

    [Fact]
    public async Task AviPreflightAcceptsRiffAviHeaderWithoutDecodingMedia()
    {
        var path = Path.Combine(Path.GetTempPath(), $"valid_{Guid.NewGuid():N}.avi");
        try
        {
            await File.WriteAllBytesAsync(path,
                [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0,
                    (byte)'A', (byte)'V', (byte)'I', (byte)' ']);

            var result = await MediaPlaybackSafety.InspectAsync(path);

            Assert.True(result.CanOpen);
            Assert.Equal(12, result.FileLength);
            Assert.Equal(MediaPreflightFailureKind.None, result.FailureKind);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AviPreflightRejectsInvalidContainerBeforeLibVlcReceivesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"invalid_{Guid.NewGuid():N}.avi");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[32]);

            var result = await MediaPlaybackSafety.InspectAsync(path);

            Assert.False(result.CanOpen);
            Assert.Equal(MediaPreflightFailureKind.InvalidContainer, result.FailureKind);
            Assert.Contains("RIFF/AVI", result.FailureReason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AviPreflightTreatsMissingFileAsRecoverableItemFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.avi");

        var result = await MediaPlaybackSafety.InspectAsync(path);

        Assert.False(result.CanOpen);
        Assert.Equal(MediaPreflightFailureKind.Missing, result.FailureKind);
    }

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
