using ImmichFrame.Core.Helpers;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Helpers;

[TestFixture]
public class RefreshingValueTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<T> WaitFor<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException();
    }

    [Test]
    public async Task GetAsync_ConcurrentFirstCalls_ShareOneLoad()
    {
        var loads = 0;
        var release = new TaskCompletionSource();
        using var value = new RefreshingValue<int>(async _ =>
        {
            var n = Interlocked.Increment(ref loads);
            await release.Task;
            return n;
        }, TimeSpan.FromMinutes(5));

        var calls = Enumerable.Range(0, 10).Select(_ => Task.Run(() => value.GetAsync())).ToList();
        await Task.Delay(50);
        release.SetResult();

        Assert.That(await Task.WhenAll(calls), Is.All.EqualTo(1));
        Assert.That(loads, Is.EqualTo(1));
    }

    [Test]
    public async Task GetAsync_WithinLifetime_DoesNotReload()
    {
        var time = new FakeTimeProvider();
        var loads = 0;
        using var value = new RefreshingValue<int>(_ => Task.FromResult(Interlocked.Increment(ref loads)), TimeSpan.FromMinutes(5), time);

        Assert.That(await value.GetAsync(), Is.EqualTo(1));
        time.Now += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
        Assert.That(await value.GetAsync(), Is.EqualTo(1));
        Assert.That(loads, Is.EqualTo(1));
    }

    [Test]
    public async Task GetAsync_Expired_ServesOldValueWhileOneRefreshRuns()
    {
        var time = new FakeTimeProvider();
        var loads = 0;
        var release = new TaskCompletionSource();
        using var value = new RefreshingValue<int>(async _ =>
        {
            var n = Interlocked.Increment(ref loads);
            if (n > 1)
            {
                await release.Task;
            }

            return n;
        }, TimeSpan.FromMinutes(5), time);

        Assert.That(await value.GetAsync(), Is.EqualTo(1));
        time.Now += TimeSpan.FromMinutes(5);

        // Many callers while the refresh is held: all get the old value at once, one refresh runs.
        for (var i = 0; i < 10; i++)
        {
            Assert.That(await value.GetAsync(), Is.EqualTo(1));
        }

        await Task.Delay(20);
        Assert.That(loads, Is.EqualTo(2));

        release.SetResult();
        Assert.That(await WaitFor(() => value.GetAsync(), v => v == 2), Is.EqualTo(2));
        Assert.That(loads, Is.EqualTo(2));
    }

    [Test]
    public async Task GetAsync_CallerCancelled_DoesNotCancelSharedLoad()
    {
        var release = new TaskCompletionSource();
        CancellationToken loadToken = default;
        using var value = new RefreshingValue<int>(async ct =>
        {
            loadToken = ct;
            await release.Task;
            return 42;
        }, TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource();
        var cancelled = value.GetAsync(cts.Token);
        var other = value.GetAsync();
        cts.Cancel();

        Assert.That(async () => await cancelled, Throws.InstanceOf<OperationCanceledException>());
        release.SetResult();
        Assert.That(await other, Is.EqualTo(42));
        Assert.That(loadToken.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task GetAsync_FirstLoadFails_NextCallLoadsAgain()
    {
        var loads = 0;
        using var value = new RefreshingValue<int>(_ =>
        {
            var n = Interlocked.Increment(ref loads);
            return n == 1 ? throw new HttpRequestException("down") : Task.FromResult(n);
        }, TimeSpan.FromMinutes(5));

        Assert.That(async () => await value.GetAsync(), Throws.InstanceOf<HttpRequestException>());
        Assert.That(await value.GetAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetAsync_RefreshFails_KeepsServingOldValue()
    {
        var time = new FakeTimeProvider();
        var loads = 0;
        using var value = new RefreshingValue<int>(_ =>
        {
            var n = Interlocked.Increment(ref loads);
            return n == 2 ? throw new HttpRequestException("down") : Task.FromResult(n);
        }, TimeSpan.FromMinutes(5), time);

        Assert.That(await value.GetAsync(), Is.EqualTo(1));
        time.Now += TimeSpan.FromMinutes(5);
        Assert.That(await value.GetAsync(), Is.EqualTo(1));

        // The failed refresh is not retried until the next call, which still gets the old value.
        Assert.That(await WaitFor(() => value.GetAsync(), v => v == 3), Is.EqualTo(3));
    }
}
