using Koan.Core;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Abstractions.Filtering;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Koan.Data.Connector.Mongo.Tests.Specs;

public sealed class MongoInsertOnlySpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output)
{
    [Fact]
    public async Task Duplicate_preserves_native_document_and_ordinary_save_still_updates()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<InsertProbe, string>();
        DataCaps.Describe(repository, "mongo").Has(DataCaps.Write.InsertOnly).Should().BeTrue();
        var insert = (IInsertOnlyRepository<InsertProbe, string>)repository;
        var original = new InsertProbe { Id = Guid.NewGuid().ToString("N"), Value = "original" };
        var receipt = await insert.Insert(original);
        receipt.Key.Should().Be(original.Id);
        receipt.Outcome.Should().Be(MutationOutcome.Inserted);
        receipt.CommitOutcome.Should().Be(DataCommitOutcome.Committed);
        var before = await Stored(original.Id);

        var conflict = await insert.Insert(new InsertProbe { Id = original.Id, Value = "replacement" });
        conflict.Key.Should().Be(original.Id);
        conflict.Outcome.Should().Be(MutationOutcome.Conflict);
        conflict.CommitOutcome.Should().Be(DataCommitOutcome.NotCommitted);
        conflict.Entity.Should().BeNull();
        (await Stored(original.Id)).ToBson().Should().Equal(before.ToBson());

        original.Value = "ordinary save";
        await original.Save();
        (await Stored(original.Id))["value"].AsString.Should().Be("ordinary save");
    }

    [Fact]
    public async Task Concurrent_native_insertions_commit_exactly_one_identity()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var insert = (IInsertOnlyRepository<InsertProbe, string>)host.Services
            .GetRequiredService<IDataService>().GetRepository<InsertProbe, string>();
        var id = Guid.NewGuid().ToString("N");
        _ = await InsertProbe.Get(id);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(async index =>
        {
            await gate.Task;
            return await insert.Insert(new InsertProbe { Id = id, Value = $"winner-{index}" });
        }).ToArray();
        gate.SetResult();
        var receipts = await Task.WhenAll(attempts);
        receipts.Count(result => result.Outcome == MutationOutcome.Inserted).Should().Be(1);
        receipts.Count(result => result.Outcome == MutationOutcome.Conflict).Should().Be(7);
        receipts.Where(result => result.Outcome == MutationOutcome.Conflict)
            .Should().OnlyContain(result => result.Entity == null && result.CommitOutcome == DataCommitOutcome.NotCommitted);
        var winner = receipts.Single(result => result.Outcome == MutationOutcome.Inserted);
        (await Stored(id))["value"].AsString.Should().Be(winner.Entity!.Value);
    }

    [Fact]
    public async Task Nonidentity_unique_violation_remains_a_failure()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var insert = (IInsertOnlyRepository<UniqueProbe, string>)host.Services
            .GetRequiredService<IDataService>().GetRepository<UniqueProbe, string>();
        var original = new UniqueProbe { Id = Guid.NewGuid().ToString("N"), Value = Guid.NewGuid().ToString("N") };
        await insert.Insert(original);
        var collection = Database().GetCollection<BsonDocument>("KOAN_INSERT_UNIQUE_PROBE");
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("value"), new CreateIndexOptions { Unique = true }));
        var attemptedId = Guid.NewGuid().ToString("N");
        await FluentActions.Invoking(() => insert.Insert(new UniqueProbe { Id = attemptedId, Value = original.Value }))
            .Should().ThrowAsync<MongoWriteException>();
        (await collection.Find(new BsonDocument("_id", attemptedId)).AnyAsync()).Should().BeFalse();
        (await collection.Find(new BsonDocument("value", original.Value)).ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Explicit_nonunique_mapping_rejects_before_persistence()
    {
        RequireBackingStore();
        var container = $"koan_insert_mapped_{Guid.NewGuid():N}";
        var collection = Database().GetCollection<BsonDocument>(container);
        await collection.InsertOneAsync(new BsonDocument { ["CUSTOMER_NO"] = 7L, ["VALUE"] = "original" });
        var before = await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Fixture.SettingsForBoot())
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("Default").Map<MappedProbe>(map => map
                    .Container(container)
                    .Key(item => item.Id).Name("CUSTOMER_NO")
                    .Property(item => item.Value).Name("VALUE"))))
            .StartAsync(TestContext.Current.CancellationToken);
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<MappedProbe, long>();
        DataCaps.Describe(repository, "mongo").Has(DataCaps.Write.InsertOnly).Should().BeFalse();
        DataCaps.Describe(repository, "mongo").Detail<FilterSupport>(DataCaps.Query.Filter)!
            .SupportsSameIdIn.Should().BeFalse();
        var native = new MongoAdapterFactory().Create<MappedProbe, long>(host.Services);
        FluentActions.Invoking(() => ((ICounterpartQueryRepository)native).BindCounterpartTarget())
            .Should().Throw<NotSupportedException>();
        var insert = (IInsertOnlyRepository<MappedProbe, long>)repository;
        await FluentActions.Invoking(() => insert.Insert(new MappedProbe { Id = 7, Value = "replacement" }))
            .Should().ThrowAsync<NotSupportedException>();
        var after = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        after.Should().ContainSingle();
        after[0].ToBson().Should().Equal(before.ToBson());
    }

    [Fact]
    public async Task Default_numeric_identity_rejects_before_native_insert()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<NumericProbe, long>();
        var insert = (IInsertOnlyRepository<NumericProbe, long>)repository;
        await FluentActions.Invoking(() => insert.Insert(new NumericProbe()))
            .Should().ThrowAsync<NotSupportedException>();
        (await Database().GetCollection<BsonDocument>("KOAN_INSERT_NUMERIC_PROBE")
            .CountDocumentsAsync(new BsonDocument("_id", 0L))).Should().Be(0);
    }

    [Storage(Name = "KOAN_INSERT_NUMERIC_PROBE")]
    private sealed class NumericProbe : Entity<NumericProbe, long>;

    private IMongoDatabase Database() => new MongoClient(Fixture.ConnectionString).GetDatabase(Fixture.Database);

    private Task<BsonDocument> Stored(string id) => Database().GetCollection<BsonDocument>("KOAN_INSERT_PROBE")
        .Find(new BsonDocument("_id", id)).SingleAsync();

    [Storage(Name = "KOAN_INSERT_PROBE")]
    private sealed class InsertProbe : Entity<InsertProbe>
    {
        public string Value { get; set; } = "";
    }

    [Storage(Name = "KOAN_INSERT_UNIQUE_PROBE")]
    private sealed class UniqueProbe : Entity<UniqueProbe>
    {
        public string Value { get; set; } = "";
    }

    private sealed class MappedProbe : Entity<MappedProbe, long>
    {
        public string Value { get; set; } = "";
    }
}
