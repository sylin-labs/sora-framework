using System.Linq.Expressions;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Core;
using Koan.Data.Core.Configuration;
using Koan.Data.Core.Optimization;
using Koan.Data.Connector.Mongo.Runtime;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Koan.Data.Connector.Mongo;

internal sealed class MongoRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IBoundedQueryRepository<TEntity, TKey>,
    IOptimizedDataRepository<TEntity, TKey>,
    IConditionalWriteRepository<TEntity, TKey>,
    IInsertOnlyRepository<TEntity, TKey>,
    IInstructionExecutor<TEntity>,
    IDescribesCapabilities,
    IBulkUpsert<TKey>,
    IBulkDelete<TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private const int IdentityBatch = 1000;
    private readonly IServiceProvider _services;
    private readonly MongoAdapterFactory _factory;
    private readonly MongoRoute _route;
    private readonly MongoClientManager _clients;
    private readonly MongoEntityPlan<TEntity, TKey> _entity;
    private readonly MongoQueryCompiler<TEntity, TKey> _queries;
    private readonly MongoSchema<TEntity, TKey> _schema;

    public MongoRepository(
        IServiceProvider services,
        MongoAdapterFactory factory,
        MongoRoute route,
        MongoClientManager clients,
        MappingPlan? mapping)
    {
        _services = services;
        _factory = factory;
        _route = route;
        _clients = clients;
        _entity = new MongoEntityPlan<TEntity, TKey>(services, route.Source, mapping);
        _queries = new MongoQueryCompiler<TEntity, TKey>(_entity);
        _schema = new MongoSchema<TEntity, TKey>(route, clients, _entity,
            services.GetRequiredService<ILogger<MongoSchema<TEntity, TKey>>>());

        if (_entity.MappedContainer is { } container &&
            (container.Namespace.Count > 1 ||
             container.Namespace.Count == 1 &&
             !string.Equals(container.Namespace[0], route.Database, StringComparison.Ordinal)))
            throw new MappingCompilationException(route.Source, typeof(TEntity),
                $"MongoDB mapping container '{container}' must use no namespace or the routed database '{route.Database}'.");
    }

    public StorageOptimizationInfo OptimizationInfo => _entity.Optimization;

    public void Describe(ICapabilities capabilities)
    {
        MongoFeatures.Describe(capabilities);
        if (!_entity.IsMapped) capabilities.Add(DataCaps.Write.InsertOnly);
    }

    public Task EnsureReady(CancellationToken ct = default) => _schema.Ensure(CollectionName(), ct);

    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        var collection = await Collection(ct).ConfigureAwait(false);
        var document = await collection.Find(_entity.Identity(id)).Limit(1).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return document is null ? null : _entity.Read(document);
    }

    public async Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var requested = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        if (requested.Count == 0) return [];
        var found = new Dictionary<TKey, TEntity>(EqualityComparer<TKey>.Default);
        var collection = await Collection(ct).ConfigureAwait(false);
        for (var offset = 0; offset < requested.Count; offset += IdentityBatch)
        {
            var filters = requested.Skip(offset).Take(IdentityBatch).Select(_entity.Identity);
            using var cursor = await collection.FindAsync(
                Builders<BsonDocument>.Filter.Or(filters),
                cancellationToken: ct).ConfigureAwait(false);
            while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                foreach (var document in cursor.Current)
                {
                    var item = _entity.Read(document);
                    found[item.Id] = item;
                }
        }
        var result = new TEntity?[requested.Count];
        for (var index = 0; index < requested.Count; index++)
            if (found.TryGetValue(requested[index], out var item)) result[index] = item;
        return result;
    }

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var collection = await Collection(ct).ConfigureAwait(false);
        await Replace(collection, model, session: null, ct).ConfigureAwait(false);
        return model;
    }

    public async Task<MutationResult<TEntity, TKey>> Insert(TEntity model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (EqualityComparer<TKey>.Default.Equals(model.Id, default!))
            throw new NotSupportedException("MongoDB insert-only requires a non-default identity. Assign the entity identity before insertion.");
        if (_entity.IsMapped)
            throw new NotSupportedException(
                "MongoDB insert-only requires ordinary managed _id storage. Explicit mappings have not proved native identity uniqueness.");
        var document = _entity.Write(model);
        var identity = document[Infrastructure.Constants.Storage.Identity];
        if (identity.IsBsonNull)
            throw new NotSupportedException("MongoDB insert-only requires an assigned entity identity before persistence.");
        var collection = await Collection(ct).ConfigureAwait(false);
        if (!collection.Settings.WriteConcern.IsAcknowledged)
            throw new NotSupportedException("MongoDB insert-only requires acknowledged writes. Configure a nonzero write concern.");

        // Matching an identity performs no update. Only a newly inserted document receives these fields.
        // The native match receipt proves collision without fetching an existing row or parsing driver errors.
        var result = await collection.UpdateOneAsync(
            _entity.Identity(model.Id),
            new BsonDocumentUpdateDefinition<BsonDocument>(new BsonDocument(Infrastructure.Constants.Storage.SetOnInsert, document)),
            new UpdateOptions { IsUpsert = true },
            ct).ConfigureAwait(false);
        DemandAcknowledged(result.IsAcknowledged);
        if (result.UpsertedId is { } inserted && inserted.Equals(identity) && result.MatchedCount == 0)
            return new(model.Id, MutationOutcome.Inserted, model, DataCommitOutcome.Committed);
        if (result.UpsertedId is null && result.MatchedCount == 1 &&
            result.IsModifiedCountAvailable && result.ModifiedCount == 0)
            return new(model.Id, MutationOutcome.Conflict, null, DataCommitOutcome.NotCommitted);
        throw new InvalidOperationException("MongoDB insert-only did not return a verified insertion or unchanged identity collision.");
    }

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        var values = models as IReadOnlyList<TEntity> ?? models.ToArray();
        if (values.Count == 0) return 0;
        var collection = await Collection(ct).ConfigureAwait(false);
        var writes = values.Select(WriteModel).ToArray();
        await Bulk(collection, writes, session: null, ct).ConfigureAwait(false);
        return values.Count;
    }

    public async Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        var collection = await Collection(ct).ConfigureAwait(false);
        var result = await collection.DeleteOneAsync(_entity.Identity(id), ct).ConfigureAwait(false);
        DemandAcknowledged(result.IsAcknowledged);
        return result.DeletedCount == 1;
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var values = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        if (values.Count == 0) return 0;
        var collection = await Collection(ct).ConfigureAwait(false);
        long deleted = 0;
        for (var offset = 0; offset < values.Count; offset += IdentityBatch)
        {
            var filters = values.Skip(offset).Take(IdentityBatch).Select(_entity.Identity);
            var result = await collection.DeleteManyAsync(
                Builders<BsonDocument>.Filter.Or(filters),
                ct).ConfigureAwait(false);
            DemandAcknowledged(result.IsAcknowledged);
            deleted += result.DeletedCount;
        }
        return checked((int)deleted);
    }

    public async Task<int> DeleteAll(CancellationToken ct = default)
    {
        var collection = await Collection(ct).ConfigureAwait(false);
        var result = await collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, ct).ConfigureAwait(false);
        DemandAcknowledged(result.IsAcknowledged);
        return checked((int)result.DeletedCount);
    }

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default) =>
        await DeleteAll(ct).ConfigureAwait(false);

    public async Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        var plan = _queries.Compile(query);
        var collection = await Collection(ct).ConfigureAwait(false);
        long? total = null;
        if (plan.CountExecution != CountExecutionKind.None)
            total = await collection.CountDocumentsAsync(plan.Filter, cancellationToken: ct).ConfigureAwait(false);
        var items = await Read(collection, plan, ct).ConfigureAwait(false);
        return new RepositoryQueryResult<TEntity>
        {
            Items = items,
            FilterHandled = plan.FilterHandled,
            TotalCount = total,
            CountExecution = plan.CountExecution,
            SortHandled = plan.SortHandled,
            PaginationHandled = plan.PaginationHandled
        };
    }

    public async Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        var counted = query.WithoutPagination().WithCountStrategy(CountStrategy.Exact);
        var plan = _queries.Compile(counted);
        var collection = await Collection(ct).ConfigureAwait(false);
        return CountResult.Exact(await collection.CountDocumentsAsync(plan.Filter, cancellationToken: ct)
            .ConfigureAwait(false));
    }

    public async Task<BoundedQueryResult<TEntity>> QueryBoundedCandidates(
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct = default)
    {
        if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        var plan = _queries.Compile(
            query.WithoutPagination().WithCountStrategy(null),
            checked(maxCandidates + 1));
        if (plan.SortHandled.Count != query.Sort.Count)
            throw new NotSupportedException("MongoDB cannot provide a stable bounded candidate page for this sort.");
        var collection = await Collection(ct).ConfigureAwait(false);
        var rows = await Read(collection, plan, ct).ConfigureAwait(false);
        var exceeded = rows.Count > maxCandidates;
        return new BoundedQueryResult<TEntity>(
            exceeded ? rows.Take(maxCandidates).ToArray() : rows,
            rows.Count,
            exceeded);
    }

    public async Task<bool> ConditionalReplaceAsync(
        TEntity model,
        Expression<Func<TEntity, bool>> guard,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(guard);
        var filter = Builders<BsonDocument>.Filter.And(
            _entity.Identity(model),
            _queries.Predicate(LinqFilterCompiler.Compile(guard)),
            _entity.WriteGuard());
        var collection = await Collection(ct).ConfigureAwait(false);
        if (_entity.IsMapped)
        {
            var result = await collection.UpdateOneAsync(filter, _entity.Update(model), cancellationToken: ct)
                .ConfigureAwait(false);
            DemandAcknowledged(result.IsAcknowledged);
            return result.MatchedCount == 1;
        }
        else
        {
            var result = await collection.ReplaceOneAsync(filter, _entity.Write(model), cancellationToken: ct)
                .ConfigureAwait(false);
            DemandAcknowledged(result.IsAcknowledged);
            return result.MatchedCount == 1;
        }
    }

    public IBatchSet<TEntity, TKey> CreateBatch() => new MongoBatch<TEntity, TKey>(CommitBatch);

    internal async Task<BatchResult> CommitBatch(
        IReadOnlyList<TEntity> adds,
        IReadOnlyList<TEntity> updates,
        IReadOnlyList<(TKey Id, Action<TEntity> Mutate)> mutations,
        IReadOnlyList<TKey> deletes,
        BatchOptions? options,
        CancellationToken ct)
    {
        var total = checked(adds.Count + updates.Count + mutations.Count + deletes.Count);
        if (options?.MaxItems is { } bound && total > bound)
            throw new InvalidOperationException($"MongoDB batch contains {total} operations, exceeding MaxItems={bound}.");
        if (options?.RequireAtomic == true)
            throw new NotSupportedException(
                "This MongoDB batch has not proved transaction support for its selected topology; atomic execution is unavailable.");
        if (!string.IsNullOrWhiteSpace(options?.IdempotencyKey))
            throw new NotSupportedException("MongoDB idempotency keys are not claimed by this adapter.");
        if (total == 0) return new BatchResult(0, 0, 0);

        var mutationModels = new List<TEntity>(mutations.Count);
        if (mutations.Count != 0)
        {
            var loaded = await GetMany(mutations.Select(static mutation => mutation.Id), ct).ConfigureAwait(false);
            for (var index = 0; index < mutations.Count; index++)
            {
                if (loaded[index] is not { } current) continue;
                mutations[index].Mutate(current);
                mutationModels.Add(current);
            }
        }

        var writes = new List<WriteModel<BsonDocument>>(total);
        writes.AddRange(adds.Select(WriteModel));
        writes.AddRange(updates.Select(WriteModel));
        writes.AddRange(mutationModels.Select(WriteModel));
        writes.AddRange(deletes.Select(id => new DeleteOneModel<BsonDocument>(_entity.Identity(id))));
        var collection = await Collection(ct).ConfigureAwait(false);
        var result = await Bulk(collection, writes, session: null, ct).ConfigureAwait(false);
        return new BatchResult(adds.Count, updates.Count + mutationModels.Count, checked((int)result.DeletedCount));
    }

    public async Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return instruction.Name switch
        {
            DataInstructions.EnsureCreated => await Ensure<TResult>(ct).ConfigureAwait(false),
            DataInstructions.Clear => Cast<TResult>(await DeleteAll(ct).ConfigureAwait(false)),
            _ => throw new NotSupportedException(
                $"Instruction '{instruction.Name}' is not supported by MongoDB for '{typeof(TEntity).Name}'.")
        };
    }

    private async Task<TResult> Ensure<TResult>(CancellationToken ct)
    {
        await EnsureReady(ct).ConfigureAwait(false);
        return Cast<TResult>(true);
    }

    private async Task<IMongoCollection<BsonDocument>> Collection(CancellationToken ct)
    {
        var name = CollectionName();
        await _schema.Ensure(name, ct).ConfigureAwait(false);
        return (await _clients.Database(_route, ct).ConfigureAwait(false))
            .GetCollection<BsonDocument>(name);
    }

    private string CollectionName()
    {
        if (_entity.MappedContainer is { } mapped)
        {
            if (!string.IsNullOrWhiteSpace(EntityContext.Current?.Partition))
                throw new MappingCompilationException(_route.Source, typeof(TEntity),
                    "An explicit MongoDB map pins one collection and cannot accept an ambient container partition.");
            return mapped.Name;
        }
        return ((INamingProvider)_factory).ResolveStorage(
            typeof(TEntity),
            EntityContext.Current?.Partition,
            _services);
    }

    private async Task<IReadOnlyList<TEntity>> Read(
        IMongoCollection<BsonDocument> collection,
        MongoQueryPlan plan,
        CancellationToken ct)
    {
        if (plan.Computed is not null)
            return await ReadPipeline(collection, plan, ct).ConfigureAwait(false);

        var find = collection.Find(plan.Filter);
        if (plan.Sort is not null) find = find.Sort(plan.Sort);
        if (plan.Skip != 0) find = find.Skip(plan.Skip);
        if (plan.Limit is { } limit) find = find.Limit(limit);
        var documents = await find.ToListAsync(ct).ConfigureAwait(false);
        return documents.Select(_entity.Read).ToArray();
    }

    /// <summary>
    /// Runs the query as a pipeline, which is the only way MongoDB will sort by an expression.
    ///
    /// <para>An order key that reaches through a collection — "by each widget's latest sighting" — is an
    /// aggregate over a nested array, so it is computed into a field, sorted and paged on the server, and the
    /// field removed again before the documents are materialized. Nothing the caller stored is disturbed:
    /// $addFields shapes the stream, never the collection.</para>
    /// </summary>
    private async Task<IReadOnlyList<TEntity>> ReadPipeline(
        IMongoCollection<BsonDocument> collection,
        MongoQueryPlan plan,
        CancellationToken ct)
    {
        var stages = new List<BsonDocument>(6)
        {
            new("$match", plan.FilterDocument),
            new("$addFields", plan.Computed)
        };
        if (plan.SortDocument is not null) stages.Add(new BsonDocument("$sort", plan.SortDocument));
        if (plan.Skip != 0) stages.Add(new BsonDocument("$skip", plan.Skip));
        if (plan.Limit is { } limit) stages.Add(new BsonDocument("$limit", limit));

        var hide = new BsonDocument();
        foreach (var element in plan.Computed!) hide[element.Name] = 0;
        stages.Add(new BsonDocument("$project", hide));

        var documents = await collection
            .Aggregate<BsonDocument>(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages),
                cancellationToken: ct)
            .ToListAsync(ct).ConfigureAwait(false);
        return documents.Select(_entity.Read).ToArray();
    }

    private async Task Replace(
        IMongoCollection<BsonDocument> collection,
        TEntity model,
        IClientSessionHandle? session,
        CancellationToken ct)
    {
        if (_entity.Mapping?.Identity.IsGenerated == true)
            throw new NotSupportedException("MongoDB provider-generated mapped Entity identities are not claimed.");
        var filter = Builders<BsonDocument>.Filter.And(_entity.Identity(model), _entity.WriteGuard());
        try
        {
            if (_entity.IsMapped)
            {
                UpdateResult result = session is null
                    ? await collection.UpdateOneAsync(filter, _entity.Update(model), new UpdateOptions { IsUpsert = true }, ct)
                        .ConfigureAwait(false)
                    : await collection.UpdateOneAsync(session, filter, _entity.Update(model), new UpdateOptions { IsUpsert = true }, ct)
                        .ConfigureAwait(false);
                DemandAcknowledged(result.IsAcknowledged);
            }
            else
            {
                ReplaceOneResult result = session is null
                    ? await collection.ReplaceOneAsync(filter, _entity.Write(model), new ReplaceOptions { IsUpsert = true }, ct)
                        .ConfigureAwait(false)
                    : await collection.ReplaceOneAsync(session, filter, _entity.Write(model), new ReplaceOptions { IsUpsert = true }, ct)
                        .ConfigureAwait(false);
                DemandAcknowledged(result.IsAcknowledged);
            }
        }
        catch (MongoWriteException error) when (error.WriteError?.Code == Infrastructure.Constants.Provider.DuplicateKeyError)
        {
            if (await IsCrossScope(collection, _entity.Identity(model), ct).ConfigureAwait(false))
                throw CrossScope(model.Id, error);
            throw;
        }
    }

    private WriteModel<BsonDocument> WriteModel(TEntity model)
    {
        if (_entity.Mapping?.Identity.IsGenerated == true)
            throw new NotSupportedException("MongoDB provider-generated mapped Entity identities are not claimed.");
        var filter = Builders<BsonDocument>.Filter.And(_entity.Identity(model), _entity.WriteGuard());
        if (_entity.IsMapped)
            return new UpdateOneModel<BsonDocument>(filter, _entity.Update(model)) { IsUpsert = true };
        return new ReplaceOneModel<BsonDocument>(filter, _entity.Write(model)) { IsUpsert = true };
    }

    private async Task<BulkWriteResult<BsonDocument>> Bulk(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<WriteModel<BsonDocument>> writes,
        IClientSessionHandle? session,
        CancellationToken ct)
    {
        try
        {
            var options = new BulkWriteOptions { IsOrdered = true };
            var result = session is null
                ? await collection.BulkWriteAsync(writes, options, ct).ConfigureAwait(false)
                : await collection.BulkWriteAsync(session, writes, options, ct).ConfigureAwait(false);
            DemandAcknowledged(result.IsAcknowledged);
            return result;
        }
        catch (MongoBulkWriteException<BsonDocument> error)
            when (error.WriteErrors.Any(static item => item.Code == Infrastructure.Constants.Provider.DuplicateKeyError))
        {
            foreach (var failure in error.WriteErrors.Where(static item => item.Code == Infrastructure.Constants.Provider.DuplicateKeyError))
                if (writes[failure.Index] is ReplaceOneModel<BsonDocument> replacement
                    && replacement.Replacement.TryGetValue(Infrastructure.Constants.Storage.Identity, out var identity)
                    && await IsCrossScope(collection, Builders<BsonDocument>.Filter.Eq(
                        Infrastructure.Constants.Storage.Identity, identity), ct).ConfigureAwait(false))
                    throw CrossScope("batch", error);
            throw;
        }
    }

    private async Task<bool> IsCrossScope(IMongoCollection<BsonDocument> collection,
        FilterDefinition<BsonDocument> identity, CancellationToken ct)
    {
        var guard = _entity.WriteGuard();
        var render = new RenderArgs<BsonDocument>(collection.DocumentSerializer,
            MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry);
        if (guard.Render(render).ElementCount == 0) return false;
        return await collection.Find(Builders<BsonDocument>.Filter.And(identity,
            Builders<BsonDocument>.Filter.Not(guard))).AnyAsync(ct).ConfigureAwait(false);
    }

    private InvalidOperationException CrossScope(object? id, Exception error) => new(
        $"Rejected a cross-scope write to MongoDB collection '{CollectionName()}' id '{id}'.",
        error);

    private static void DemandAcknowledged(bool acknowledged)
    {
        if (!acknowledged)
            throw new InvalidOperationException("MongoDB returned an unacknowledged write; no exact outcome is available.");
    }

    private static TResult Cast<TResult>(object? value)
    {
        if (value is TResult typed) return typed;
        if (value is null) return default!;
        return (TResult)Convert.ChangeType(value, typeof(TResult), System.Globalization.CultureInfo.InvariantCulture);
    }
}
