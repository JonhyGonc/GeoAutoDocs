namespace GeoAutoDocs.Core;

public sealed class BoundedAsyncExecutor
{
    private readonly SemaphoreSlim _gate;

    public BoundedAsyncExecutor(int maximumConcurrency)
    {
        if (maximumConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        _gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
