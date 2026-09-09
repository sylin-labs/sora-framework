using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Naming;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Koan.Data.Connector.Mongo.Tests.Specs;

public sealed class MongoExistingIndexSpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output)
{
    [Fact]
    public async Task Legacy_generated_names_preserve_unique_compound_and_ttl_indexes()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var partition = NewPartition("legacy-index");
        using var lease = Lease(partition);
        var name = StorageNameGenerator.Generate(typeof(Document), partition,
            new MongoAdapterFactory().GetNamingCapability(host.Services));
        var collection = new MongoClient(Fixture.ConnectionString)
            .GetDatabase(Fixture.Database).GetCollection<BsonDocument>(name);
        await collection.InsertOneAsync(new BsonDocument { ["_id"] = "existing", ["slug"] = "existing" });
        await collection.Indexes.CreateManyAsync([
            new(Builders<BsonDocument>.IndexKeys.Ascending("slug"), new CreateIndexOptions { Name = "ix_Slug", Unique = true }),
            new(Builders<BsonDocument>.IndexKeys.Ascending("kind").Ascending("status"), new CreateIndexOptions { Name = "ix_Kind_Status" }),
            new(Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"), new CreateIndexOptions { Name = "ix_ExpiresAt", ExpireAfter = TimeSpan.Zero })
        ]);

        (await Document.Get("existing"))!.Slug.Should().Be("existing");
        await new Document { Slug = "new" }.Save();
        await using var secondHost = await BootAsync();
        (await Document.Get("existing"))!.Slug.Should().Be("existing");
        using var cursor = await collection.Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        indexes.Select(index => index["name"].AsString).Should().BeEquivalentTo(
            "_id_", "ix_Slug", "ix_Kind_Status", "ix_ExpiresAt");
        await FluentActions.Invoking(() => new Document { Slug = "existing" }.Save())
            .Should().ThrowAsync<MongoWriteException>();
    }

    [Theory]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("collation")]
    [InlineData("ttl")]
    [InlineData("hidden")]
    public async Task Incompatible_existing_index_is_not_accepted_or_replaced(string difference)
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var partition = NewPartition("incompatible-index");
        using var lease = Lease(partition);
        var name = StorageNameGenerator.Generate(typeof(Document), partition,
            new MongoAdapterFactory().GetNamingCapability(host.Services));
        var collection = new MongoClient(Fixture.ConnectionString)
            .GetDatabase(Fixture.Database).GetCollection<BsonDocument>(name);
        var options = new CreateIndexOptions<BsonDocument> { Name = "ix_Slug", Unique = difference != "unique" };
        if (difference == "sparse") options.Sparse = true;
        if (difference == "partial") options.PartialFilterExpression = new BsonDocument("slug", new BsonDocument("$exists", true));
        if (difference == "collation") options.Collation = new Collation("en", strength: CollationStrength.Secondary);
        if (difference == "ttl") options.ExpireAfter = TimeSpan.FromHours(1);
        if (difference == "hidden") options.Hidden = true;
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("slug"), options));

        await FluentActions.Invoking(() => Document.Get("missing"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*index*incompatible*explicit migration*");
        using var cursor = await collection.Indexes.ListAsync();
        (await cursor.ToListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Equivalent_index_inherits_collection_collation_and_cache_is_partition_specific()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        foreach (var suffix in new[] { "a", "b" })
        {
            var partition = NewPartition("collated-" + suffix);
            using var lease = Lease(partition);
            var name = StorageNameGenerator.Generate(typeof(CollatedDocument), partition,
                new MongoAdapterFactory().GetNamingCapability(host.Services));
            var database = new MongoClient(Fixture.ConnectionString).GetDatabase(Fixture.Database);
            await database.CreateCollectionAsync(name, new CreateCollectionOptions
            {
                Collation = new Collation("en", strength: CollationStrength.Secondary)
            });
            var collection = database.GetCollection<BsonDocument>(name);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("slug"), new CreateIndexOptions { Name = "legacy_" + suffix, Unique = true }));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CollatedDocument.Get("missing")));
            await new CollatedDocument { Slug = "one" }.Save();
            await FluentActions.Invoking(() => CollatedDocument.UpsertMany([
                new CollatedDocument { Slug = "ONE" }
            ])).Should().ThrowAsync<MongoBulkWriteException<BsonDocument>>();
            using var cursor = await collection.Indexes.ListAsync();
            (await cursor.ToListAsync()).Select(index => index["name"].AsString)
                .Should().BeEquivalentTo("_id_", "legacy_" + suffix);
        }
    }

    [Fact]
    public async Task Explicit_name_remains_required_and_failed_initialization_can_retry()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var partition = NewPartition("explicit-index");
        using var lease = Lease(partition);
        var name = StorageNameGenerator.Generate(typeof(NamedDocument), partition,
            new MongoAdapterFactory().GetNamingCapability(host.Services));
        var collection = new MongoClient(Fixture.ConnectionString).GetDatabase(Fixture.Database)
            .GetCollection<BsonDocument>(name);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("slug"), new CreateIndexOptions { Name = "legacy" }));
        await FluentActions.Invoking(() => NamedDocument.Get("missing"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*explicit migration*");
        // Emulate an operator-approved migration; Koan itself never drops the conflicting index.
        await collection.Indexes.DropOneAsync("legacy");
        (await NamedDocument.Get("missing")).Should().BeNull();
        using var cursor = await collection.Indexes.ListAsync();
        (await cursor.ToListAsync()).Select(index => index["name"].AsString)
            .Should().BeEquivalentTo("_id_", "declared_slug");
    }

    private sealed class CollatedDocument : Entity<CollatedDocument>
    {
        [Index(Unique = true)] public string Slug { get; set; } = "";
    }

    private sealed class NamedDocument : Entity<NamedDocument>
    {
        [Index(Name = "declared_slug")] public string Slug { get; set; } = "";
    }

    private sealed class Document : Entity<Document>
    {
        [Index(Unique = true)] public string Slug { get; set; } = "";
        [Index(Group = "kind-status", Order = 0)] public string Kind { get; set; } = "";
        [Index(Group = "kind-status", Order = 1)] public string Status { get; set; } = "";
        [Index(Ttl = true)] public DateTimeOffset? ExpiresAt { get; set; }
    }
}
