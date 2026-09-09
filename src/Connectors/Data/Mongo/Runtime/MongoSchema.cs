using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core;
using Koan.Data.Connector.Mongo.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace Koan.Data.Connector.Mongo.Runtime;

internal sealed class MongoSchema<TEntity, TKey>(
    MongoRoute route,
    MongoClientManager clients,
    MongoEntityPlan<TEntity, TKey> entity,
    ILogger<MongoSchema<TEntity, TKey>> logger)
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly object _gate = new();
    // The repository fixes source/database/entity; the remaining physical identity is the collection.
    // Successful initialization retains resolved names. Normal operations perform no further index I/O.
    private readonly Dictionary<string, Lazy<Task<string[]>>> _entries = new(StringComparer.Ordinal);

    public Task Ensure(string collection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Lazy<Task<string[]>> entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(collection, out entry!))
            {
                if (_entries.Count >= Constants.Provider.MaximumCollectionsPerRepository)
                    throw new InvalidOperationException(
                        $"MongoDB reached the bounded collection-plan limit of " +
                        $"{Constants.Provider.MaximumCollectionsPerRepository} for '{typeof(TEntity).FullName}'.");
                entry = new Lazy<Task<string[]>>(() => EnsureCore(collection, ct), LazyThreadSafetyMode.ExecutionAndPublication);
                _entries.Add(collection, entry);
            }
        }
        return Observe(collection, entry);
    }

    private async Task Observe(string collection, Lazy<Task<string[]>> entry)
    {
        try { await entry.Value.ConfigureAwait(false); }
        catch
        {
            lock (_gate)
                if (_entries.TryGetValue(collection, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(collection);
            throw;
        }
    }

    private async Task<string[]> EnsureCore(string collection, CancellationToken ct)
    {
        var database = await clients.Database(route, ct).ConfigureAwait(false);
        var metadata = await Describe(database, collection, ct).ConfigureAwait(false);
        if (metadata is null && route.StorageLifecycle == StorageLifecycle.External)
            throw new InvalidOperationException(
                $"External MongoDB collection '{route.Database}/{collection}' does not exist. " +
                "Create it outside Koan or select StorageLifecycle=Managed.");
        if (metadata is null) await database.CreateCollectionAsync(collection, cancellationToken: ct).ConfigureAwait(false);
        if (route.StorageLifecycle == StorageLifecycle.External) return [];
        var collation = metadata?.GetValue(Constants.Index.Options, new BsonDocument()).AsBsonDocument
            .GetValue(Constants.Index.Collation, BsonNull.Value) ?? BsonNull.Value;
        return await EnsureIndexes(database.GetCollection<BsonDocument>(collection), collation, ct).ConfigureAwait(false);
    }

    private async Task<string[]> EnsureIndexes(IMongoCollection<BsonDocument> collection, BsonValue collation, CancellationToken ct)
    {
        var indexes = new List<CreateIndexModel<BsonDocument>>();
        if (entity.Mapping is { } mapping)
        {
            foreach (var index in mapping.Indexes.Where(static index => !index.Primary))
            {
                var keys = index.Bindings.Select(binding =>
                    Builders<BsonDocument>.IndexKeys.Ascending(MongoValues.Path(binding.PhysicalPath)));
                indexes.Add(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Combine(keys),
                    new CreateIndexOptions
                    {
                        Name = index.Name,
                        Unique = index.Unique,
                        ExpireAfter = index.Ttl ? TimeSpan.Zero : null
                    }));
            }
        }
        else
        {
            foreach (var index in IndexMetadata.GetIndexes(typeof(TEntity)).Where(static index => !index.IsPrimaryKey))
            {
                var keys = index.Properties.Select(property =>
                {
                    var path = FieldPath.Of(property.Name);
                    var resolved = FieldPathResolver.Resolve(typeof(TEntity), path);
                    return Builders<BsonDocument>.IndexKeys.Ascending(entity.Field(path, resolved, MappingConsumer.Index));
                });
                indexes.Add(new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Combine(keys),
                    new CreateIndexOptions
                    {
                        Name = index.Name,
                        Unique = index.Unique,
                        ExpireAfter = index.Ttl ? TimeSpan.Zero : null
                    }));
            }
        }
        if (indexes.Count == 0) return [];

        using var cursor = await collection.Indexes.ListAsync(ct).ConfigureAwait(false);
        var existing = await cursor.ToListAsync(ct).ConfigureAwait(false);
        var pending = new List<CreateIndexModel<BsonDocument>>();
        var names = new List<string>(indexes.Count);
        var render = new RenderArgs<BsonDocument>(collection.DocumentSerializer,
            MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry);
        foreach (var index in indexes)
        {
            var keys = index.Keys.Render(render);
            var sameKeys = existing.Where(candidate => candidate[Constants.Index.Key].Equals(keys)).ToArray();
            var equivalent = sameKeys.FirstOrDefault(candidate => Equivalent(candidate, index.Options, collation)
                && (index.Options.Name is null || candidate[Constants.Index.Name].AsString == index.Options.Name));
            if (equivalent is not null)
            {
                names.Add(equivalent[Constants.Index.Name].AsString);
                logger.LogDebug("MongoDB collection {Collection} reuses equivalent index {Index} for {Entity}.",
                    collection.CollectionNamespace.CollectionName, equivalent[Constants.Index.Name].AsString, typeof(TEntity).FullName);
                continue;
            }

            var conflict = sameKeys.FirstOrDefault()
                ?? existing.FirstOrDefault(candidate => index.Options.Name is not null
                    && candidate[Constants.Index.Name].AsString == index.Options.Name);
            if (conflict is not null)
                throw new InvalidOperationException(
                    $"MongoDB index '{conflict[Constants.Index.Name].AsString}' on '{collection.CollectionNamespace.CollectionName}' " +
                    $"is incompatible with the declared index for '{typeof(TEntity).FullName}'. " +
                    "Compare ordered keys, uniqueness, sparse/partial coverage, collation, visibility, TTL and any explicit name; " +
                    "resolve the difference with an explicit migration. Koan will not drop or rename existing indexes.");
            pending.Add(index);
        }
        if (pending.Count != 0)
            names.AddRange(await collection.Indexes.CreateManyAsync(pending, cancellationToken: ct).ConfigureAwait(false));
        return names.ToArray();
    }

    private static bool Equivalent(BsonDocument existing, CreateIndexOptions desired, BsonValue collation)
    {
        return existing.GetValue(Constants.Index.Unique, false).ToBoolean() == (desired.Unique ?? false)
            && !existing.GetValue(Constants.Index.Sparse, false).ToBoolean()
            && !existing.GetValue(Constants.Index.Hidden, false).ToBoolean()
            && !existing.Contains(Constants.Index.PartialFilter)
            && existing.GetValue(Constants.Index.Collation, BsonNull.Value).Equals(collation)
            && (desired.ExpireAfter is { } expiry
                ? existing.TryGetValue(Constants.Index.Expiry, out var seconds) && seconds.IsNumeric
                    && seconds.ToDouble() == expiry.TotalSeconds
                : !existing.Contains(Constants.Index.Expiry));
    }

    private static async Task<BsonDocument?> Describe(IMongoDatabase database, string collection, CancellationToken ct)
    {
        using var cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions
            {
                Filter = new BsonDocument(Constants.Index.Name, collection)
            },
            ct).ConfigureAwait(false);
        return await cursor.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
