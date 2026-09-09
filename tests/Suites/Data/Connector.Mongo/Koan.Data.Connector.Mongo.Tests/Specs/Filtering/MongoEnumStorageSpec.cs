using Koan.Data.Abstractions.Naming;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Koan.Data.Connector.Mongo.Tests.Specs.Filtering;

public sealed class MongoEnumStorageSpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output)
{
    [Fact]
    public async Task Enum_names_survive_storage_queries_and_replacement_writes()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var partition = NewPartition("enum-storage");
        using var lease = Lease(partition);
        var name = StorageNameGenerator.Generate(typeof(Document), partition,
            new MongoAdapterFactory().GetNamingCapability(host.Services));
        var collection = new MongoClient(Fixture.ConnectionString)
            .GetDatabase(Fixture.Database).GetCollection<BsonDocument>(name);

        var published = await new Document
        {
            Status = State.Published, Optional = State.Suppressed,
            States = [State.Published, State.Suppressed],
            Details = new Detail { Status = State.Published },
            ByName = new() { ["review"] = State.Suppressed }
        }.Save();
        var suppressed = await new Document { Status = State.Suppressed }.Save();
        var raw = await collection.Find(new BsonDocument("_id", published.Id)).SingleAsync();
        raw["status"].Should().Be(new BsonString("Published"));
        raw["optional"].Should().Be(new BsonString("Suppressed"));
        raw["states"].AsBsonArray.Should().Equal(new BsonString("Published"), new BsonString("Suppressed"));
        raw["details"]["status"].Should().Be(new BsonString("Published"));
        raw["byName"]["review"].Should().Be(new BsonString("Suppressed"));

        (await Document.Query(item => item.Status == State.Published)).Select(item => item.Id)
            .Should().Equal(published.Id);
        (await Document.Query(item => item.Status != State.Published)).Select(item => item.Id)
            .Should().Equal(suppressed.Id);
        (await Document.Query(item => item.Optional == State.Suppressed)).Select(item => item.Id)
            .Should().Equal(published.Id);
        (await Document.Query(item => item.States.Contains(State.Published))).Select(item => item.Id)
            .Should().Equal(published.Id);
        (await Document.Query(item => item.Details.Status == State.Published)).Select(item => item.Id)
            .Should().Equal(published.Id);
        (await Document.Query(item => item.Status > State.Published)).Select(item => item.Id)
            .Should().Equal(suppressed.Id);
        (await Document.Query(item => item.Status < State.Suppressed)).Select(item => item.Id)
            .Should().Equal(published.Id);

        var restored = (await Document.Get(published.Id))!;
        restored.Status.Should().Be(State.Published);
        restored.States.Should().Equal(State.Published, State.Suppressed);
        restored.Audit.Should().Equal("created");
        restored.Status = State.Suppressed;
        await restored.Save();
        (await collection.Find(new BsonDocument("_id", published.Id)).SingleAsync())["status"]
            .Should().Be(new BsonString("Suppressed"));
        (await Document.Get(published.Id))!.Audit.Should().Equal("created");
    }

    private enum State { Draft, Published, Suppressed }
    private sealed class Detail { public State Status { get; set; } }
    private sealed class Document : Entity<Document>
    {
        public State Status { get; set; }
        public State? Optional { get; set; }
        public State[] States { get; set; } = [];
        public Detail Details { get; set; } = new();
        public Dictionary<string, State> ByName { get; set; } = new();
        public List<string> Audit { get; set; } = ["created"];
    }
}
