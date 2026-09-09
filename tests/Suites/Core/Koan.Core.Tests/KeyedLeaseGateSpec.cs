using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Koan.Core.Concurrency;
using Xunit;

namespace Koan.Core.Tests;

public sealed class KeyedLeaseGateSpec
{
    [Fact]
    public async Task RunAsync_enforces_exclusive_access_for_same_key()
    {
        var registry = new KeyedLeaseGate();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = registry.RunAsync<int>(
            "shared",
            TimeSpan.FromSeconds(1),
            async ct =>
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(ct);
                return 1;
            },
            CancellationToken.None);

        await firstEntered.Task;

        var secondTask = Task.Run(async () =>
        {
            return await registry.RunAsync<int>(
                "shared",
                TimeSpan.FromSeconds(1),
                ct =>
                {
                    secondStarted.TrySetResult(true);
                    return new ValueTask<int>(2);
                },
                CancellationToken.None);
        });

        await Task.Delay(20);
        secondStarted.Task.IsCompleted.Should().BeFalse(
            "second caller should wait until the first releases the gate");

        releaseFirst.TrySetResult(true);

        await secondStarted.Task;
        var secondResult = await secondTask;
        secondResult.Should().Be(2);

        await first;
    }

    [Fact]
    public async Task RunAsync_timeout_when_lock_not_acquired_within_window()
    {
        var registry = new KeyedLeaseGate();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = registry.RunAsync<int>(
            "timeout",
            TimeSpan.FromSeconds(1),
            async ct =>
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(ct);
                return 1;
            },
            CancellationToken.None);

        await firstEntered.Task;

        var act = async () => await registry.RunAsync<int>(
            "timeout",
            TimeSpan.FromMilliseconds(50),
            _ => new ValueTask<int>(0),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();

        releaseFirst.TrySetResult(true);
        await first;

        (await registry.RunAsync<int>("timeout", TimeSpan.FromSeconds(1),
            _ => new ValueTask<int>(42), CancellationToken.None)).Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_distinct_keys_run_concurrently()
    {
        var registry = new KeyedLeaseGate();
        var aEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = registry.RunAsync<int>("A", TimeSpan.FromSeconds(1), async ct =>
        {
            aEntered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return 1;
        }, CancellationToken.None);

        var b = registry.RunAsync<int>("B", TimeSpan.FromSeconds(1), async ct =>
        {
            bEntered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return 2;
        }, CancellationToken.None);

        await aEntered.Task;
        await bEntered.Task;  // Both must enter before either releases — proves they don't share a gate.

        release.TrySetResult(true);
        (await a).Should().Be(1);
        (await b).Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_releases_gate_when_action_throws()
    {
        var registry = new KeyedLeaseGate();

        var bang = async () => await registry.RunAsync<int>(
            "boom",
            TimeSpan.FromSeconds(1),
            _ => throw new InvalidOperationException("intentional"),
            CancellationToken.None);

        await bang.Should().ThrowAsync<InvalidOperationException>();

        // Next call must succeed — gate must have been released even though action threw.
        var ok = await registry.RunAsync<int>(
            "boom",
            TimeSpan.FromSeconds(1),
            _ => new ValueTask<int>(42),
            CancellationToken.None);

        ok.Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_preserves_exclusion_across_last_release_and_reacquire_churn()
    {
        var registry = new KeyedLeaseGate();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var active = 0;
        var overlap = 0;
        var executions = 0;
        const int waves = 2048;

        async ValueTask<int> Enter(CancellationToken ct)
        {
            if (Interlocked.Increment(ref active) != 1)
                Interlocked.Increment(ref overlap);
            try
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                return Interlocked.Increment(ref executions);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        for (var wave = 0; wave < waves; wave++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = registry.RunAsync<int>("churn", TimeSpan.FromSeconds(5), async ct =>
            {
                if (Interlocked.Increment(ref active) != 1)
                    Interlocked.Increment(ref overlap);
                try
                {
                    await start.Task.WaitAsync(ct);
                    return Interlocked.Increment(ref executions);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }, watchdog.Token).AsTask();

            async Task<int> Contend()
            {
                await start.Task.WaitAsync(watchdog.Token);
                return await registry.RunAsync<int>("churn", TimeSpan.FromSeconds(5), Enter, watchdog.Token);
            }

            var second = Task.Run(Contend, watchdog.Token);
            var third = Task.Run(Contend, watchdog.Token);
            start.SetResult();
            // Each settled wave permits retirement; a permanently queued workload would miss that boundary.
            await Task.WhenAll(holder, second, third).WaitAsync(watchdog.Token);
        }

        executions.Should().Be(waves * 3);
        overlap.Should().Be(0, "last release and registration must retain one gate for the same key");
        active.Should().Be(0);
        (await registry.RunAsync<int>("churn", TimeSpan.FromSeconds(1),
            _ => new ValueTask<int>(42), watchdog.Token)).Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_cancelled_waiter_preserves_holder_and_live_waiter_exclusion()
    {
        var registry = new KeyedLeaseGate();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = registry.RunAsync<int>("cancel-waiter", TimeSpan.FromSeconds(1), async ct =>
        {
            await release.Task.WaitAsync(ct);
            return 1;
        }, watchdog.Token).AsTask();
        var cancelledCalls = 0;
        var waiter = registry.RunAsync<int>("cancel-waiter", TimeSpan.FromSeconds(5), _ =>
        {
            Interlocked.Increment(ref cancelledCalls);
            return new ValueTask<int>(2);
        }, cancelled.Token).AsTask();

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        cancelledCalls.Should().Be(0);

        var liveCalls = 0;
        var live = registry.RunAsync<int>("cancel-waiter", TimeSpan.FromSeconds(5), _ =>
        {
            Interlocked.Increment(ref liveCalls);
            return new ValueTask<int>(3);
        }, watchdog.Token).AsTask();
        live.IsCompleted.Should().BeFalse("a cancelled waiter must not release or retire the held gate");
        liveCalls.Should().Be(0);
        (await registry.RunAsync<int>("independent", TimeSpan.FromSeconds(1),
            _ => new ValueTask<int>(4), watchdog.Token)).Should().Be(4);

        release.SetResult();
        (await holder).Should().Be(1);
        (await live).Should().Be(3);
        liveCalls.Should().Be(1);
        (await registry.RunAsync<int>("cancel-waiter", TimeSpan.FromSeconds(1),
            _ => new ValueTask<int>(5), watchdog.Token)).Should().Be(5);
    }

    [Fact]
    public async Task RunAsync_cancelled_action_releases_queued_caller()
    {
        var registry = new KeyedLeaseGate();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        var neverReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = registry.RunAsync<int>("cancel-action", TimeSpan.FromSeconds(1), async ct =>
        {
            ct.Should().Be(cancelled.Token);
            await neverReleased.Task.WaitAsync(ct);
            return 1;
        }, cancelled.Token).AsTask();
        var calls = 0;
        var waiter = registry.RunAsync<int>("cancel-action", TimeSpan.FromSeconds(5), _ =>
        {
            Interlocked.Increment(ref calls);
            return new ValueTask<int>(2);
        }, watchdog.Token).AsTask();
        waiter.IsCompleted.Should().BeFalse();

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holder);
        (await waiter).Should().Be(2);
        calls.Should().Be(1);
        (await registry.RunAsync<int>("cancel-action", TimeSpan.FromSeconds(1),
            _ => new ValueTask<int>(3), watchdog.Token)).Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_precancelled_calls_never_execute_and_allow_key_reuse()
    {
        var registry = new KeyedLeaseGate();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var calls = 0;
        for (var index = 0; index < 16; index++)
        {
            var key = $"precancelled-{index}";
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await registry.RunAsync<int>(key, TimeSpan.FromSeconds(1), _ =>
                {
                    Interlocked.Increment(ref calls);
                    return new ValueTask<int>(0);
                }, cancelled.Token));
            (await registry.RunAsync<int>(key, TimeSpan.FromSeconds(1),
                _ => new ValueTask<int>(index), CancellationToken.None)).Should().Be(index);
        }
        calls.Should().Be(0);
    }
}
