using Koan.Core;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Koan.Data.Connector.Mongo.Tests.Specs;

public sealed class MongoConditionalPublicationSpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output)
{
    [Fact]
    public async Task Native_match_conflict_missing_and_identical_replacement_have_honest_results()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var row = await new Publication { Revision = "old", Text = "original" }.Save();
        var next = new Publication { Id = row.Id, Revision = "new", Text = "translated" };

        (await next.ReplaceIf(stored => stored.Revision == "old")).Should().BeTrue();
        (await next.ReplaceIf(stored => stored.Revision == "new")).Should().BeTrue("Mongo matches even when no bytes change");
        (await new Publication { Id = row.Id, Revision = "late", Text = "obsolete" }
            .ReplaceIf(stored => stored.Revision == "old")).Should().BeFalse();
        var absentId = Guid.NewGuid().ToString("N");
        (await new Publication { Id = absentId, Revision = "first" }.ReplaceIf(stored => true)).Should().BeFalse();

        (await Publication.Get(absentId)).Should().BeNull();
        var document = await Stored(row.Id);
        document["revision"].AsString.Should().Be("new");
        document["text"].AsString.Should().Be("translated");
    }

    [Fact]
    public async Task Concurrent_publications_consume_one_native_revision()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var row = await new Publication { Revision = "old" }.Save();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(async index =>
        {
            await start.Task;
            var value = "candidate-" + index;
            var applied = await new Publication { Id = row.Id, Revision = value, Text = value }
                .ReplaceIf(stored => stored.Revision == "old");
            return (applied, value);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(result => result.applied).Should().Be(1);
        results.Count(result => !result.applied).Should().Be(7);
        var winner = results.Single(result => result.applied).value;
        var document = await Stored(row.Id);
        document["revision"].AsString.Should().Be(winner);
        document["text"].AsString.Should().Be(winner);
    }

    [Fact]
    public async Task Explicit_named_and_default_destinations_restore_the_callers_partition()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var id = Guid.NewGuid().ToString("N");
        var locale = NewPartition("locale");
        var ambient = NewPartition("caller");
        await new Publication { Id = id, Revision = "canonical", Text = "source" }.Save();
        using (Lease(locale)) await new Publication { Id = id, Revision = "locale-old", Text = "locale" }.Save();
        using (Lease(ambient))
        {
            await new Publication { Id = id, Revision = "caller", Text = "untouched" }.Save();
            (await new Publication { Id = id, Revision = "locale-new", Text = "translated" }
                .ReplaceIf(stored => stored.Revision == "locale-old", partition: locale)).Should().BeTrue();
            EntityContext.Current!.Partition.Should().Be(ambient);
            (await new Publication { Id = id, Revision = "canonical-new", Text = "source-updated" }
                .ReplaceIf(stored => stored.Revision == "canonical", partition: "")).Should().BeTrue();
            EntityContext.Current!.Partition.Should().Be(ambient);
            (await Publication.Get(id))!.Text.Should().Be("untouched");
        }
        (await Publication.Get(id, partition: locale))!.Text.Should().Be("translated");
        (await Publication.Get(id, partition: ""))!.Text.Should().Be("source-updated");
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("cancel")]
    [InlineData("capture")]
    public async Task Delayed_preparation_preserves_native_competitors_cancellation_and_captured_guard(string change)
    {
        RequireBackingStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await BootAsync(() => Publication.Lifecycle.BeforeUpsert(async context =>
        {
            if (context.Current.Text == "generated")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
            }
            return context.Proceed();
        }));
        var row = await new Publication { Revision = "old", Text = "original" }.Save();
        using var cancellation = new CancellationTokenSource();
        var expected = "old";
        var pending = new Publication { Id = row.Id, Revision = "generated", Text = "generated" }
            .ReplaceIf(stored => stored.Revision == expected, ct: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (change == "save") await new Publication { Id = row.Id, Revision = "manual", Text = "manual" }.Save();
            else if (change == "delete") await Publication.Remove(row.Id);
            else if (change == "cancel") cancellation.Cancel();
            else expected = "changed-capture";
        }
        finally { release.TrySetResult(); }

        if (change == "cancel")
            await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
        else (await pending).Should().Be(change == "capture");
        var stored = await Publication.Get(row.Id);
        if (change == "delete") stored.Should().BeNull();
        else stored!.Revision.Should().Be(change switch { "save" => "manual", "capture" => "generated", _ => "old" });
    }

    [Fact]
    public async Task Nonunique_mapped_identity_refuses_without_changing_either_native_document()
    {
        RequireBackingStore();
        var container = "conditional_mapping_" + Guid.NewGuid().ToString("N");
        var collection = Database().GetCollection<BsonDocument>(container);
        await collection.InsertManyAsync(new[]
        {
            new BsonDocument { ["CUSTOMER_NO"] = 7L, ["REVISION"] = "old", ["EXTERNAL"] = "first" },
            new BsonDocument { ["CUSTOMER_NO"] = 7L, ["REVISION"] = "old", ["EXTERNAL"] = "second" }
        });
        var before = await collection.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await using var host = await KoanIntegrationHost.Configure().WithSettings(Fixture.SettingsForBoot())
            .ConfigureServices(services => services.AddKoan(koan => koan.Data.Source("Default").Map<MappedPublication>(map => map
                .Container(container).Key(row => row.Id).Name("CUSTOMER_NO").Property(row => row.Revision).Name("REVISION"))))
            .StartAsync(TestContext.Current.CancellationToken);
        var row = new MappedPublication { Id = 7, Revision = "new" };
        await FluentActions.Awaiting(() => row.ReplaceIf(stored => stored.Revision == "old"))
            .Should().ThrowAsync<NotSupportedException>();
        var native = new MongoAdapterFactory().Create<MappedPublication, long>(host.Services);
        await FluentActions.Awaiting(() => ((IConditionalWriteRepository<MappedPublication, long>)native)
            .ConditionalReplaceAsync(row, Filter.Eq("Revision", "old")))
            .Should().ThrowAsync<NotSupportedException>();
        Data<MappedPublication, long>.Capabilities.Has(DataCaps.Write.ConditionalReplace).Should().BeFalse();
        var after = await collection.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
        after.Should().HaveCount(2);
        after.Select(document => document.ToJson()).Should().Equal(before.Select(document => document.ToJson()));
    }

    private IMongoDatabase Database() => new MongoClient(Fixture.ConnectionString).GetDatabase(Fixture.Database);
    private Task<BsonDocument> Stored(string id) => Database().GetCollection<BsonDocument>("KOAN_CONDITIONAL_PUBLICATION")
        .Find(new BsonDocument("_id", id)).SingleAsync();

    [Storage(Name = "KOAN_CONDITIONAL_PUBLICATION")]
    private sealed class Publication : Entity<Publication>
    {
        public string Revision { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private sealed class MappedPublication : Entity<MappedPublication, long>
    {
        public string Revision { get; set; } = "";
    }
}
