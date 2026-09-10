using Koan.Cache.Abstractions.Policies;
using Koan.Cache.Abstractions.Primitives;
using Koan.Cache.Abstractions.Stores;
using Koan.Core;
using Koan.Core.Capabilities;
using Koan.Core.Hosting.App;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Core;
using Koan.Data.Core.Decorators;
using Koan.Data.Core.Model;
using Koan.Testing.Integration;
using Koan.Tests.Cache.Topology.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Cache.Topology.Specs;

public sealed class ConditionalPublicationCacheSpec
{
    [Fact]
    public async Task Uncached_native_conditional_replace_is_available_in_the_same_host()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan()).StartAsync(TestContext.Current.CancellationToken);
        using var appScope = AppHost.PushScope(host.Services);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var saved = await new UncachedPublication { Revision = "old" }.Save();
        var conditional = Data<UncachedPublication, string>.As<IConditionalWriteRepository<UncachedPublication, string>>();

        conditional.Should().NotBeNull();
        (await conditional!.ConditionalReplaceAsync(
            new UncachedPublication { Id = saved.Id, Revision = "new" }, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<UncachedPublication>(row => row.Revision == "old")))
            .Should().BeTrue();
        (await UncachedPublication.Get(saved.Id))!.Revision.Should().Be("new");
    }

    [Fact]
    public async Task Cached_native_conditional_replace_forwards_its_advertised_capability_and_invalidates_success()
    {
        var cache = new ObservedCacheStore();
        var native = new ConditionalObserver();
        await using var host = await Configure(cache, native).StartAsync(TestContext.Current.CancellationToken);
        using var appScope = AppHost.PushScope(host.Services);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var saved = await new CachedPublication { Revision = "old" }.Save();
        (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("old");
        Data<CachedPublication, string>.Capabilities.Has(DataCaps.Write.ConditionalReplace).Should().BeTrue();
        var warmedKey = cache.FetchedKeys.Last();
        cache.Contains(warmedKey.Value).Should().BeTrue();
        cache.Evictions.Clear();
        var sets = cache.SetCount;

        (await new CachedPublication { Id = saved.Id, Revision = "new" }
            .ReplaceIf(row => row.Revision == "old")).Should().BeTrue();

        native.Calls.Should().Be(1);
        cache.Evictions.Should().Equal(warmedKey);
        cache.Contains(warmedKey.Value).Should().BeFalse();
        cache.SetCount.Should().Be(sets, "a confirmed replacement invalidates without seeding its payload");
        (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("new");
        using (EntityContext.NoCache())
            (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("new");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflict_or_missing_identity_does_not_invalidate_or_seed(bool missing)
    {
        var cache = new ObservedCacheStore();
        var native = new ConditionalObserver();
        await using var host = await Configure(cache, native).StartAsync(TestContext.Current.CancellationToken);
        using var appScope = AppHost.PushScope(host.Services);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var saved = await new CachedPublication { Revision = "old" }.Save();
        (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("old");
        var warmedKey = cache.FetchedKeys.Last();
        cache.Evictions.Clear();
        var sets = cache.SetCount;
        var id = missing ? "absent" : saved.Id;

        (await new CachedPublication { Id = id, Revision = "new" }
            .ReplaceIf(row => row.Revision == "stale")).Should().BeFalse();

        native.Calls.Should().Be(1);
        cache.Evictions.Should().BeEmpty();
        cache.SetCount.Should().Be(sets);
        cache.Contains(warmedKey.Value).Should().BeTrue();
        (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("old");
        using (EntityContext.NoCache())
        {
            (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("old");
            if (missing) (await CachedPublication.Get(id)).Should().BeNull();
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("named")]
    [InlineData("inherit")]
    public async Task Explicit_and_inherited_partition_selection_invalidate_only_the_selected_identity(string selection)
    {
        var cache = new ObservedCacheStore();
        var native = new ConditionalObserver();
        await using var host = await Configure(cache, native).StartAsync(TestContext.Current.CancellationToken);
        using var appScope = AppHost.PushScope(host.Services);
        var named = Guid.NewGuid().ToString("N");
        using var route = EntityContext.With(adapter: "inmemory", partition: named);
        const string id = "same-identity";
        await new CachedPublication { Id = id, Revision = "named-old" }.Save();
        (await CachedPublication.Get(id))!.Revision.Should().Be("named-old");
        var namedKey = cache.FetchedKeys.Last();
        CacheKey defaultKey;
        using (EntityContext.Partition(""))
        {
            await new CachedPublication { Id = id, Revision = "default-old" }.Save();
            (await CachedPublication.Get(id))!.Revision.Should().Be("default-old");
            defaultKey = cache.FetchedKeys.Last();
        }
        namedKey.Should().NotBe(defaultKey);
        cache.Evictions.Clear();
        var expected = selection == "default" ? "default-old" : "named-old";
        var partition = selection == "default" ? "" : selection == "named" ? named : null;
        using (EntityContext.Partition(selection == "named" ? "" : named))
        {
            var caller = EntityContext.Current;
            (await new CachedPublication { Id = id, Revision = "published" }
                .ReplaceIf(row => row.Revision == expected, partition: partition)).Should().BeTrue();
            EntityContext.Current.Should().BeSameAs(caller);
        }

        native.Calls.Should().Be(1);
        cache.Evictions.Should().Equal(selection == "default" ? defaultKey : namedKey);
        cache.Contains((selection == "default" ? namedKey : defaultKey).Value).Should().BeTrue();
        (await CachedPublication.Get(id))!.Revision.Should().Be(selection == "default" ? "named-old" : "published");
        using (EntityContext.Partition(""))
            (await CachedPublication.Get(id))!.Revision.Should().Be(selection == "default" ? "published" : "default-old");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalidation_failure_after_native_success_keeps_the_commit_and_never_retries(bool cancel)
    {
        var cache = new ObservedCacheStore();
        var native = new ConditionalObserver();
        await using var host = await Configure(cache, native).StartAsync(TestContext.Current.CancellationToken);
        using var appScope = AppHost.PushScope(host.Services);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var caller = EntityContext.Current;
        var saved = await new CachedPublication { Revision = "old" }.Save();
        (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("old");
        var warmedKey = cache.FetchedKeys.Last();
        cache.Evictions.Clear();
        var sets = cache.SetCount;
        using var cancellation = new CancellationTokenSource();
        cache.BeforeRemove = async ct =>
        {
            using (EntityContext.NoCache())
                (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("published",
                    "the fault is injected only after the real native write completed");
            if (cancel)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("cache invalidation unavailable after commit");
        };

        var error = await Record.ExceptionAsync(() => new CachedPublication { Id = saved.Id, Revision = "published" }
            .ReplaceIf(row => row.Revision == "old", ct: cancellation.Token));

        if (cancel) error.Should().BeAssignableTo<OperationCanceledException>();
        else error.Should().BeOfType<IOException>();
        EntityContext.Current.Should().BeSameAs(caller);
        native.Calls.Should().Be(1);
        cache.Evictions.Should().Equal(warmedKey);
        cache.SetCount.Should().Be(sets);
        cache.Contains(warmedKey.Value).Should().BeTrue("a failed invalidation is not a cache coherence guarantee");
        cache.BeforeRemove = null;
        using (EntityContext.NoCache())
            (await CachedPublication.Get(saved.Id))!.Revision.Should().Be("published");
    }

    private static KoanIntegrationHost.Builder Configure(ObservedCacheStore cache, ConditionalObserver native)
        => KoanIntegrationHost.Configure().ConfigureServices(services =>
        {
            services.AddSingleton<IDataRepositoryDecorator>(native);
            services.AddSingleton<ICacheStore>(cache);
            services.AddKoan();
        });

    [ProviderPriority(500)]
    private sealed class ObservedCacheStore() : FakeCacheStore("conditional-observer", CacheStorePlacement.Local), ICacheStore
    {
        public List<CacheKey> FetchedKeys { get; } = [];
        public List<CacheKey> Evictions { get; } = [];
        public Func<CancellationToken, Task>? BeforeRemove { get; set; }
        ValueTask<CacheFetchResult> ICacheStore.Fetch(CacheKey key, CacheReadOptions options, CancellationToken ct)
        {
            FetchedKeys.Add(key);
            return Fetch(key, options, ct);
        }
        async ValueTask<bool> ICacheStore.Remove(CacheKey key, CancellationToken ct)
        {
            Evictions.Add(key);
            if (BeforeRemove is not null) await BeforeRemove(ct);
            return await Remove(key, ct);
        }
    }

    // This observer forwards to the real connector and sits inside the production cache decorator.
    // It counts dispatches without supplying, simulating or retrying the native write result.
    private sealed class ConditionalObserver : IDataRepositoryDecorator
    {
        public int Calls;
        public object? TryDecorate(Type entityType, Type keyType, object repository, IServiceProvider services)
            => entityType == typeof(CachedPublication)
                ? new ObservedRepository((IDataRepository<CachedPublication, string>)repository, this)
                : null;
    }

    private sealed class ObservedRepository(IDataRepository<CachedPublication, string> inner, ConditionalObserver observer)
        : IDataRepository<CachedPublication, string>, IConditionalWriteRepository<CachedPublication, string>, IDescribesCapabilities
    {
        public void Describe(ICapabilities caps) => DataCaps.Describe(inner, inner.GetType().Name).CopyInto(caps);
        public Task<bool> ConditionalReplaceAsync(CachedPublication model, Filter guard, CancellationToken ct = default)
        {
            Interlocked.Increment(ref observer.Calls);
            return ((IConditionalWriteRepository<CachedPublication, string>)inner).ConditionalReplaceAsync(model, guard, ct);
        }
        public Task EnsureReady(CancellationToken ct = default) => inner.EnsureReady(ct);
        public Task<CachedPublication?> Get(string id, CancellationToken ct = default) => inner.Get(id, ct);
        public Task<IReadOnlyList<CachedPublication?>> GetMany(IEnumerable<string> ids, CancellationToken ct = default) => inner.GetMany(ids, ct);
        public Task<CachedPublication> Upsert(CachedPublication model, CancellationToken ct = default) => inner.Upsert(model, ct);
        public Task<bool> Delete(string id, CancellationToken ct = default) => inner.Delete(id, ct);
        public Task<int> UpsertMany(IEnumerable<CachedPublication> models, CancellationToken ct = default) => inner.UpsertMany(models, ct);
        public Task<int> DeleteMany(IEnumerable<string> ids, CancellationToken ct = default) => inner.DeleteMany(ids, ct);
        public Task<int> DeleteAll(CancellationToken ct = default) => inner.DeleteAll(ct);
        public Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default) => inner.RemoveAll(strategy, ct);
        public IBatchSet<CachedPublication, string> CreateBatch() => inner.CreateBatch();
    }
}

public sealed class UncachedPublication : Entity<UncachedPublication>
{
    public string Revision { get; set; } = "";
}

[Cacheable]
public sealed class CachedPublication : Entity<CachedPublication>
{
    public string Revision { get; set; } = "";
}
