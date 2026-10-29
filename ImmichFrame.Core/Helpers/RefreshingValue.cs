namespace ImmichFrame.Core.Helpers;

/// <summary>
/// A value loaded on demand and kept for a lifetime. Concurrent callers share one load. Once the
/// value is older than its lifetime, callers keep getting it while a single refresh runs in the
/// background. A caller's cancellation ends only its own wait, never the shared load.
/// </summary>
public sealed class RefreshingValue<T> : IDisposable
{
    private readonly Func<CancellationToken, Task<T>> _load;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();

    private T? _value;
    private bool _hasValue;
    private DateTimeOffset _loadedAt;
    private Task<T>? _loading;

    public RefreshingValue(Func<CancellationToken, Task<T>> load, TimeSpan lifetime, TimeProvider? timeProvider = null)
    {
        _load = load;
        _lifetime = lifetime;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Lifetime => _lifetime;

    public Task<T> GetAsync(CancellationToken ct = default)
    {
        Task<T> loading;
        lock (_gate)
        {
            if (_hasValue)
            {
                if (_loading == null && _time.GetUtcNow() - _loadedAt >= _lifetime)
                {
                    _loading = StartLoad();
                    // Nobody awaits a background refresh; observe its failure so it is not rethrown later.
                    _loading.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }

                return Task.FromResult(_value!);
            }

            loading = _loading ??= StartLoad();
        }

        return loading.WaitAsync(ct);
    }

    // Runs the load on the thread pool so none of it runs while _gate is held.
    private Task<T> StartLoad() => Task.Run(Load);

    private async Task<T> Load()
    {
        try
        {
            var value = await _load(_disposed.Token);
            lock (_gate)
            {
                _value = value;
                _hasValue = true;
                _loadedAt = _time.GetUtcNow();
                _loading = null;
            }

            return value;
        }
        catch
        {
            lock (_gate)
            {
                _loading = null;
            }

            throw;
        }
    }

    // Cancels a load still running. The token source is not disposed, since that load may still hold its token.
    public void Dispose() => _disposed.Cancel();
}
