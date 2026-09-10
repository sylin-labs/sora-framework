using Koan.Cache;
using Koan.Cache.Abstractions.Primitives;
using Koan.Cache.Abstractions.Stores;
using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Testing.Integration;
using Koan.Tests.Cache.Topology.Support;
using Microsoft.Extensions.DependencyInjection;
using CacheFacade = Koan.Cache.Cache;

namespace Koan.Tests.Cache.Topology.Specs;

public sealed class CacheRemovalOrderingSpec
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(5);
    private const string ShortGateSetting = "Cache:DefaultSingleflightTimeout";
    private const string ShortGateValue = "00:00:00.250";

    private const string Key = "spec:cache-removal:order";
    private const string OtherKey = "spec:cache-removal:other";
    private const string Stale = "old";
    private const string Fresh = "new";

    [Fact]
    public async Task Remove_waits_for_inflight_factory_and_prevents_late_repopulation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);
        var store = new RemovalObservingStore("removal-order-local");
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICacheStore>(store);
                services.AddKoan();
            })
            .StartAsync(ct);

        using (AppHost.PushScope(host.Services))
        {
            var client = host.Services.GetRequiredService<ICacheClient>();

            var factoryStarted = Started();
            var releaseFactory = Started();
            var factoryTask = CacheFacade.WithJson<string>(Key).GetOrAdd(async _ =>
            {
                factoryStarted.TrySetResult();
                await releaseFactory.Task.WaitAsync(deadline.Token);
                return Stale;
            }, deadline.Token).AsTask();
            Task<bool>? removeTask = null;

            try
            {
                await factoryStarted.Task.WaitAsync(deadline.Token);

                removeTask = client.Remove(new CacheKey(Key), deadline.Token).AsTask();
                removeTask.IsCompleted.Should().BeFalse(
                    "removal must not complete while a GetOrAdd factory for the same key is still running");
            }
            finally
            {
                releaseFactory.TrySetResult();
                await DrainAsync(deadline.Token, removeTask, factoryTask);
            }

            (await factoryTask).Should().Be(Stale);
            (await removeTask!).Should().BeTrue(
                "the factory republished its pre-removal value, so the completed removal evicts it");
            store.RemoveCount.Should().Be(1);

            var probeRuns = 0;
            var probe = await CacheFacade.WithJson<string>(Key).GetOrAdd(_ =>
            {
                Interlocked.Increment(ref probeRuns);
                return ValueTask.FromResult<string?>(Fresh);
            }, deadline.Token);

            probe.Should().Be(Fresh);
            probeRuns.Should().Be(1, "a completed removal leaves nothing for the earlier factory to have republished");
        }
    }

    [Fact]
    public async Task Layered_read_backfill_and_removal_share_one_ordering()
    {
        var ct = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);
        var local = new RemovalObservingStore("removal-order-l1");
        var remote = new FakeCacheStore("removal-order-l2", CacheStorePlacement.Remote);
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICacheStore>(local);
                services.AddSingleton<ICacheStore>(remote);
                services.AddKoan();
            })
            .StartAsync(ct);

        using (AppHost.PushScope(host.Services))
        {
            var client = host.Services.GetRequiredService<ICacheClient>();

            await CacheFacade.WithJson<string>(Key).WithTier(CacheTier.RemoteOnly).Set(Stale, deadline.Token);
            (await CacheFacade.WithJson<string>(Key).WithTier(CacheTier.LocalOnly).Get(deadline.Token)).Should().BeNull(
                "the scenario starts with L1 cold and only L2 warm");

            var backfillStarted = Started();
            var releaseBackfill = Started();
            local.BeforeSet = async _ =>
            {
                backfillStarted.TrySetResult();
                await releaseBackfill.Task.WaitAsync(deadline.Token);
            };

            var readerRuns = 0;
            var readerTask = CacheFacade.WithJson<string>(Key).GetOrAdd(_ =>
            {
                Interlocked.Increment(ref readerRuns);
                return ValueTask.FromResult<string?>(Fresh);
            }, deadline.Token).AsTask();
            Task<bool>? removeTask = null;

            try
            {
                await backfillStarted.Task.WaitAsync(deadline.Token);

                removeTask = client.Remove(new CacheKey(Key), deadline.Token).AsTask();
                removeTask.IsCompleted.Should().BeFalse(
                    "removal must not complete while a layered GetOrAdd read/backfill for the same key is in flight");
            }
            finally
            {
                local.BeforeSet = null;
                releaseBackfill.TrySetResult();
                await DrainAsync(deadline.Token, removeTask, readerTask);
            }

            (await readerTask).Should().Be(Stale,
                "a reader already in flight may still receive the pre-removal value");
            readerRuns.Should().Be(0, "the layered read is served from the L2 hit without running the factory");
            (await removeTask!).Should().BeTrue();
            local.RemoveCount.Should().Be(1, "the completed removal evicts the backfilled L1 value");

            var probeRuns = 0;
            var probe = await CacheFacade.WithJson<string>(Key).GetOrAdd(_ =>
            {
                Interlocked.Increment(ref probeRuns);
                return ValueTask.FromResult<string?>(Fresh);
            }, deadline.Token);

            probe.Should().Be(Fresh);
            probeRuns.Should().Be(1, "no late L1 backfill may survive a completed removal");
        }
    }

    [Fact]
    public async Task Layered_write_failure_settles_pending_tier_writes_before_removal_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);
        var local = new RemovalObservingStore("removal-order-write-l1");
        var remote = new RemovalObservingStore("removal-order-write-l2", CacheStorePlacement.Remote);
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting(ShortGateSetting, ShortGateValue)
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICacheStore>(local);
                services.AddSingleton<ICacheStore>(remote);
                services.AddKoan();
            })
            .StartAsync(ct);

        using (AppHost.PushScope(host.Services))
        {
            var client = host.Services.GetRequiredService<ICacheClient>();

            var l2WriteStarted = Started();
            var releaseL2Write = Started();
            local.BeforeSet = async _ =>
            {
                await l2WriteStarted.Task.WaitAsync(deadline.Token);
                throw new IOException("L1 write failed while the L2 write was still in flight");
            };
            remote.BeforeSet = async _ =>
            {
                l2WriteStarted.TrySetResult();
                await releaseL2Write.Task.WaitAsync(deadline.Token);
            };

            var factoryTask = CacheFacade.WithJson<string>(Key).GetOrAdd(
                _ => ValueTask.FromResult<string?>(Stale), deadline.Token).AsTask();
            Task<bool>? removeTask = null;
            Exception? factoryError = null;

            try
            {
                await l2WriteStarted.Task.WaitAsync(deadline.Token);

                removeTask = client.Remove(new CacheKey(Key), deadline.Token).AsTask();
                var removeError = await Record.ExceptionAsync(() => removeTask);
                removeError.Should().BeAssignableTo<TimeoutException>(
                    "a removal cannot acquire the key while an earlier layered write is still settling");
                local.RemoveCount.Should().Be(0,
                    "the timed-out removal must not attempt eviction while the write is in flight");
                remote.RemoveCount.Should().Be(0,
                    "the timed-out removal must not attempt eviction while the write is in flight");
            }
            finally
            {
                local.BeforeSet = null;
                remote.BeforeSet = null;
                releaseL2Write.TrySetResult();
                await DrainAsync(deadline.Token, removeTask);
                factoryError = await Record.ExceptionAsync(() => factoryTask);
            }

            factoryError.Should().BeAssignableTo<IOException>(
                "the tier write failure surfaces to the factory caller only after every started tier write settled");
            (await client.Remove(new CacheKey(Key), deadline.Token)).Should().BeTrue(
                "an ordinary removal after the settled write evicts the stale value it published");

            var probeRuns = 0;
            var probe = await CacheFacade.WithJson<string>(Key).GetOrAdd(_ =>
            {
                Interlocked.Increment(ref probeRuns);
                return ValueTask.FromResult<string?>(Fresh);
            }, deadline.Token);

            probe.Should().Be(Fresh, "a fresh probe after the completed removal cannot retrieve the stale tier write");
            probeRuns.Should().Be(1, "the late-settling L2 write left nothing readable after the ordered removal");
        }
    }

    [Fact]
    public async Task Unrelated_key_progresses_while_a_factory_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);
        var store = new RemovalObservingStore("removal-order-parallel");
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICacheStore>(store);
                services.AddKoan();
            })
            .StartAsync(ct);

        using (AppHost.PushScope(host.Services))
        {
            var client = host.Services.GetRequiredService<ICacheClient>();

            var factoryStarted = Started();
            var releaseFactory = Started();
            var heldTask = CacheFacade.WithJson<string>(Key).GetOrAdd(async _ =>
            {
                factoryStarted.TrySetResult();
                await releaseFactory.Task.WaitAsync(deadline.Token);
                return Stale;
            }, deadline.Token).AsTask();

            try
            {
                await factoryStarted.Task.WaitAsync(deadline.Token);

                var otherRuns = 0;
                (await CacheFacade.WithJson<string>(OtherKey).GetOrAdd(_ =>
                {
                    Interlocked.Increment(ref otherRuns);
                    return ValueTask.FromResult<string?>("other-1");
                }, deadline.Token)).Should().Be("other-1");
                (await client.Remove(new CacheKey(OtherKey), deadline.Token)).Should().BeTrue();
                (await CacheFacade.WithJson<string>(OtherKey).Get(deadline.Token)).Should().BeNull(
                    "the completed other-key removal evicted it");
                (await CacheFacade.WithJson<string>(OtherKey).GetOrAdd(_ =>
                {
                    Interlocked.Increment(ref otherRuns);
                    return ValueTask.FromResult<string?>("other-2");
                }, deadline.Token)).Should().Be("other-2");
                otherRuns.Should().Be(2, "the unrelated key completed two full factory cycles while the first factory was held");

                (await CacheFacade.WithJson<string>(Key).Get(deadline.Token)).Should().BeNull(
                    "the held factory has not published yet, and the other key's removal must not touch it");
            }
            finally
            {
                releaseFactory.TrySetResult();
                await DrainAsync(deadline.Token, heldTask);
            }

            (await heldTask).Should().Be(Stale);
        }
    }

    [Fact]
    public async Task Cancelled_removal_waiting_for_the_lease_fails_boundedly_without_evicting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);
        var store = new RemovalObservingStore("removal-order-cancel");
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICacheStore>(store);
                services.AddKoan();
            })
            .StartAsync(ct);

        using (AppHost.PushScope(host.Services))
        {
            var client = host.Services.GetRequiredService<ICacheClient>();

            var factoryStarted = Started();
            var releaseFactory = Started();
            var heldTask = CacheFacade.WithJson<string>(Key).GetOrAdd(async _ =>
            {
                factoryStarted.TrySetResult();
                await releaseFactory.Task.WaitAsync(deadline.Token);
                return Stale;
            }, deadline.Token).AsTask();
            Task<bool>? removeTask = null;

            try
            {
                await factoryStarted.Task.WaitAsync(deadline.Token);

                using var cancelRemoval = new CancellationTokenSource();
                removeTask = client.Remove(new CacheKey(Key), cancelRemoval.Token).AsTask();
                removeTask.IsCompleted.Should().BeFalse(
                    "a removal started while a same-key factory holds the lease must wait for it");

                cancelRemoval.Cancel();
                var removeError = await Record.ExceptionAsync(() => removeTask);
                removeError.Should().BeAssignableTo<OperationCanceledException>(
                    "the waiting removal fails on its caller's cancellation instead of proceeding");
                store.RemoveCount.Should().Be(0, "the cancelled removal must not attempt eviction while the lease is held");
            }
            finally
            {
                releaseFactory.TrySetResult();
                await DrainAsync(deadline.Token, removeTask, heldTask);
            }

            (await heldTask).Should().Be(Stale);
            (await CacheFacade.WithJson<string>(Key).Get(deadline.Token)).Should().Be(Stale,
                "the factory's published result survives the failed removal");
            (await client.Remove(new CacheKey(Key), deadline.Token)).Should().BeTrue("a later ordinary removal still works");

            var probeRuns = 0;
            var probe = await CacheFacade.WithJson<string>(Key).GetOrAdd(_ =>
            {
                Interlocked.Increment(ref probeRuns);
                return ValueTask.FromResult<string?>(Fresh);
            }, deadline.Token);

            probe.Should().Be(Fresh);
            probeRuns.Should().Be(1, "the ordinary removal evicted the factory's value and nothing republished it");
        }
    }

    [Fact]
    public async Task Removal_gate_timeout_abandons_without_a_late_eviction()
    {
        var ct = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);
        var store = new RemovalObservingStore("removal-order-timeout");
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting(ShortGateSetting, ShortGateValue)
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICacheStore>(store);
                services.AddKoan();
            })
            .StartAsync(ct);

        using (AppHost.PushScope(host.Services))
        {
            var client = host.Services.GetRequiredService<ICacheClient>();

            var factoryStarted = Started();
            var releaseFactory = Started();
            var heldTask = CacheFacade.WithJson<string>(Key).GetOrAdd(async _ =>
            {
                factoryStarted.TrySetResult();
                await releaseFactory.Task.WaitAsync(deadline.Token);
                return Stale;
            }, deadline.Token).AsTask();
            Task<bool>? removeTask = null;

            try
            {
                await factoryStarted.Task.WaitAsync(deadline.Token);

                removeTask = client.Remove(new CacheKey(Key), deadline.Token).AsTask();
                var removeError = await Record.ExceptionAsync(() => removeTask);
                removeError.Should().BeAssignableTo<TimeoutException>(
                    "a removal that cannot acquire the lease within its configured gate timeout fails honestly");
                store.RemoveCount.Should().Be(0, "the timed-out removal must not attempt eviction while the lease is held");
            }
            finally
            {
                releaseFactory.TrySetResult();
                await DrainAsync(deadline.Token, removeTask, heldTask);
            }

            (await heldTask).Should().Be(Stale);
            (await CacheFacade.WithJson<string>(Key).Get(deadline.Token)).Should().Be(Stale,
                "the factory's published result survives the abandoned removal");
            store.RemoveCount.Should().Be(0, "the abandoned removal never evicts later");
            (await client.Remove(new CacheKey(Key), deadline.Token)).Should().BeTrue("a later ordinary removal still works");

            var probeRuns = 0;
            var probe = await CacheFacade.WithJson<string>(Key).GetOrAdd(_ =>
            {
                Interlocked.Increment(ref probeRuns);
                return ValueTask.FromResult<string?>(Fresh);
            }, deadline.Token);

            probe.Should().Be(Fresh);
            probeRuns.Should().Be(1, "the ordinary removal evicted the factory's value and nothing republished it");
        }
    }

    [Fact]
    public async Task Factory_reentrant_same_key_removal_fails_boundedly_without_bypass()
    {
        var ct = TestContext.Current.CancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TestDeadline);
        var store = new RemovalObservingStore("removal-order-reentrant");
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting(ShortGateSetting, ShortGateValue)
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICacheStore>(store);
                services.AddKoan();
            })
            .StartAsync(ct);

        using (AppHost.PushScope(host.Services))
        {
            var client = host.Services.GetRequiredService<ICacheClient>();
            using var live = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);

            Exception? innerRemovalError = null;
            var factoryStarted = Started();
            var heldTask = CacheFacade.WithJson<string>(Key).GetOrAdd(async _ =>
            {
                factoryStarted.TrySetResult();
                innerRemovalError = await Record.ExceptionAsync(
                    () => client.Remove(new CacheKey(Key), live.Token).AsTask());
                return Stale;
            }, deadline.Token).AsTask();

            try
            {
                await factoryStarted.Task.WaitAsync(deadline.Token);
            }
            finally
            {
                await DrainAsync(deadline.Token, heldTask);
                live.Cancel();
                await DrainAsync(deadline.Token, heldTask);
            }

            (await heldTask.WaitAsync(DrainBound, deadline.Token)).Should().Be(Stale,
                "the factory may still return its value because its reentrant removal did not succeed");
            innerRemovalError.Should().BeAssignableTo<TimeoutException>(
                "a reentrant same-key removal cannot reacquire its own lease and fails on the configured gate timeout");
            store.RemoveCount.Should().Be(0, "the reentrant removal must not evict from inside the factory's own lease");

            (await client.Remove(new CacheKey(Key), deadline.Token)).Should().BeTrue("an ordinary removal after the factory finished works");
            store.RemoveCount.Should().Be(1);

            var probeRuns = 0;
            var probe = await CacheFacade.WithJson<string>(Key).GetOrAdd(_ =>
            {
                Interlocked.Increment(ref probeRuns);
                return ValueTask.FromResult<string?>(Fresh);
            }, deadline.Token);

            probe.Should().Be(Fresh);
            probeRuns.Should().Be(1, "the ordinary removal evicted the factory's value and nothing republished it");
        }
    }

    private static TaskCompletionSource Started()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task DrainAsync(CancellationToken deadlineToken, params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null)
                continue;

            try
            {
                await task.WaitAsync(DrainBound, deadlineToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }
    }

    /// <summary>
    /// Private test double: FakeCacheStore with a write-side hold so tier writes, an L2-hit L1
    /// backfill or either side of a layered publish, can be parked or failed deterministically.
    /// Existing Support stores expose no Set hook, and the comparable observer in
    /// ConditionalPublicationCacheSpec is private to that file.
    /// </summary>
    [ProviderPriority(500)]
    private sealed class RemovalObservingStore : FakeCacheStore, ICacheStore
    {
        public RemovalObservingStore(string name, CacheStorePlacement placement = CacheStorePlacement.Local)
            : base(name, placement)
        {
        }

        public Func<CancellationToken, Task>? BeforeSet { get; set; }

        async ValueTask ICacheStore.Set(CacheKey key, CacheValue value, CacheWriteOptions options, CancellationToken ct)
        {
            if (BeforeSet is not null)
                await BeforeSet(ct);
            await Set(key, value, options, ct);
        }
    }
}
