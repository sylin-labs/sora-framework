using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koan.Cache.Abstractions.Policies;
using Koan.Cache.Abstractions.Primitives;
using Koan.Cache.Abstractions.Stores;
using Koan.Cache.Entity;
using Koan.Cache.Stores;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Core;
using Koan.Data.Core.Decorators;
using Microsoft.Extensions.Logging;

namespace Koan.Cache.Decorators;

internal sealed class CachedRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IInsertOnlyRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IRawQueryRepository<TEntity, TKey>,
    IDescribesCapabilities,
    IInstructionExecutor<TEntity>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IDataRepository<TEntity, TKey> _inner;
    private readonly IQueryRepository<TEntity, TKey>? _query;
    private readonly IRawQueryRepository<TEntity, TKey>? _rawQuery;
    private readonly IInstructionExecutor<TEntity>? _instructionExecutor;
    private readonly CacheClient _cacheClient;
    private readonly CachePolicyDescriptor _entityPolicy;
    private readonly EntityCachePlan.Resolution _plan;
    private readonly ILogger<CachedRepository<TEntity, TKey>> _logger;
    private readonly string _entityName;
    private readonly string? _routeNamespace;

    public CachedRepository(
        IDataRepository<TEntity, TKey> inner,
        CacheClient cacheClient,
        EntityCachePlan.Resolution plan,
        DataRepositoryDecorationContext decorationContext,
        ILogger<CachedRepository<TEntity, TKey>> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cacheClient = cacheClient ?? throw new ArgumentNullException(nameof(cacheClient));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentNullException.ThrowIfNull(decorationContext);
        _routeNamespace = string.IsNullOrWhiteSpace(decorationContext.RouteNamespace)
            ? null
            : decorationContext.RouteNamespace;
        _entityPolicy = plan.Policy;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _query = inner as IQueryRepository<TEntity, TKey>;
        _rawQuery = inner as IRawQueryRepository<TEntity, TKey>;
        _instructionExecutor = inner as IInstructionExecutor<TEntity>;
        _entityName = plan.EntityName;
    }

    // ARCH-0084: forward the inner provider's unified capabilities (native IDescribesCapabilities,
    // else the legacy-marker bridge).
    public void Describe(ICapabilities caps)
        => DataCaps.Describe(_inner, _inner.GetType().Name).CopyInto(caps);


    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        // The cache decorates outside Data's canonical read fold. A dynamic request predicate therefore has to bypass
        // the global id entry here, before a cache hit can short-circuit the scoped store read.
        if (_plan.IsReadScopedNow())
        {
            return await _inner.Get(id, ct);
        }

        var effectiveStrategy = ResolveEffectiveStrategy();

        if (effectiveStrategy is CacheStrategy.NoCache or CacheStrategy.SetOnly or CacheStrategy.Invalidate)
        {
            return await _inner.Get(id, ct);
        }

        if (!TryBuildEntityKey(null, id, out var key))
        {
            return await _inner.Get(id, ct);
        }

        var options = _entityPolicy.ToOptions();
        switch (effectiveStrategy)
        {
            case CacheStrategy.GetOrSet:
                return await _cacheClient.GetOrAddAsync<TEntity>(key, async innerCt =>
                {
                    var value = await _inner.Get(id, innerCt);
                    return value;
                }, options, typeof(TEntity), ct);

            case CacheStrategy.GetOnly:
                {
                    var cached = await _cacheClient.GetAsync<TEntity>(key, options, typeof(TEntity), ct);
                    if (cached is not null)
                    {
                        return cached;
                    }

                    return await _inner.Get(id, ct);
                }

            default:
                return await _inner.Get(id, ct);
        }
    }

    /// <summary>
    /// Resolve the effective read-side strategy by combining the policy's declared Strategy
    /// with any per-request <c>EntityContext.CacheBehavior</c> override. Writes (Upsert/Delete)
    /// always invalidate and are unaffected by this.
    /// </summary>
    private CacheStrategy ResolveEffectiveStrategy()
    {
        var behavior = EntityContext.Current?.CacheBehavior;
        return behavior switch
        {
            CacheBehavior.Bypass => CacheStrategy.NoCache,
            CacheBehavior.Refresh => CacheStrategy.SetOnly,
            CacheBehavior.ReadOnly => CacheStrategy.GetOnly,
            _ => _entityPolicy.Strategy
        };
    }

    public Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        // Pass-through to inner repository
        // TODO: Future enhancement - implement batch caching strategy
        return _inner.GetMany(ids, ct);
    }

    // ============================ Query / Count (delegated) ============================
    // The id-keyed entity cache cannot satisfy arbitrary filters, so structured queries pass
    // through to the inner repository's unified IQueryRepository contract.

    public Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        if (_query is null)
        {
            throw new NotSupportedException($"Repository for {_entityName} does not support queries.");
        }

        return _query.Query(query, ct);
    }

    public Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        if (_query is null)
        {
            throw new NotSupportedException($"Repository for {_entityName} does not support counts.");
        }

        return _query.Count(query, ct);
    }

    // ============================ Raw query (delegated passthrough) ============================

    public Task<RepositoryQueryResult<TEntity>> QueryRaw(string query, object? parameters, QueryDefinition shaping, CancellationToken ct = default)
    {
        if (_rawQuery is null)
        {
            throw new NotSupportedException($"Repository for {_entityName} does not support raw provider queries.");
        }

        return _rawQuery.QueryRaw(query, parameters, shaping, ct);
    }

    public Task<CountResult> CountRaw(string query, object? parameters, CancellationToken ct = default)
    {
        if (_rawQuery is null)
        {
            throw new NotSupportedException($"Repository for {_entityName} does not support raw provider queries.");
        }

        return _rawQuery.CountRaw(query, parameters, ct);
    }

    public async Task<MutationResult<TEntity, TKey>> Insert(TEntity model, CancellationToken ct = default)
    {
        if (_inner is not IInsertOnlyRepository<TEntity, TKey> inserts ||
            !DataCaps.Describe(_inner, _inner.GetType().Name).Has(DataCaps.Write.InsertOnly))
            throw new NotSupportedException($"The adapter backing {_entityName} does not support atomic insertion.");
        var submittedKey = model.Id;
        var result = await inserts.Insert(model, ct);
        // Invalidate only a proven successful identity. Do not seed cached values from a provider
        // receipt before the outer Data facade has accepted it or expose a conflicting stored row.
        if (result.Outcome == MutationOutcome.Inserted && result.CommitOutcome == DataCommitOutcome.Committed &&
            result.Entity is not null && EqualityComparer<TKey>.Default.Equals(result.Key, model.Id) &&
            EqualityComparer<TKey>.Default.Equals(result.Entity.Id, result.Key) &&
            (IsDefaultKey(submittedKey) || EqualityComparer<TKey>.Default.Equals(submittedKey, result.Key)) &&
            !IsDefaultKey(result.Key))
            await Remove(result.Key, ct);
        return result;
    }

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        var result = await _inner.Upsert(model, ct);
        await HandleWrite(result, ct);
        return result;
    }

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        var materialized = models as IList<TEntity> ?? models.ToList();
        var updated = await _inner.UpsertMany(materialized, ct);
        await HandleWrite(materialized, ct);
        return updated;
    }

    public async Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        var deleted = await _inner.Delete(id, ct);
        if (deleted)
        {
            await Remove(id, ct);
        }

        return deleted;
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        var materialized = ids as IList<TKey> ?? ids.ToList();
        var count = await _inner.DeleteMany(materialized, ct);
        if (count > 0)
        {
            foreach (var id in materialized)
            {
                await Remove(id, ct);
            }
        }

        return count;
    }

    public async Task<int> DeleteAll(CancellationToken ct = default)
    {
        var count = await _inner.DeleteAll(ct);
        if (count > 0)
        {
            _logger.LogDebug("DeleteAll detected for {Entity}. Cached entries cannot be enumerated automatically; downstream policies should prefer tag-based invalidation.", _entityName);
        }

        return count;
    }

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        var removed = await _inner.RemoveAll(strategy, ct);
        if (removed > 0)
        {
            _logger.LogDebug("RemoveAll({Strategy}) invoked for {Entity}; cached entries should be invalidated via tags.", strategy, _entityName);
        }

        return removed;
    }

    public IBatchSet<TEntity, TKey> CreateBatch()
    {
        var batch = _inner.CreateBatch();
        if (batch is null)
        {
            throw new InvalidOperationException($"Repository for {_entityName} returned a null batch set.");
        }

        return new CachingBatchSet(this, batch);
    }

    public async Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        if (_instructionExecutor is null)
        {
            throw new NotSupportedException($"Repository for {_entityName} does not support instruction execution.");
        }

        return await _instructionExecutor.ExecuteAsync<TResult>(instruction, ct);
    }

    public Task EnsureReady(CancellationToken ct = default) => _inner.EnsureReady(ct);

    private async ValueTask HandleWrite(IEnumerable<TEntity> entities, CancellationToken ct)
    {
        foreach (var entity in entities)
        {
            await HandleWrite(entity, ct);
        }
    }

    private async ValueTask HandleWrite(TEntity entity, CancellationToken ct)
    {
        if (_entityPolicy.Strategy is CacheStrategy.NoCache)
        {
            return;
        }

        // Never seed a globally keyed cache from a request-scoped write. Remove a possible older entry instead.
        if (_plan.IsReadScopedNow())
        {
            if (!IsDefaultKey(entity.Id)) await Remove(entity.Id, ct);
            return;
        }

        var key = ResolveKey(entity);
        if (key is null)
        {
            return;
        }

        switch (_entityPolicy.Strategy)
        {
            case CacheStrategy.GetOrSet:
            case CacheStrategy.SetOnly:
                var options = _entityPolicy.ToOptions();
                await _cacheClient.SetAsync(key.Value, entity, options, typeof(TEntity), ct);
                break;
            case CacheStrategy.GetOnly:
            case CacheStrategy.Invalidate:
                await _cacheClient.Remove(key.Value, typeof(TEntity), ct);
                break;
        }
    }

    private async ValueTask Remove(TKey id, CancellationToken ct)
    {
        if (_entityPolicy.Strategy is CacheStrategy.NoCache)
        {
            return;
        }

        if (!TryBuildEntityKey(null, id, out var key))
        {
            return;
        }

        await _cacheClient.Remove(key, typeof(TEntity), ct);
    }

    private CacheKey? ResolveKey(TEntity entity)
    {
        var id = entity.Id;
        if (IsDefaultKey(id))
        {
            _logger.LogDebug("Entity of type {Entity} produced a default key; skipping cache interaction.", _entityName);
            return null;
        }

        if (!TryBuildEntityKey(entity, id, out var key))
        {
            return null;
        }

        return key;
    }

    private bool TryBuildEntityKey(TEntity? entity, object? id, out CacheKey key)
    {
        if (!_plan.TryBuildKey(entity, id, out key, _routeNamespace))
        {
            _logger.LogDebug(
                "Cache policy for {Entity} could not resolve key template '{Template}'. Skipping cache interaction.",
                _entityName,
                _entityPolicy.KeyTemplate);
            return false;
        }
        return true;
    }

    private static bool IsDefaultKey(TKey key)
        => EqualityComparer<TKey>.Default.Equals(key, default!);

    private sealed class CachingBatchSet : IBatchSet<TEntity, TKey>
    {
        private readonly CachedRepository<TEntity, TKey> _outer;
        private readonly IBatchSet<TEntity, TKey> _inner;
        private readonly List<TEntity> _upserts = new();
        private readonly List<TKey> _deletes = new();
        private readonly HashSet<TKey> _mutations = new();

        public CachingBatchSet(CachedRepository<TEntity, TKey> outer, IBatchSet<TEntity, TKey> inner)
        {
            _outer = outer;
            _inner = inner;
        }

        public IBatchSet<TEntity, TKey> Add(TEntity entity)
        {
            _upserts.Add(entity);
            _inner.Add(entity);
            return this;
        }

        public IBatchSet<TEntity, TKey> Update(TEntity entity)
        {
            _upserts.Add(entity);
            _inner.Update(entity);
            return this;
        }

        public IBatchSet<TEntity, TKey> Delete(TKey id)
        {
            _deletes.Add(id);
            _inner.Delete(id);
            return this;
        }

        public IBatchSet<TEntity, TKey> Update(TKey id, Action<TEntity> mutate)
        {
            _mutations.Add(id);
            _inner.Update(id, mutate);
            return this;
        }

        public IBatchSet<TEntity, TKey> Clear()
        {
            _upserts.Clear();
            _deletes.Clear();
            _mutations.Clear();
            _inner.Clear();
            return this;
        }

        public async Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            var result = await _inner.Save(options, ct);

            foreach (var entity in _upserts)
            {
                await _outer.HandleWrite(entity, ct);
            }

            foreach (var id in _deletes)
            {
                await _outer.Remove(id, ct);
            }

            foreach (var id in _mutations)
            {
                await _outer.Remove(id, ct);
            }

            _upserts.Clear();
            _deletes.Clear();
            _mutations.Clear();

            return result;
        }
    }
}
