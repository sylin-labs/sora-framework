using Koan.Cache.Abstractions.Policies;
using Koan.Core;
using Koan.Core.Diagnostics;
using Koan.Core.Hosting.App;
using Koan.Data.Core.Model;
using Koan.Data.Core;
using Koan.Data.Abstractions;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Cache.Topology.Specs;

public sealed class EntityCacheCompositionSpec
{
    [Fact]
    public async Task Insert_is_forwarded_and_conflict_preserves_the_cached_original()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan()).StartAsync(TestContext.Current.CancellationToken);
        using var appScope = AppHost.PushScope(host.Services);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<InsertedCacheEntity, string>();
        var inserts = (IInsertOnlyRepository<InsertedCacheEntity, string>)repository;
        var first = await inserts.Insert(new InsertedCacheEntity { Value = "original" });
        first.Outcome.Should().Be(MutationOutcome.Inserted);
        (await InsertedCacheEntity.Get(first.Key))!.Value.Should().Be("original");
        var conflict = await inserts.Insert(new InsertedCacheEntity { Id = first.Key, Value = "replacement" });
        conflict.Outcome.Should().Be(MutationOutcome.Conflict);
        conflict.Entity.Should().BeNull();
        (await InsertedCacheEntity.Get(first.Key))!.Value.Should().Be("original");
        using (EntityContext.NoCache())
            (await InsertedCacheEntity.Get(first.Key))!.Value.Should().Be("original");
    }

    [Fact]
    public async Task Cached_identity_does_not_bypass_unsupported_counterpart_query()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan()).StartAsync(TestContext.Current.CancellationToken);
        using var appScope = AppHost.PushScope(host.Services);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var row = await new InsertedCacheEntity { Value = "cached" }.Save();
        (await InsertedCacheEntity.Get(row.Id))!.Value.Should().Be("cached");
        var filter = Koan.Data.Abstractions.Filtering.Filter.SameIdIn<InsertedCacheEntity>(item => true, "");
        await ((Func<Task>)(() => InsertedCacheEntity.AllWithCount(QueryDefinition.All.Where(filter))))
            .Should().ThrowAsync<NotSupportedException>();
        (await InsertedCacheEntity.Get(row.Id))!.Value.Should().Be("cached");
    }

    [Fact]
    public async Task Startup_reports_the_effective_entity_cache_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan())
            .StartAsync(ct);
        using var appScope = AppHost.PushScope(host.Services);

        var facts = host.Services.GetRequiredService<IKoanRuntimeFacts>().Current.Facts;

        facts.Should().Contain(fact =>
            fact.Code == "koan.cache.policies.discovered"
            && fact.Summary.Contains("Entity entry plan", StringComparison.Ordinal)
            && !fact.Summary.StartsWith("Koan materialized 0 ", StringComparison.Ordinal));
        facts.Should().Contain(fact =>
            fact.Code == "koan.cache.entity-plan.resolved"
            && fact.Subject.EndsWith(typeof(ReportedCacheEntity).FullName!, StringComparison.Ordinal)
            && fact.Summary.Contains(CacheableAttribute.DefaultKeyTemplate, StringComparison.Ordinal));
        facts.Should().Contain(fact =>
            fact.Code == "koan.cache.local.selected"
            && fact.Subject == "cache:local"
            && fact.ReasonCode == "priority-selection");
    }
}

[Cacheable]
public sealed class ReportedCacheEntity : Entity<ReportedCacheEntity>;

[Cacheable]
public sealed class InsertedCacheEntity : Entity<InsertedCacheEntity>
{
    public string Value { get; set; } = "";
}
