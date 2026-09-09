using Koan.Core.Capabilities;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Koan.Data.Connector.Mongo.Tests.Specs;

public sealed class MongoCounterpartScopeSpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output), IDisposable
{
    private static readonly AsyncLocal<string?> Tenant = new();

    public void Dispose()
    {
        Tenant.Value = null;
        ManagedFieldRegistry.Reset();
    }

    [Theory]
    [InlineData("bob")]
    [InlineData(null)]
    public async Task Target_cannot_change_managed_isolation_or_cross_match_scoped_identity(string? targetTenant)
    {
        RequireBackingStore();
        var canonical = NewPartition("canonical");
        var translated = NewPartition("translated");
        var changeBinding = false;
        await using var host = await BootAsync(
            () => RegisterTenant(() => changeBinding && EntityContext.Current?.Partition == canonical ? targetTenant : Tenant.Value),
            services => services.AddSingleton<IReadFilterContributor>(new ScopedReadContributor(() =>
                EntityContext.Current?.Partition == canonical ? Filter.Eq(nameof(ScopedCatalog.Allowed), true) : null)));
        Tenant.Value = "alice";
        await Seed(canonical, new ScopedCatalog { Id = "visible", Allowed = true }, new ScopedCatalog { Id = "scope-hidden", Allowed = false });
        Tenant.Value = "bob";
        await Seed(canonical, new ScopedCatalog { Id = "foreign-tenant", Allowed = true });
        Tenant.Value = "alice";
        await Seed(translated, new ScopedCatalog { Id = "visible", Allowed = false },
            new ScopedCatalog { Id = "scope-hidden", Allowed = true }, new ScopedCatalog { Id = "foreign-tenant", Allowed = true });
        var query = QueryDefinition.All.ForPartition(translated)
            .Where(Filter.SameIdIn<ScopedCatalog>(item => true, canonical)).WithCountStrategy(CountStrategy.Exact);
        var result = await Data<ScopedCatalog, string>.QueryWithCount(query);
        result.TotalCount.Should().Be(1);
        result.Items.Single().Id.Should().Be("visible");

        var database = new MongoClient(Fixture.ConnectionString).GetDatabase(Fixture.Database);
        var factory = (INamingProvider)new MongoAdapterFactory();
        var sourceName = factory.ResolveStorage(typeof(ScopedCatalog), canonical, host.Services);
        var outerName = factory.ResolveStorage(typeof(ScopedCatalog), translated, host.Services);
        var rawCanonical = await database.GetCollection<BsonDocument>(sourceName)
            .Find(new BsonDocument("_id", "foreign-tenant")).SingleAsync();
        var rawTranslated = await database.GetCollection<BsonDocument>(outerName)
            .Find(new BsonDocument("_id", "foreign-tenant")).SingleAsync();
        rawCanonical["__counterpart_tenant"].AsString.Should().Be("bob");
        rawTranslated["__counterpart_tenant"].AsString.Should().Be("alice");

        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        try
        {
            using var caller = EntityContext.With(partition: "caller");
            changeBinding = true;
            await FluentActions.Invoking(() => Data<ScopedCatalog, string>.QueryWithCount(query))
                .Should().ThrowAsync<NotSupportedException>().WithMessage("*isolation bindings*");
            EntityContext.Current?.Partition.Should().Be("caller");
            var candidateCommands = await database.GetCollection<BsonDocument>("system.profile")
                .Find(new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("command.aggregate", outerName),
                    new BsonDocument("command.find", outerName)
                })).ToListAsync();
            candidateCommands.Should().BeEmpty();
        }
        finally
        {
            changeBinding = false;
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        }
        (await Data<ScopedCatalog, string>.Count(query)).Should().Be(1);
    }

    [Fact]
    public async Task Multiple_targets_restore_context_and_evidence_tracks_managed_and_contributor_scope()
    {
        RequireBackingStore();
        var canonical = NewPartition("canonical");
        var secondary = NewPartition("secondary");
        var translated = NewPartition("translated");
        var requiredVisibility = true;
        await using var host = await BootAsync(
            () => RegisterTenant(() => Tenant.Value),
            services => services.AddSingleton<IReadFilterContributor>(new ScopedReadContributor(() =>
                Filter.Eq(nameof(ScopedCatalog.Allowed), requiredVisibility))));
        Tenant.Value = "alice";
        await Seed(canonical, new ScopedCatalog { Id = "a", Allowed = true });
        await Seed(secondary, new ScopedCatalog { Id = "a", Allowed = true });
        await Seed(translated, new ScopedCatalog { Id = "a", Allowed = true });
        using var caller = EntityContext.With(partition: "caller");
        var query = QueryDefinition.All.ForPartition(translated).Where(Filter.All(
            Filter.SameIdIn<ScopedCatalog>(item => true, canonical),
            Filter.SameIdIn<ScopedCatalog>(item => true, secondary))).WithCountStrategy(CountStrategy.Exact);
        var result = await Data<ScopedCatalog, string>.QueryWithCount(query);
        result.TotalCount.Should().Be(1);
        result.Items.Single().Id.Should().Be("a");
        EntityContext.Current?.Partition.Should().Be("caller");
        result.ReadEvidence.Should().NotBeNull();
        var evidence = result.ReadEvidence!;
        evidence.Partition.Should().Be(translated);
        evidence.EntityType.Should().Be(typeof(ScopedCatalog));
        evidence.CoversRows(result.Items.Cast<object>()).Should().BeTrue();
        evidence.CoversRows([new ScopedCatalog { Id = "a", Allowed = true }]).Should().BeFalse();
        evidence.CoversRows([new ScopedCatalog { Id = "foreign" }]).Should().BeFalse();
        evidence.IsCurrentScope().Should().BeTrue();
        Tenant.Value = "bob";
        evidence.IsCurrentScope().Should().BeFalse();
        Tenant.Value = "alice";
        requiredVisibility = false;
        evidence.IsCurrentScope().Should().BeFalse();
        requiredVisibility = true;
        evidence.IsCurrentScope().Should().BeTrue();
        EntityContext.Current?.Partition.Should().Be("caller");
        // A following operation proves all target horizon leases were unwound.
        (await Data<ScopedCatalog, string>.Count(query)).Should().Be(1);
        EntityContext.Current?.Partition.Should().Be("caller");
    }

    [Fact]
    public async Task Date_isolation_preserves_native_kind_in_binding_and_evidence()
    {
        RequireBackingStore();
        var canonical = NewPartition("date-canonical");
        var translated = NewPartition("date-translated");
        var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Local);
        var current = utc;
        var changeTarget = false;
        await using var host = await BootAsync(() => ManagedFieldRegistry.Register(new ManagedFieldDescriptor(
            StorageName: "__counterpart_date", ClrType: typeof(DateTime),
            ValueProvider: () => changeTarget && EntityContext.Current?.Partition == canonical ? local : current,
            AppliesTo: type => type == typeof(ScopedCatalog), RequiredCapability: DataCaps.Isolation.RowScoped)));
        await Seed(canonical, new ScopedCatalog { Id = "a" });
        await Seed(translated, new ScopedCatalog { Id = "a" });
        var query = QueryDefinition.All.ForPartition(translated)
            .Where(Filter.SameIdIn<ScopedCatalog>(row => true, canonical));
        var result = await Data<ScopedCatalog, string>.QueryWithCount(query);
        result.Items.Should().ContainSingle();
        result.ReadEvidence!.IsCurrentScope().Should().BeTrue();
        current = local;
        result.ReadEvidence.IsCurrentScope().Should().BeFalse();
        current = utc;
        changeTarget = true;
        await FluentActions.Invoking(() => Data<ScopedCatalog, string>.QueryWithCount(query))
            .Should().ThrowAsync<NotSupportedException>().WithMessage("*isolation bindings*");
    }

    [Fact]
    public async Task Distinct_predicates_in_one_target_partition_share_one_scope_capture()
    {
        RequireBackingStore();
        var canonical = NewPartition("shared-canonical");
        var translated = NewPartition("shared-translated");
        var captures = new Dictionary<string, int>();
        await using var host = await BootAsync(services =>
            services.AddSingleton<IReadFilterContributor>(new ScopedReadContributor(() =>
            {
                var partition = EntityContext.Current?.Partition ?? "";
                captures[partition] = captures.GetValueOrDefault(partition) + 1;
                return Filter.Eq(nameof(ScopedCatalog.Allowed), true);
            })));
        await Seed(canonical, new ScopedCatalog { Id = "a", Allowed = true });
        await Seed(translated, new ScopedCatalog { Id = "a", Allowed = true });
        captures.Clear();
        var query = QueryDefinition.All.ForPartition(translated).Where(Filter.All(
            Filter.SameIdIn<ScopedCatalog>(row => true, canonical),
            Filter.SameIdIn<ScopedCatalog>(row => row.Allowed, canonical)));
        (await Data<ScopedCatalog, string>.Count(query)).Should().Be(1);
        captures[translated].Should().Be(1);
        captures[canonical].Should().Be(1);
    }

    private static void RegisterTenant(Func<object?> value) => ManagedFieldRegistry.Register(new ManagedFieldDescriptor(
        StorageName: "__counterpart_tenant", ClrType: typeof(string), ValueProvider: value,
        AppliesTo: type => type == typeof(ScopedCatalog), RequiredCapability: DataCaps.Isolation.RowScoped));

    private sealed class ScopedReadContributor(Func<Filter?> filter) : IReadFilterContributor
    {
        public Filter? ReadFilter(Type entityType) => entityType == typeof(ScopedCatalog) ? filter() : null;
        public Capability? RequiredCapability => DataCaps.Isolation.RowScoped;
    }

    private static async Task Seed(string partition, params ScopedCatalog[] rows)
    {
        using var scope = EntityContext.With(partition: partition);
        foreach (var row in rows) await row.Save();
    }

    [Storage(Name = "KOAN_COUNTERPART_SCOPED")]
    public sealed class ScopedCatalog : Entity<ScopedCatalog> { public bool Allowed { get; set; } }
}
