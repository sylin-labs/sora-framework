using Koan.Data.Abstractions.Naming;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace Koan.Data.Connector.Mongo.Tests.Specs.Filtering;

public sealed class MongoEnumStorageSpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output)
{
    [Fact]
    public async Task Native_numeric_width_decimal_and_binary_survive_document_writes()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var partition = NewPartition("native-scalars");
        using var lease = Lease(partition);
        var name = StorageNameGenerator.Generate(typeof(NativeDocument), partition,
            new MongoAdapterFactory().GetNamingCapability(host.Services));
        var collection = new MongoClient(Fixture.ConnectionString)
            .GetDatabase(Fixture.Database).GetCollection<BsonDocument>(name);
        var saved = await new NativeDocument { Quantity = 42, Total = 42L, Amount = 12.34m, Bytes = [1, 2, 3] }.Save();
        var raw = await collection.Find(new BsonDocument("_id", saved.Id)).SingleAsync();
        raw["quantity"].BsonType.Should().Be(BsonType.Int32);
        raw["total"].BsonType.Should().Be(BsonType.Int64);
        raw["amount"].BsonType.Should().Be(BsonType.Decimal128);
        raw["bytes"].BsonType.Should().Be(BsonType.Binary);
        var restored = (await NativeDocument.Get(saved.Id))!;
        restored.Bytes.Should().Equal(1, 2, 3);
        restored.Amount.Should().Be(12.34m);
        await restored.Save();
        (await collection.Find(new BsonDocument("_id", saved.Id)).SingleAsync()).Equals(raw).Should().BeTrue();
    }

    private sealed class NativeDocument : Entity<NativeDocument>
    {
        public int Quantity { get; set; }
        public long Total { get; set; }
        public decimal Amount { get; set; }
        public byte[] Bytes { get; set; } = [];
    }

    [Fact]
    public async Task Native_uri_and_special_tokens_survive_nested_document_writes()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var partition = NewPartition("native-specials");
        using var lease = Lease(partition);
        var name = StorageNameGenerator.Generate(typeof(SpecialDocument), partition,
            new MongoAdapterFactory().GetNamingCapability(host.Services));
        var collection = new MongoClient(Fixture.ConnectionString)
            .GetDatabase(Fixture.Database).GetCollection<BsonDocument>(name);
        var stamp = new DateTimeOffset(2026, 9, 9, 1, 2, 3, TimeSpan.FromHours(-4));
        var saved = await new SpecialDocument
        {
            Cover = new Uri("https://example.test/cover%20image.png?q=one%20two"),
            Gallery = [new Uri("relative/image.png", UriKind.Relative)],
            Tokens = new JObject
            {
                ["uri"] = new JValue(new Uri("https://example.test/image.png")),
                ["date"] = new JValue(stamp),
                ["duration"] = new JValue(TimeSpan.FromSeconds(12)),
                ["amount"] = new JValue(12.34m),
                ["bytes"] = new JValue(new byte[] { 1, 2, 3 }),
                ["guid"] = new JValue(Guid.Parse("a11d611e-2725-424f-b5b1-4551ab2f5eda"))
            }
        }.Save();
        var raw = await collection.Find(new BsonDocument("_id", saved.Id)).SingleAsync();
        raw["cover"].Should().Be(new BsonString(saved.Cover.OriginalString));
        raw["gallery"][0].Should().Be(new BsonString("relative/image.png"));
        raw["tokens"]["date"].Should().Be(new BsonDateTime(stamp.UtcDateTime));
        raw["tokens"]["duration"].Should().Be(new BsonInt64(TimeSpan.FromSeconds(12).Ticks));
        raw["tokens"]["guid"].Should().Be(new BsonString("a11d611e-2725-424f-b5b1-4551ab2f5eda"));
        var restored = (await SpecialDocument.Get(saved.Id))!;
        restored.Cover.Should().Be(saved.Cover);
        restored.Gallery.Should().Equal(saved.Gallery);
        await restored.Save();
        (await collection.Find(new BsonDocument("_id", saved.Id)).SingleAsync()).Equals(raw).Should().BeTrue();
    }

    private sealed class SpecialDocument : Entity<SpecialDocument>
    {
        public Uri Cover { get; set; } = new("https://example.test");
        public List<Uri> Gallery { get; set; } = [];
        public JObject Tokens { get; set; } = new();
    }

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
