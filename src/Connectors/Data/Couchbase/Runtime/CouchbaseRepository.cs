using System.Linq.Expressions;
using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.KeyValue;
using Couchbase.Query;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Koan.Data.Connector.Couchbase.Runtime;

internal sealed class CouchbaseRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IBoundedQueryRepository<TEntity, TKey>,
    IConditionalWriteRepository<TEntity, TKey>,
    IInstructionExecutor<TEntity>,
    IDescribesCapabilities
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private const int MutationAttempts = 5;
    private const int BulkConcurrency = 16;
    private readonly IServiceProvider _services;
    private readonly INamingProvider _naming;
    private readonly CouchbaseRoute _route;
    private readonly CouchbaseResourcePool _resources;
    private readonly CouchbaseDocumentPlan<TEntity, TKey> _entity;
    private readonly CouchbaseQueryCompiler<TEntity, TKey> _queries;
    private readonly CouchbaseSchema _schema;

    internal CouchbaseRepository(
        IServiceProvider services,
        INamingProvider naming,
        CouchbaseRoute route,
        CouchbaseResourcePool resources,
        MappingPlan? mapping)
    {
        _services = services;
        _naming = naming;
        _route = route;
        _resources = resources;
        _entity = new CouchbaseDocumentPlan<TEntity, TKey>(services, route.Source, mapping);
        _queries = new CouchbaseQueryCompiler<TEntity, TKey>(_entity);
        _schema = new CouchbaseSchema(route, resources, _entity.DeclaredIndexes());
    }

    public Task EnsureReady(CancellationToken ct = default) =>
        _schema.Ensure(Container(EntityContext.Current?.Partition), queryable: false, ct);

    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        var collection = await Collection(Container(EntityContext.Current?.Partition), queryable: false, ct).ConfigureAwait(false);
        try
        {
            using var result = await collection.GetAsync(_entity.Key(id), options => options.CancellationToken(ct))
                .ConfigureAwait(false);
            return _entity.Read(Document(result.ContentAs<JObject>()));
        }
        catch (DocumentNotFoundException) { return null; }
    }

    public async Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var values = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        var results = new TEntity?[values.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, values.Count),
            new ParallelOptions { MaxDegreeOfParallelism = BulkConcurrency, CancellationToken = ct },
            async (index, token) => results[index] = await Get(values[index], token).ConfigureAwait(false));
        return results;
    }

    public async Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        var container = Container(query.Partition ?? EntityContext.Current?.Partition);
        var plan = _queries.Compile(query, container);
        var items = await Read(plan, container, ct).ConfigureAwait(false);
        long? total = null;
        if (query.CountStrategy is not null)
            total = await CountValue(query.WithoutPagination(), container, ct).ConfigureAwait(false);
        return new RepositoryQueryResult<TEntity>
        {
            Items = items,
            FilterHandled = true,
            TotalCount = total,
            IsEstimate = false,
            CountExecution = total is null ? CountExecutionKind.None : CountExecutionKind.Exact,
            SortHandled = plan.SortHandled,
            // Only when the plan actually applied the caller's ordering; otherwise Data owns both the sort
            // and the window, and claiming the page here would give it an arbitrary slice to order.
            PaginationHandled = query.HasPagination && plan.Ordered
        };
    }

    public async Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        var container = Container(query.Partition ?? EntityContext.Current?.Partition);
        return CountResult.Exact(await CountValue(query.WithoutPagination(), container, ct).ConfigureAwait(false));
    }

    public async Task<BoundedQueryResult<TEntity>> QueryBoundedCandidates(
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct = default)
    {
        if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        var unpaged = query.WithoutPagination().WithCountStrategy(null);
        var container = Container(query.Partition ?? EntityContext.Current?.Partition);
        var plan = _queries.Compile(unpaged, container, checked(maxCandidates + 1));
        var values = await Read(plan, container, ct).ConfigureAwait(false);
        var exceeded = values.Count > maxCandidates;
        return new BoundedQueryResult<TEntity>(
            exceeded ? values.Take(maxCandidates).ToArray() : values,
            values.Count,
            exceeded);
    }

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        DemandWrite("upsert Couchbase document");
        var container = Container(EntityContext.Current?.Partition);
        var collection = await Collection(container, queryable: false, ct).ConfigureAwait(false);
        await Write(collection, model, ct).ConfigureAwait(false);
        return model;
    }

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        DemandWrite("upsert Couchbase documents");
        var values = models as IReadOnlyList<TEntity> ?? models.ToArray();
        var container = Container(EntityContext.Current?.Partition);
        var collection = await Collection(container, queryable: false, ct).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            values,
            new ParallelOptions { MaxDegreeOfParallelism = BulkConcurrency, CancellationToken = ct },
            (model, token) => new ValueTask(Write(collection, model, token)));
        return values.Count;
    }

    public async Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        DemandWrite("delete Couchbase document");
        var collection = await Collection(Container(EntityContext.Current?.Partition), queryable: false, ct).ConfigureAwait(false);
        var key = _entity.Key(id);
        try
        {
            if (ManagedFieldWriteScope.Current is not { Count: > 0 } guard)
            {
                await collection.RemoveAsync(
                    key,
                    options => options.Durability(_route.Durability).CancellationToken(ct)).ConfigureAwait(false);
                return true;
            }
            using var current = await collection.GetAsync(key, options => options.CancellationToken(ct)).ConfigureAwait(false);
            if (!GuardMatches(Document(current.ContentAs<JObject>()), guard)) return false;
            await collection.RemoveAsync(
                key,
                options => options.Cas(current.Cas).Durability(_route.Durability).CancellationToken(ct)).ConfigureAwait(false);
            return true;
        }
        catch (DocumentNotFoundException) { return false; }
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var removed = 0;
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = BulkConcurrency, CancellationToken = ct },
            async (id, token) => { if (await Delete(id, token).ConfigureAwait(false)) Interlocked.Increment(ref removed); });
        return removed;
    }

    public async Task<int> DeleteAll(CancellationToken ct = default) =>
        checked((int)await RemoveAll(RemoveStrategy.Safe, ct).ConfigureAwait(false));

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        DemandWrite("clear Couchbase container");
        var values = await Query(QueryDefinition.All, ct).ConfigureAwait(false);
        return await DeleteMany(values.Items.Select(static item => item.Id), ct).ConfigureAwait(false);
    }

    public async Task<bool> ConditionalReplaceAsync(
        TEntity model,
        Filter guard,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(guard);
        DemandWrite("conditionally replace Couchbase document");
        var predicate = InMemoryFilterEvaluator.CompileConditional<TEntity>(guard);
        var collection = await Collection(Container(EntityContext.Current?.Partition), queryable: false, ct).ConfigureAwait(false);
        var key = _entity.Key(model.Id);
        try
        {
            using var current = await collection.GetAsync(key, options => options.CancellationToken(ct)).ConfigureAwait(false);
            var document = Document(current.ContentAs<JObject>());
            var existing = _entity.Read(document);
            if (!predicate(existing) || !GuardMatches(document, ManagedFieldWriteScope.Current)) return false;
            if (_entity.Mapping is null) document = _entity.Write(model);
            else _entity.ApplyMappedWrite(document, model);
            await collection.ReplaceAsync(
                key,
                document,
                options => options.Cas(current.Cas).Durability(_route.Durability).CancellationToken(ct)).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is DocumentNotFoundException or CasMismatchException) { return false; }
    }

    public IBatchSet<TEntity, TKey> CreateBatch() => new CouchbaseBatch(this);

    public async Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        object value = instruction.Name switch
        {
            DataInstructions.EnsureCreated => await EnsureCreated(ct).ConfigureAwait(false),
            DataInstructions.Clear => await DeleteAll(ct).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"Instruction '{instruction.Name}' is not supported by Couchbase for '{typeof(TEntity).Name}'.")
        };
        return value is TResult typed
            ? typed
            : (TResult)Convert.ChangeType(value, typeof(TResult), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Describe(ICapabilities capabilities) => CouchbaseFeatures.Describe(capabilities);

    private async Task<bool> EnsureCreated(CancellationToken ct)
    {
        await EnsureReady(ct).ConfigureAwait(false);
        return true;
    }

    private async Task Write(ICouchbaseCollection collection, TEntity model, CancellationToken ct)
    {
        var key = _entity.Key(model.Id);
        var guard = ManagedFieldWriteScope.Current;
        if (_entity.Mapping is null && guard is not { Count: > 0 })
        {
            await collection.UpsertAsync(
                    key,
                    _entity.Write(model),
                    options => options.Durability(_route.Durability).CancellationToken(ct))
                .ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt < MutationAttempts; attempt++)
        {
            try
            {
                using var current = await collection.GetAsync(key, options => options.CancellationToken(ct)).ConfigureAwait(false);
                var document = Document(current.ContentAs<JObject>());
                if (!GuardMatches(document, guard))
                    throw new InvalidOperationException(
                        $"Rejected a cross-scope write to Couchbase id '{key}'.");
                if (_entity.Mapping is null) document = _entity.Write(model);
                else _entity.ApplyMappedWrite(document, model);
                await collection.ReplaceAsync(
                    key,
                    document,
                    options => options.Cas(current.Cas).Durability(_route.Durability).CancellationToken(ct)).ConfigureAwait(false);
                return;
            }
            catch (DocumentNotFoundException)
            {
                try
                {
                    await collection.InsertAsync(
                            key,
                            _entity.Write(model),
                            options => options.Durability(_route.Durability).CancellationToken(ct))
                        .ConfigureAwait(false);
                    return;
                }
                catch (DocumentExistsException) when (attempt + 1 < MutationAttempts) { }
            }
            catch (CasMismatchException) when (attempt + 1 < MutationAttempts) { }
        }
        throw new InvalidOperationException($"Couchbase could not commit id '{key}' after {MutationAttempts} CAS attempts.");
    }

    private async Task<IReadOnlyList<TEntity>> Read(
        CouchbaseQueryPlan plan,
        CouchbaseContainer container,
        CancellationToken ct)
    {
        var target = await _resources.Target(_route, ct).ConfigureAwait(false);
        await _schema.Ensure(container, queryable: true, ct).ConfigureAwait(false);
        var statement = "SELECT RAW doc FROM " + container.Qualified(_route.Bucket) + " AS doc" +
                        (plan.Where is null ? "" : " WHERE " + plan.Where) +
                        " ORDER BY " + plan.Order +
                        (plan.Limit is null ? "" : " LIMIT " + plan.Limit.Value) +
                        (plan.Offset == 0 ? "" : " OFFSET " + plan.Offset);
        var options = Configure(new QueryOptions(), plan.Parameters, readOnly: true);
        var result = await target.Cluster.QueryAsync<JObject>(statement, options)
            .ConfigureAwait(false);
        var values = new List<TEntity>();
        await foreach (var row in result.Rows.WithCancellation(ct).ConfigureAwait(false))
            values.Add(_entity.Read(row));
        return values;
    }

    private async Task<long> CountValue(QueryDefinition query, CouchbaseContainer container, CancellationToken ct)
    {
        var plan = _queries.Compile(query, container);
        var target = await _resources.Target(_route, ct).ConfigureAwait(false);
        await _schema.Ensure(container, queryable: true, ct).ConfigureAwait(false);
        var statement = "SELECT RAW COUNT(1) FROM " + container.Qualified(_route.Bucket) + " AS doc" +
                        (plan.Where is null ? "" : " WHERE " + plan.Where);
        var options = Configure(new QueryOptions(), plan.Parameters, readOnly: true);
        var result = await target.Cluster.QueryAsync<long>(statement, options)
            .ConfigureAwait(false);
        await foreach (var row in result.Rows.WithCancellation(ct).ConfigureAwait(false)) return row;
        return 0;
    }

    private QueryOptions Configure(QueryOptions options, IReadOnlyDictionary<string, object?> parameters, bool readOnly)
    {
        options.Readonly(readOnly)
            .ScanConsistency(QueryScanConsistency.RequestPlus)
            .Timeout(_route.QueryTimeout);
        foreach (var parameter in parameters)
            options.Parameter(parameter.Key, parameter.Value ?? JValue.CreateNull());
        return options;
    }

    private async Task<ICouchbaseCollection> Collection(
        CouchbaseContainer container,
        bool queryable,
        CancellationToken ct)
    {
        await _schema.Ensure(container, queryable, ct).ConfigureAwait(false);
        var target = await _resources.Target(_route, ct).ConfigureAwait(false);
        var scope = await target.Bucket.ScopeAsync(container.Scope).ConfigureAwait(false);
        return await scope.CollectionAsync(container.Collection).ConfigureAwait(false);
    }

    private CouchbaseContainer Container(string? partition)
    {
        if (_entity.MappedContainer is { } mapped)
        {
            if (!string.IsNullOrWhiteSpace(partition))
                throw new MappingCompilationException(_route.Source, typeof(TEntity),
                    "An explicit Couchbase map pins one scope/collection and cannot accept an ambient container partition.");
            if (mapped.Namespace.Count > 1)
                throw new MappingCompilationException(_route.Source, typeof(TEntity),
                    "A Couchbase map accepts at most one Namespace segment for the scope.");
            return new CouchbaseContainer(
                mapped.Namespace.Count == 0 ? _route.DefaultScope : mapped.Namespace[0],
                mapped.Name);
        }

        var scope = string.IsNullOrWhiteSpace(partition)
            ? _route.DefaultScope
            : CouchbaseAdapterFactory.FormatScope(partition);
        var name = _naming.ResolveStorage(typeof(TEntity), null, _services);
        return new CouchbaseContainer(scope, CouchbaseAdapterFactory.FormatCollection(name));
    }

    private void DemandWrite(string operation) => _route.Plan.Demand(DataOperationEffect.Write, operation);

    private static bool GuardMatches(JObject document, IReadOnlyDictionary<string, object?>? guard)
    {
        if (guard is not { Count: > 0 }) return true;
        foreach (var value in guard)
            if (!document.TryGetValue(value.Key, StringComparison.Ordinal, out var stored) ||
                !JToken.DeepEquals(stored, value.Value is null ? JValue.CreateNull() : JToken.FromObject(value.Value)))
                return false;
        return true;
    }

    private static JObject Document(JObject? value) => value
        ?? throw new InvalidDataException("Couchbase returned an empty JSON document.");

    private sealed class CouchbaseBatch(CouchbaseRepository<TEntity, TKey> repository) : IBatchSet<TEntity, TKey>
    {
        private readonly List<TEntity> _upserts = [];
        private readonly List<TKey> _deletes = [];
        private readonly List<(TKey Id, Action<TEntity> Mutate)> _mutations = [];

        public IBatchSet<TEntity, TKey> Add(TEntity entity) { _upserts.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TEntity entity) { _upserts.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TKey id, Action<TEntity> mutate) { _mutations.Add((id, mutate)); return this; }
        public IBatchSet<TEntity, TKey> Delete(TKey id) { _deletes.Add(id); return this; }
        public IBatchSet<TEntity, TKey> Clear() { _upserts.Clear(); _deletes.Clear(); _mutations.Clear(); return this; }

        public async Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            if (options?.RequireAtomic == true)
                throw new NotSupportedException(
                    "Couchbase atomic batches are not claimed until Koan has an explicit replay-safe transaction callback contract.");
            var total = checked(_upserts.Count + _deletes.Count + _mutations.Count);
            if (options?.MaxItems is { } maximum && total > maximum)
                throw new InvalidOperationException($"Couchbase batch contains {total} operations, exceeding MaxItems={maximum}.");
            if (!string.IsNullOrWhiteSpace(options?.IdempotencyKey))
                throw new NotSupportedException("Couchbase idempotency keys are not claimed.");

            var updated = 0;
            foreach (var mutation in _mutations)
            {
                var current = await repository.Get(mutation.Id, ct).ConfigureAwait(false);
                if (current is null) continue;
                mutation.Mutate(current);
                _upserts.Add(current);
                updated++;
            }
            await repository.UpsertMany(_upserts, ct).ConfigureAwait(false);
            var deleted = await repository.DeleteMany(_deletes, ct).ConfigureAwait(false);
            return new BatchResult(_upserts.Count - updated, updated, deleted);
        }
    }
}
