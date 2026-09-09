using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Reflection;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core;
using Koan.Data.Core.Sorting;
using StackExchange.Redis;

namespace Koan.Data.Connector.Redis.Runtime;

internal sealed class RedisRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IBoundedQueryRepository<TEntity, TKey>,
    IConditionalWriteRepository<TEntity, TKey>,
    IBulkUpsert<TKey>,
    IBulkDelete<TKey>,
    IDescribesCapabilities
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IServiceProvider _services;
    private readonly INamingProvider _naming;
    private readonly RedisRoute _route;
    private readonly RedisEntityPlan<TEntity, TKey> _entity;

    internal RedisRepository(
        IServiceProvider services,
        INamingProvider storageNaming,
        RedisRoute route,
        MappingPlan? mapping)
    {
        _services = services;
        _naming = storageNaming;
        _route = route;
        _entity = new RedisEntityPlan<TEntity, TKey>(services, route.Source, mapping);
    }

    public Task EnsureReady(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var set = Set(EntityContext.Current?.Partition);
        var value = await _route.Data.StringGetAsync(set.Record(_entity.Identity(id))).WaitAsync(ct).ConfigureAwait(false);
        return value.IsNull ? null : _entity.Read(value!);
    }

    public async Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var values = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        if (values.Count == 0) return [];
        DemandBulk(values.Count);
        var set = Set(EntityContext.Current?.Partition);
        var keys = values.Select(id => (RedisKey)set.Record(_entity.Identity(id))).ToArray();
        var documents = await _route.Data.StringGetAsync(keys).WaitAsync(ct).ConfigureAwait(false);
        return documents.Select(value => value.IsNull ? null : _entity.Read(value!)).ToArray();
    }

    public async Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var values = await ReadManaged(query.Partition ?? EntityContext.Current?.Partition, _route.MaxQueryEntries, ct)
            .ConfigureAwait(false);
        return Execute(values, query);
    }

    public async Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var values = await ReadManaged(query.Partition ?? EntityContext.Current?.Partition, _route.MaxQueryEntries, ct)
            .ConfigureAwait(false);
        if (query.Filter is null) return CountResult.Exact(values.Count);
        return CountResult.Exact(values.LongCount(RedisFilter.Compile<TEntity>(query.Filter)));
    }

    public async Task<BoundedQueryResult<TEntity>> QueryBoundedCandidates(
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct = default)
    {
        if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        DemandManagedSet("query bounded Redis candidates");
        var set = Set(query.Partition ?? EntityContext.Current?.Partition);
        var cardinality = await _route.Data.SetLengthAsync(set.Members).WaitAsync(ct).ConfigureAwait(false);
        if (cardinality > maxCandidates)
            return new BoundedQueryResult<TEntity>([], checked((int)Math.Min(cardinality, int.MaxValue)), CandidateLimitExceeded: true);
        var values = await ReadManaged(set, maxCandidates, ct).ConfigureAwait(false);
        return new BoundedQueryResult<TEntity>(Execute(values, query).Items, values.Count, CandidateLimitExceeded: false);
    }

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        DemandWrite("upsert Redis entity");
        var set = Set(EntityContext.Current?.Partition);
        await Guard(set, model.Id, ct).ConfigureAwait(false);
        await Write(set, model, preserveMapped: true, ct).ConfigureAwait(false);
        return model;
    }

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        DemandWrite("upsert Redis entities");
        var values = models as IReadOnlyList<TEntity> ?? models.ToArray();
        DemandBulk(values.Count);
        if (values.Count == 0) return 0;
        var set = Set(EntityContext.Current?.Partition);
        if (_entity.Mapping is null && ManagedFieldWriteScope.Current is not { Count: > 0 })
        {
            var transaction = _route.Data.CreateTransaction();
            var members = new List<RedisValue>(values.Count);
            foreach (var model in values)
            {
                ct.ThrowIfCancellationRequested();
                var key = (RedisKey)set.Record(_entity.Identity(model.Id));
                var expiry = Expiry(model);
                if (expiry == TimeSpan.Zero)
                {
                    _ = transaction.KeyDeleteAsync(key);
                    if (_route.StorageLifecycle == StorageLifecycle.Managed)
                        _ = transaction.SetRemoveAsync(set.Members, key.ToString());
                    continue;
                }
                _ = transaction.StringSetAsync(
                    key,
                    _entity.Create(model).ToString(Newtonsoft.Json.Formatting.None),
                    RedisExpiry(expiry));
                members.Add(key.ToString());
            }
            if (_route.StorageLifecycle == StorageLifecycle.Managed && members.Count > 0)
                _ = transaction.SetAddAsync(set.Members, members.ToArray());
            if (!await transaction.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Redis rejected the bulk entity transaction.");
            return values.Count;
        }
        foreach (var model in values)
        {
            ct.ThrowIfCancellationRequested();
            await Guard(set, model.Id, ct).ConfigureAwait(false);
            await Write(set, model, preserveMapped: true, ct).ConfigureAwait(false);
        }
        return values.Count;
    }

    public async Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        DemandWrite("delete Redis entity");
        var set = Set(EntityContext.Current?.Partition);
        var key = (RedisKey)set.Record(_entity.Identity(id));
        var expected = await Guard(set, id, ct).ConfigureAwait(false);
        if (_route.StorageLifecycle != StorageLifecycle.Managed)
            return await _route.Data.KeyDeleteAsync(key).WaitAsync(ct).ConfigureAwait(false);
        var transaction = _route.Data.CreateTransaction();
        if (!expected.IsNull) transaction.AddCondition(Condition.StringEqual(key, expected));
        var removed = transaction.KeyDeleteAsync(key);
        _ = transaction.SetRemoveAsync(set.Members, key.ToString());
        if (!await transaction.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false)) return false;
        return await removed.ConfigureAwait(false);
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var values = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        DemandBulk(values.Count);
        if (values.Count == 0) return 0;
        if (ManagedFieldWriteScope.Current is not { Count: > 0 })
        {
            DemandWrite("delete Redis entities");
            var set = Set(EntityContext.Current?.Partition);
            var keys = values.Select(id => (RedisKey)set.Record(_entity.Identity(id))).ToArray();
            if (_route.StorageLifecycle != StorageLifecycle.Managed)
                return checked((int)await _route.Data.KeyDeleteAsync(keys).WaitAsync(ct).ConfigureAwait(false));
            var transaction = _route.Data.CreateTransaction();
            var removed = transaction.KeyDeleteAsync(keys);
            _ = transaction.SetRemoveAsync(
                set.Members,
                keys.Select(static key => (RedisValue)key.ToString()).ToArray());
            if (!await transaction.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Redis rejected the bulk delete transaction.");
            return checked((int)await removed.ConfigureAwait(false));
        }
        var count = 0;
        foreach (var id in values)
            if (await Delete(id, ct).ConfigureAwait(false)) count++;
        return count;
    }

    public async Task<int> DeleteAll(CancellationToken ct = default) =>
        checked((int)await RemoveAll(RemoveStrategy.Safe, ct).ConfigureAwait(false));

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        DemandWrite("clear Redis managed set");
        DemandManagedSet("clear Redis entities");
        var set = Set(EntityContext.Current?.Partition);
        var cardinality = await _route.Data.SetLengthAsync(set.Members).WaitAsync(ct).ConfigureAwait(false);
        if (cardinality > _route.MaxQueryEntries) throw BoundExceeded(cardinality, _route.MaxQueryEntries);
        var members = await _route.Data.SetMembersAsync(set.Members).WaitAsync(ct).ConfigureAwait(false);
        long removed = 0;
        if (members.Length > 0)
        {
            var keys = members.Select(static member => (RedisKey)member.ToString()).ToArray();
            if (strategy == RemoveStrategy.Fast)
            {
                var args = keys.Select(static key => (object)key).ToArray();
                removed = (long)await _route.Data.ExecuteAsync("UNLINK", args).WaitAsync(ct).ConfigureAwait(false);
            }
            else removed = await _route.Data.KeyDeleteAsync(keys).WaitAsync(ct).ConfigureAwait(false);
        }
        await _route.Data.KeyDeleteAsync(set.Members).WaitAsync(ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<bool> ConditionalReplaceAsync(
        TEntity model,
        Filter guard,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(guard);
        DemandWrite("conditionally replace Redis entity");
        var expiry = Expiry(model);
        if (expiry == TimeSpan.Zero)
            throw new NotSupportedException("Redis conditional replacement cannot perform an immediate-expiry deletion. Use a non-expiring or positive-lived replacement.");
        var predicate = InMemoryFilterEvaluator.CompileConditional<TEntity>(guard);
        var set = Set(EntityContext.Current?.Partition);
        var key = (RedisKey)set.Record(_entity.Identity(model.Id));
        for (var attempt = 0; attempt < Infrastructure.Constants.MaximumConditionalAttempts; attempt++)
        {
            var current = await _route.Data.StringGetAsync(key).WaitAsync(ct).ConfigureAwait(false);
            if (current.IsNull) return false;
            var currentRecord = _entity.ReadRecord(current!);
            if (!predicate(currentRecord.Entity) || !ManagedGuardMatches(currentRecord.Managed)) return false;
            var document = _entity.Mapping is null ? _entity.Create(model) : Newtonsoft.Json.Linq.JObject.Parse(current!);
            if (_entity.Mapping is not null) _entity.Apply(document, model, MappingWriteOperation.ConditionalWrite);
            var transaction = _route.Data.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, current));
            _ = transaction.StringSetAsync(key, document.ToString(Newtonsoft.Json.Formatting.None), RedisExpiry(expiry));
            if (_route.StorageLifecycle == StorageLifecycle.Managed) _ = transaction.SetAddAsync(set.Members, key.ToString());
            if (await transaction.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false)) return true;
        }
        return false;
    }

    public IBatchSet<TEntity, TKey> CreateBatch() => new RedisBatch(this);

    public void Describe(ICapabilities capabilities) =>
        RedisFeatures.Describe(capabilities, _route.StorageLifecycle == StorageLifecycle.Managed);

    private async Task Write(RedisSet set, TEntity model, bool preserveMapped, CancellationToken ct)
    {
        var key = (RedisKey)set.Record(_entity.Identity(model.Id));
        var expiry = Expiry(model);
        if (expiry == TimeSpan.Zero)
        {
            await Delete(model.Id, ct).ConfigureAwait(false);
            return;
        }
        if (_entity.Mapping is not null && preserveMapped)
        {
            for (var attempt = 0; attempt < Infrastructure.Constants.MaximumConditionalAttempts; attempt++)
            {
                var current = await _route.Data.StringGetAsync(key).WaitAsync(ct).ConfigureAwait(false);
                var document = current.IsNull ? _entity.Create(model) : Newtonsoft.Json.Linq.JObject.Parse(current!);
                if (!current.IsNull) _entity.Apply(document, model, MappingWriteOperation.Update);
                var transaction = _route.Data.CreateTransaction();
                transaction.AddCondition(current.IsNull ? Condition.KeyNotExists(key) : Condition.StringEqual(key, current));
                _ = transaction.StringSetAsync(key, document.ToString(Newtonsoft.Json.Formatting.None), RedisExpiry(expiry));
                if (_route.StorageLifecycle == StorageLifecycle.Managed) _ = transaction.SetAddAsync(set.Members, key.ToString());
                if (await transaction.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false)) return;
            }
            throw new InvalidOperationException($"Redis could not commit '{typeof(TEntity).Name}' after concurrent changes.");
        }
        var json = _entity.Create(model).ToString(Newtonsoft.Json.Formatting.None);
        if (_route.StorageLifecycle != StorageLifecycle.Managed)
        {
            await _route.Data.StringSetAsync(key, json, RedisExpiry(expiry), ValueCondition.Always).WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        var write = _route.Data.CreateTransaction();
        _ = write.StringSetAsync(key, json, RedisExpiry(expiry));
        _ = write.SetAddAsync(set.Members, key.ToString());
        if (!await write.ExecuteAsync().WaitAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Redis rejected the managed entity transaction.");
    }

    private async Task<IReadOnlyList<RedisRecord<TEntity>>> ReadManaged(string? partition, int maximum, CancellationToken ct) =>
        await ReadManaged(Set(partition), maximum, ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<RedisRecord<TEntity>>> ReadManaged(RedisSet set, int maximum, CancellationToken ct)
    {
        DemandManagedSet("enumerate Redis entities");
        var cardinality = await _route.Data.SetLengthAsync(set.Members).WaitAsync(ct).ConfigureAwait(false);
        if (cardinality > maximum) throw BoundExceeded(cardinality, maximum);
        var members = await _route.Data.SetMembersAsync(set.Members).WaitAsync(ct).ConfigureAwait(false);
        if (members.Length == 0) return [];
        var keys = members.Select(static member => (RedisKey)member.ToString()).ToArray();
        var documents = await _route.Data.StringGetAsync(keys).WaitAsync(ct).ConfigureAwait(false);
        var values = new List<RedisRecord<TEntity>>(documents.Length);
        var stale = new List<RedisValue>();
        for (var index = 0; index < documents.Length; index++)
            if (documents[index].IsNull) stale.Add(members[index]);
            else values.Add(_entity.ReadRecord(documents[index]!));
        if (stale.Count > 0 && _route.Access == DataSourceAccess.ReadWrite)
            await _route.Data.SetRemoveAsync(set.Members, stale.ToArray()).WaitAsync(ct).ConfigureAwait(false);
        return values;
    }

    private static RepositoryQueryResult<TEntity> Execute(IReadOnlyList<RedisRecord<TEntity>> source, QueryDefinition query)
    {
        IEnumerable<TEntity> filtered = query.Filter is null
            ? source.Select(static record => record.Entity)
            : source.Where(RedisFilter.Compile<TEntity>(query.Filter)).Select(static record => record.Entity);
        var materialized = filtered as IReadOnlyList<TEntity> ?? filtered.ToArray();
        long? total = query.CountStrategy is null ? null : materialized.Count;
        IReadOnlyList<TEntity> ordered;
        IReadOnlySet<Koan.Data.Abstractions.Sorting.SortSpec> sortHandled;
        if (query.HasSort)
        {
            ordered = InMemorySorter.Apply(materialized, query.Sort);
            sortHandled = query.Sort.ToFrozenSet();
        }
        else
        {
            ordered = materialized.OrderBy(static entity => entity.Id, Comparer<TKey>.Default).ToArray();
            sortHandled = RepositoryQueryResult<TEntity>.NoSortHandled;
        }
        if (query.HasPagination)
            ordered = ordered.Skip(query.EffectiveOffset()).Take(query.EffectivePageSize()).ToArray();
        return new RepositoryQueryResult<TEntity>
        {
            Items = ordered,
            FilterHandled = query.Filter is not null,
            TotalCount = total,
            CountExecution = total is null ? CountExecutionKind.None : CountExecutionKind.Exact,
            SortHandled = sortHandled,
            PaginationHandled = query.HasPagination,
            // Redis is a key-value store: the set is read whole and shaped here.
            MaterializedAllCandidates = true
        };
    }

    private RedisSet Set(string? partition)
    {
        if (_entity.MappedContainer is { } mapped)
        {
            if (!string.IsNullOrWhiteSpace(partition))
                throw new MappingCompilationException(_route.Source, typeof(TEntity),
                    "An explicit Redis map pins one Container and cannot accept an ambient partition.");
            return RedisSet.Create(_route.Source, _route.Database, mapped);
        }
        var container = _naming.ResolveStorage(typeof(TEntity), partition, _services);
        return RedisSet.Create(_route.Source, _route.Database, container);
    }

    private void DemandWrite(string operation) => _route.Plan.Demand(DataOperationEffect.Write, operation);
    private void DemandManagedSet(string operation)
    {
        if (_route.StorageLifecycle != StorageLifecycle.Managed)
            throw new NotSupportedException(
                $"Redis source '{_route.Source}' is External, so Koan does not own a membership registry. " +
                $"Use known-key operations or a registered read-only Function instead of attempting to {operation}.");
    }

    private void DemandBulk(int count)
    {
        if (count > _route.MaxBulkEntries)
            throw new InvalidOperationException(
                $"Redis bulk operation contains {count} entries; configured MaxBulkEntries is {_route.MaxBulkEntries}.");
    }

    private static InvalidOperationException BoundExceeded(long actual, int maximum) => new(
        $"Redis managed set contains {actual} members, exceeding MaxQueryEntries={maximum}. " +
        "Use known-key access, increase the explicit bound, or choose a query-oriented provider.");

    private async Task<RedisValue> Guard(RedisSet set, TKey id, CancellationToken ct)
    {
        if (ManagedFieldWriteScope.Current is not { Count: > 0 }) return RedisValue.Null;
        var current = await _route.Data.StringGetAsync(set.Record(_entity.Identity(id))).WaitAsync(ct).ConfigureAwait(false);
        if (current.IsNull) return current;
        if (!ManagedGuardMatches(_entity.ReadRecord(current!).Managed))
            throw new InvalidOperationException(
                $"Rejected a cross-scope write to '{typeof(TEntity).Name}' id '{id}': the record belongs to another managed scope.");
        return current;
    }

    private static bool ManagedGuardMatches(IReadOnlyDictionary<string, object?>? stored)
    {
        if (ManagedFieldWriteScope.Current is not { Count: > 0 } guard) return true;
        foreach (var item in guard)
        {
            var value = stored is not null && stored.TryGetValue(item.Key, out var existing) ? existing : null;
            if (!(value is null ? item.Value is null : value.Equals(item.Value))) return false;
        }
        return true;
    }

    private static readonly PropertyInfo? TtlProperty = ResolveTtlProperty();

    private static PropertyInfo? ResolveTtlProperty() => Koan.Data.Core.IndexMetadata.GetIndexes(typeof(TEntity))
        .Where(static index => index.Ttl && index.Properties.Count == 1)
        .Select(static index => index.Properties[0])
        .SingleOrDefault();

    private static TimeSpan? Expiry(TEntity model)
    {
        if (TtlProperty is null) return null;
        var instant = TtlProperty.GetValue(model) switch
        {
            DateTimeOffset value => value.UtcDateTime,
            DateTime value => value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime(),
            _ => (DateTime?)null
        };
        if (instant is null) return null;
        var ttl = instant.Value - DateTime.UtcNow;
        return ttl <= TimeSpan.Zero ? TimeSpan.Zero : ttl;
    }

    private static Expiration RedisExpiry(TimeSpan? expiry) =>
        expiry is { } value ? new Expiration(value) : Expiration.Default;

    private sealed class RedisBatch(RedisRepository<TEntity, TKey> repository) : IBatchSet<TEntity, TKey>
    {
        private readonly List<TEntity> _upserts = [];
        private readonly List<TKey> _deletes = [];
        private readonly List<(TKey Id, Action<TEntity> Mutate)> _mutations = [];

        public IBatchSet<TEntity, TKey> Add(TEntity entity) { _upserts.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TEntity entity) { _upserts.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Delete(TKey id) { _deletes.Add(id); return this; }
        public IBatchSet<TEntity, TKey> Update(TKey id, Action<TEntity> mutate) { _mutations.Add((id, mutate)); return this; }
        public IBatchSet<TEntity, TKey> Clear() { _upserts.Clear(); _deletes.Clear(); _mutations.Clear(); return this; }

        public async Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            if (options?.RequireAtomic == true)
                throw new NotSupportedException("Redis atomic batches are not claimed by the Entity contract.");
            if (!string.IsNullOrWhiteSpace(options?.IdempotencyKey))
                throw new NotSupportedException("Redis Entity batches do not claim idempotency keys.");
            var total = checked(_upserts.Count + _deletes.Count + _mutations.Count);
            if (options?.MaxItems is { } maximum && total > maximum)
                throw new InvalidOperationException($"Redis batch contains {total} operations, exceeding MaxItems={maximum}.");
            repository.DemandBulk(total);
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

internal sealed record RedisSet(string Prefix)
{
    internal RedisKey Members => Prefix + ":members";
    internal RedisKey Record(string identity) => Prefix + ":record:" + RedisKeyLayout.Encode(identity);

    internal static RedisSet Create(string source, int database, string container) =>
        new(RedisKeyLayout.Prefix(source, database, container));
}
