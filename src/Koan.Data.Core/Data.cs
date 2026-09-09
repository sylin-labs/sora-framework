using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Koan.Core;
using Koan.Core.Capabilities;
using Koan.Core.Context;
using Koan.Core.Diagnostics;
using Koan.Core.Hosting.App;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core.Querying;
using Koan.Data.Core.Routing;
using Koan.Data.Core.Sorting;
using Koan.Data.Core.Execution;
using Koan.Data.Core.Transfers;

namespace Koan.Data.Core;

public static class Data<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private const string DataOperation = "entity data access";
    private const string IdentityOperation = "aggregate identity assignment";

    private static IDataService Service
        => AppHost.GetRequiredService<IDataService>(DataOperation);

    private static IDataRepository<TEntity, TKey> Repo
        => Service.GetRepository<TEntity, TKey>();

    private static ValueTask<DataMultiOperationLease> EnterMutationHorizon(
        IDataRepository<TEntity, TKey> repository,
        string operation,
        CancellationToken ct)
    {
        if (repository is not RepositoryFacade<TEntity, TKey> { RouteBinding: { } binding })
            throw new InvalidOperationException(
                $"The repository for '{typeof(TEntity).FullName}' is not bound to a physical Data route.");
        return AppHost.GetRequiredService<DataOperationHorizon>(operation)
            .EnterMany([binding], operation, ct);
    }

    /// <summary>
    /// The provider's capabilities as the unified <see cref="CapabilitySet"/> (ARCH-0084), resolved
    /// from the repo's native <c>IDescribesCapabilities</c> declaration.
    /// </summary>
    public static CapabilitySet Capabilities
    {
        get
        {
            var repo = Repo;
            return DataCaps.Describe(repo, repo.GetType().Name);
        }
    }

    /// <summary>The resolved repository cast to an optional capability interface (e.g.
    /// <see cref="IConditionalWriteRepository{TEntity,TKey}"/>), or <c>null</c> if the backing adapter doesn't
    /// implement it. The cast IS the capability probe — callers branch on null to a fallback.</summary>
    public static TCapability? As<TCapability>() where TCapability : class
    {
        var repo = Repo;
        if (typeof(TCapability) == typeof(IConditionalWriteRepository<TEntity, TKey>) &&
            !DataCaps.Describe(repo, repo.GetType().Name).Has(DataCaps.Write.ConditionalReplace))
            return null;
        return repo as TCapability;
    }

    // ARCH-0084: the adapter's filter support is the FilterSupport detail on its DataCaps.Query.Filter
    // capability token (no separate property). Absent token => None => every filter node is residual.
    private static FilterSupport ResolveFilterSupport(IDataRepository<TEntity, TKey> repo)
        => DataCaps.Describe(repo, repo.GetType().Name).Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;

    private static IQueryRepository<TEntity, TKey> RequireQuery(IDataRepository<TEntity, TKey> repo)
        => repo as IQueryRepository<TEntity, TKey>
           ?? throw new NotSupportedException(
               $"The adapter backing {typeof(TEntity).Name} does not implement IQueryRepository. " +
               $"Every queryable adapter must support QueryDefinition queries.");

    private static IRawQueryRepository<TEntity, TKey> RequireRaw(IDataRepository<TEntity, TKey> repo)
        => repo as IRawQueryRepository<TEntity, TKey>
           ?? throw new NotSupportedException(
               $"The adapter backing {typeof(TEntity).Name} does not support raw provider queries. " +
               $"Use a LINQ predicate or the JSON filter DSL instead.");

    // ------------------------------------------------------------------
    // Filter lowering — the entity-first DX (LINQ predicates) lowers into
    // the unified Filter AST so it converges with the JSON DSL path.
    // ------------------------------------------------------------------
    private static Filter Lower(Expression<Func<TEntity, bool>> predicate) => LinqFilterCompiler.Compile(predicate);

    // ------------------------------------------------------------------
    // The one materialized-result execution path: plan (split vs caps) → adapter → finalize
    // (residual + sort-fallback + paginate-after), centrally. Provider-bounded async streams use
    // QueryStreamCoordinator because their candidate-page semantics are intentionally different.
    // ------------------------------------------------------------------
    public static async Task<QueryResult<TEntity>> QueryWithCount(
        QueryDefinition query,
        CancellationToken ct = default,
        int? absoluteMaxRecords = null)
    {
        if (absoluteMaxRecords is < 0) throw new ArgumentOutOfRangeException(nameof(absoluteMaxRecords));
        using var partition = EntityContext.With(partition: query.Partition);
        query = query with { Filter = Filter.Snapshot(query.Filter), Sort = query.Sort.ToArray() };
        var repo = Repo;
        var q = repo as IQueryRepository<TEntity, TKey> ?? RequireQuery(repo);
        var filterSupport = ResolveFilterSupport(repo);
        var countStrategy = query.CountStrategy ?? CountStrategy.Optimized;
        query = query.WithCountStrategy(countStrategy);

        var hasPagination = query.HasPagination;
        var (adapterQuery, residual) = FilterPushdownCoordinator.Plan(query, filterSupport, typeof(TEntity));

        // Safety cap on unpaged queries: count first, refuse if over the cap.
        if (!hasPagination && absoluteMaxRecords.HasValue)
        {
            // Only a clean count when nothing residual; otherwise we must materialize to know the true total.
            if (residual is null)
            {
                var pre = ValidateCountResult(
                    await q.Count(adapterQuery, ct),
                    countStrategy,
                    DataCaps.Describe(repo, repo.GetType().Name));
                if (pre.Value > absoluteMaxRecords.Value)
                    return Exceeded(pre.Value, pre.IsEstimate);
            }
        }

        RepositoryQueryResult<TEntity> adapterResult;
        if (!hasPagination && absoluteMaxRecords.HasValue && residual is not null)
        {
            if (repo is not IBoundedQueryRepository<TEntity, TKey> bounded)
                throw new NotSupportedException(
                    $"The adapter backing {typeof(TEntity).Name} cannot enforce the requested residual-query safety bound. " +
                    "Use a natively pushable filter, explicit provider-bounded paging, or a bounded-capable adapter.");
            var candidateLimit = absoluteMaxRecords.Value == int.MaxValue
                ? int.MaxValue
                : absoluteMaxRecords.Value + 1;
            var boundedResult = await DataQueryExecution<TEntity, TKey>.QueryBoundedCandidates(
                repo, bounded, adapterQuery, candidateLimit, ct);
            if (boundedResult.CandidateLimitExceeded || boundedResult.CandidatesExamined > candidateLimit)
                return Exceeded(candidateLimit, estimate: false);
            adapterResult = new RepositoryQueryResult<TEntity>
            {
                Items = boundedResult.Items,
                FilterHandled = adapterQuery.Filter is not null
            };
        }
        else
        {
            adapterResult = await DataQueryExecution<TEntity, TKey>.QueryCandidates(repo, q, adapterQuery, ct);
        }
        var finalized = FilterPushdownCoordinator.Finalize(query, adapterQuery, residual, adapterResult);

        if (!hasPagination && absoluteMaxRecords.HasValue && finalized.TotalCount > absoluteMaxRecords.Value)
            return Exceeded(finalized.TotalCount, finalized.IsEstimate);

        await DataQueryExecution<TEntity, TKey>.MaterializeVisible(repo, finalized.Page, ct);
        DataQueryExecution<TEntity, TKey>.ValidateEvidence(adapterResult.ReadEvidence, finalized.Page);

        if (!hasPagination)
        {
            return new QueryResult<TEntity>
            {
                ReadEvidence = adapterResult.ReadEvidence,
                Items = finalized.Page,
                TotalCount = finalized.TotalCount,
                Page = 1,
                PageSize = finalized.Page.Count,
                RepositoryHandledPagination = adapterResult.PaginationHandled,
                ExceededSafetyLimit = false,
                IsEstimate = finalized.IsEstimate
            };
        }

        return new QueryResult<TEntity>
        {
            ReadEvidence = adapterResult.ReadEvidence,
            Items = finalized.Page,
            TotalCount = finalized.TotalCount,
            Page = query.EffectivePage(),
            PageSize = query.EffectivePageSize(),
            // The coordinator guarantees Items is the correct page (adapter-native or paginated-after).
            RepositoryHandledPagination = true,
            ExceededSafetyLimit = false,
            IsEstimate = finalized.IsEstimate
        };

        static QueryResult<TEntity> Exceeded(long total, bool estimate) => new()
        {
            Items = [],
            TotalCount = total,
            Page = 1,
            PageSize = 0,
            RepositoryHandledPagination = false,
            ExceededSafetyLimit = true,
            IsEstimate = estimate
        };
    }

    private static async Task<long> CountCore(QueryDefinition query, CountStrategy strategy, CancellationToken ct)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        query = query with { Filter = Filter.Snapshot(query.Filter), Sort = query.Sort.ToArray() };
        var repo = Repo;
        var q = RequireQuery(repo);
        var filterSupport = ResolveFilterSupport(repo);
        query = query.WithCountStrategy(strategy);
        var (adapterQuery, residual) = FilterPushdownCoordinator.Plan(query, filterSupport, typeof(TEntity));
        if (residual is null)
            return ValidateCountResult(
                await q.Count(adapterQuery, ct),
                strategy,
                DataCaps.Describe(repo, repo.GetType().Name)).Value;

        // Residual present → adapter count would be wrong; materialize the pushable set + finalize.
        var adapterResult = await DataQueryExecution<TEntity, TKey>.QueryCandidates(
            repo, q, adapterQuery.WithoutPagination(), ct);
        var unpaged = query.WithoutPagination();
        var finalized = FilterPushdownCoordinator.Finalize(unpaged, adapterQuery.WithoutPagination(), residual, adapterResult);
        return finalized.TotalCount;
    }

    private static CountResult ValidateCountResult(
        CountResult result,
        CountStrategy requested,
        CapabilitySet capabilities)
    {
        QueryReceiptRejectedException Reject(string correction)
            => new(typeof(TEntity).FullName ?? typeof(TEntity).Name, QueryReceiptAxis.Count, correction);

        if (result.Value < 0)
            throw Reject("A count result cannot be negative.");
        if (result.Execution == CountExecutionKind.None)
            throw Reject("The adapter returned a count value without reporting the count execution it performed.");
        if (result.Execution is CountExecutionKind.Exact or CountExecutionKind.Optimized && result.IsEstimate)
            throw Reject("An exact count execution cannot be marked as an estimate.");
        if (requested is CountStrategy.Exact or CountStrategy.Optimized &&
            result.Execution == CountExecutionKind.Fast)
            throw Reject("The requested exact total was answered by a fast/estimate execution.");
        if (result.Execution == CountExecutionKind.Fast && !capabilities.Has(DataCaps.Query.FastCount))
            throw Reject("The adapter reported fast-count execution without advertising that capability.");
        if (result.Execution == CountExecutionKind.Optimized && !capabilities.Has(DataCaps.Query.OptimizedCount))
            throw Reject("The adapter reported optimized-count execution without advertising that capability.");
        return result;
    }

    public static Task<TEntity?> Get(TKey id, CancellationToken ct = default) => Repo.Get(id, ct);
    public static Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default) => Repo.GetMany(ids, ct);

    // ------------------------------------------------------------------
    // All
    // ------------------------------------------------------------------
    public static Task<IReadOnlyList<TEntity>> All(CancellationToken ct = default)
        => All(QueryDefinition.All, ct);

    public static async Task<IReadOnlyList<TEntity>> All(QueryDefinition query, CancellationToken ct = default)
        => (await QueryWithCount(query, ct)).Items;

    public static Task<IReadOnlyList<TEntity>> All(Action<ISortBuilder<TEntity>> sort, CancellationToken ct = default)
        => All(QueryDefinition.All.WithSort<TEntity>(sort), ct);

    public static Task<QueryResult<TEntity>> AllWithCount(QueryDefinition? query = null, CancellationToken ct = default)
        => QueryWithCount(query ?? QueryDefinition.All, ct);

    // ------------------------------------------------------------------
    // Query — entity-first DX: LINQ predicate / DSL string / QueryDefinition
    // ------------------------------------------------------------------
    public static Task<QueryResult<TEntity>> QueryWithCount(Expression<Func<TEntity, bool>> predicate, QueryDefinition? query = null, CancellationToken ct = default, int? absoluteMaxRecords = null)
        => QueryWithCount((query ?? QueryDefinition.All).Where(Filter.And(query?.Filter, Lower(predicate))), ct, absoluteMaxRecords);

    public static Task<IReadOnlyList<TEntity>> Query(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        => Query(predicate, (QueryDefinition?)null, ct);

    public static async Task<IReadOnlyList<TEntity>> Query(Expression<Func<TEntity, bool>> predicate, QueryDefinition? query, CancellationToken ct = default)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return (await QueryWithCount(predicate, query, ct)).Items;
    }

    public static Task<IReadOnlyList<TEntity>> Query(Expression<Func<TEntity, bool>> predicate, Action<ISortBuilder<TEntity>> sort, CancellationToken ct = default)
        => Query(predicate, QueryDefinition.All.WithSort<TEntity>(sort), ct);

    /// <summary>
    /// Execute the JSON filter DSL (e.g. <c>{ "Tags": { "$in": ["x"] } }</c>). The string surface
    /// of <c>Query</c> is the provider-agnostic DSL — for provider-native queries use <see cref="QueryRaw"/>.
    /// </summary>
    public static Task<IReadOnlyList<TEntity>> Query(string filterJson, CancellationToken ct = default)
        => Query(filterJson, (QueryDefinition?)null, ct);

    public static Task<IReadOnlyList<TEntity>> Query(string filterJson, QueryDefinition? query, CancellationToken ct = default)
    {
        var filter = JsonFilterParser.Parse<TEntity>(filterJson);
        return All((query ?? QueryDefinition.All).Where(Filter.And(query?.Filter, filter)), ct);
    }

    public static Task<QueryResult<TEntity>> QueryWithCount(string filterJson, QueryDefinition? query = null, CancellationToken ct = default, int? absoluteMaxRecords = null)
    {
        var filter = JsonFilterParser.Parse<TEntity>(filterJson);
        return QueryWithCount((query ?? QueryDefinition.All).Where(Filter.And(query?.Filter, filter)), ct, absoluteMaxRecords);
    }

    public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, int? batchSize = null, CancellationToken ct = default)
        => QueryStreamCore(JsonFilterParser.Parse<TEntity>(filterJson), sortSpecs: null, batchSize, ct);

    /// <summary>Stream one structured query using provider-bounded candidate pages.</summary>
    public static IAsyncEnumerable<TEntity> QueryStream(QueryDefinition query, int? batchSize = null, CancellationToken ct = default)
        => StreamCore(query ?? throw new ArgumentNullException(nameof(query)), batchSize, ct);

    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, CancellationToken ct)
        => QueryStreamCore(JsonFilterParser.Parse<TEntity>(filterJson), sortSpecs: null, batchSize: null, ct);

    public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, string sort, int? batchSize = null, CancellationToken ct = default)
        => QueryStreamCore(JsonFilterParser.Parse<TEntity>(filterJson), SortSpecParser.ParseStrict<TEntity>(sort), batchSize, ct);

    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, string sort, CancellationToken ct)
        => QueryStreamCore(JsonFilterParser.Parse<TEntity>(filterJson), SortSpecParser.ParseStrict<TEntity>(sort), batchSize: null, ct);

    // ------------------------------------------------------------------
    // Raw provider query escape hatch
    // ------------------------------------------------------------------
    public static async Task<IReadOnlyList<TEntity>> QueryRaw(string providerQuery, object? parameters = null, QueryDefinition? shaping = null, CancellationToken ct = default)
    {
        var result = await RequireRaw(Repo).QueryRaw(providerQuery, parameters, shaping ?? QueryDefinition.All, ct);
        return result.Items;
    }

    // ------------------------------------------------------------------
    // Count
    // ------------------------------------------------------------------
    public static Task<long> Count(CancellationToken ct = default)
        => CountCore(QueryDefinition.All, CountStrategy.Exact, ct);

    public static Task<long> Count(CountStrategy strategy, CancellationToken ct = default)
        => CountCore(QueryDefinition.All, strategy, ct);

    public static Task<long> Count(Expression<Func<TEntity, bool>> predicate, CountStrategy strategy = CountStrategy.Optimized, CancellationToken ct = default)
        => CountCore(QueryDefinition.All.Where(Lower(predicate ?? throw new ArgumentNullException(nameof(predicate)))), strategy, ct);

    public static Task<long> Count(QueryDefinition query, CancellationToken ct = default)
        => CountCore(query, query.CountStrategy ?? CountStrategy.Exact, ct);

    public static Task<long> Count(Expression<Func<TEntity, bool>> predicate, string partition, CountStrategy strategy = CountStrategy.Optimized, CancellationToken ct = default)
    {
        using var _ = WithPartition(partition);
        return CountCore(QueryDefinition.All.Where(Lower(predicate ?? throw new ArgumentNullException(nameof(predicate)))), strategy, ct);
    }

    public static Task<long> Count(string partition, CountStrategy strategy = CountStrategy.Exact, CancellationToken ct = default)
    {
        using var _ = WithPartition(partition);
        return CountCore(QueryDefinition.All, strategy, ct);
    }

    // ------------------------------------------------------------------
    // Writes
    // ------------------------------------------------------------------
    /// <summary>Insert only if the identity is absent, returning the existing native mutation receipt.
    /// Null partition inherits; empty selects the default. No replacement or retry.</summary>
    public static async Task<MutationResult<TEntity, TKey>> Insert(TEntity model,
        string? partition = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ct.ThrowIfCancellationRequested();
        using var scope = WithPartition(partition);
        var insertion = As<IInsertOnlyRepository<TEntity, TKey>>()
            ?? throw new NotSupportedException($"The selected repository for {typeof(TEntity).Name} cannot guarantee atomic insertion. Select a supporting connector.");
        return await insertion.Insert(model, ct).ConfigureAwait(false);
    }

    /// <summary>Atomically replace an existing identity whose stored row matches the guard. No insertion or retry.</summary>
    public static Task<bool> ReplaceIf(TEntity model, Expression<Func<TEntity, bool>> guard,
        string? partition = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(guard);
        ct.ThrowIfCancellationRequested();
        // Lower synchronously at contribution time. The facade owns the immutable snapshot before its first await.
        var normalized = LinqFilterCompiler.Compile(guard);
        return Execute();

        async Task<bool> Execute()
        {
            using var scope = WithPartition(partition);
            var conditional = As<IConditionalWriteRepository<TEntity, TKey>>()
                ?? throw new NotSupportedException($"The selected repository for {typeof(TEntity).Name} cannot guarantee conditional replacement. Select a supporting connector and Entity root.");
            return await conditional.ConditionalReplaceAsync(model, normalized, ct).ConfigureAwait(false);
        }
    }

    public static Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        var context = EntityContext.Current;
        if (context?.TransactionCoordinator != null)
        {
            var repo = Repo;
            ((IDataOperationGate)repo).Demand(DataOperationEffect.Write, "deferred entity delete");
            context.TransactionCoordinator.TrackDelete<TEntity, TKey>(id, context);
            return Task.FromResult(true);
        }
        return Repo.Delete(id, ct);
    }
    public static Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default) => Repo.DeleteMany(ids, ct);
    public static Task<int> DeleteAll(CancellationToken ct = default) => Repo.DeleteAll(ct);

    public static Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
        => Repo.RemoveAll(strategy, ct);

    public static Task<long> RemoveAll(RemoveStrategy strategy, string partition, CancellationToken ct = default)
    { using var _ = WithPartition(partition); return Repo.RemoveAll(strategy, ct); }

    public static async Task<TEntity?> Patch(
        Koan.Data.Abstractions.Instructions.PatchPayload<TKey> payload,
        CancellationToken ct = default)
    {
        var repo = Repo;
        await using var horizon = await EnterMutationHorizon(repo, "entity patch", ct);
        var current = await repo.Get(payload.Id, ct);
        if (current is null) return null;
        Koan.Data.Core.Patch.PatchOpsExecutor.Apply<TEntity, TKey>(current, payload);
        return await repo.Upsert(current, ct);
    }

    public static async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        var context = EntityContext.Current;
        if (context?.TransactionCoordinator != null)
        {
            var repo = Repo;
            ((IDataOperationGate)repo).Demand(DataOperationEffect.Write, "deferred entity upsert");
            var manager = AppHost.GetRequiredService<IAggregateIdentityManager>(IdentityOperation);
            await manager.EnsureIdAsync<TEntity, TKey>(model, ct);
            context.TransactionCoordinator.TrackSave<TEntity, TKey>(model, context);
            return model;
        }
        return await Repo.Upsert(model, ct);
    }

    public static Task<MutationResult<TEntity, TKey>> UpsertWithOutcome(
        TEntity model,
        CancellationToken ct = default)
    {
        if (EntityContext.Current?.TransactionCoordinator is not null)
            throw new NotSupportedException(
                "A deferred coordination scope cannot return an upsert outcome before commit. Use Save, or perform SaveWithOutcome outside that scope.");
        var repo = Repo;
        return ((IDataMutationOutcomes<TEntity, TKey>)repo).UpsertWithOutcome(model, ct);
    }

    public static Task<MutationResult<TEntity, TKey>> DeleteWithOutcome(
        TKey id,
        CancellationToken ct = default)
    {
        if (EntityContext.Current?.TransactionCoordinator is not null)
            throw new NotSupportedException(
                "A deferred coordination scope cannot return a delete outcome before commit. Use Remove, or perform RemoveWithOutcome outside that scope.");
        var repo = Repo;
        return ((IDataMutationOutcomes<TEntity, TKey>)repo).DeleteWithOutcome(id, ct);
    }
    public static Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default) => Repo.UpsertMany(models, ct);
    public static IBatchSet<TEntity, TKey> Batch() => Repo.CreateBatch();

    // ------------------------------------------------------------------
    // Streaming (IAsyncEnumerable). Supported adapters enforce one bounded candidate page before
    // materialization; unsupported execution rejects rather than falling back to a complete result.
    // ------------------------------------------------------------------
    public static IAsyncEnumerable<TEntity> AllStream(int? batchSize = null, CancellationToken ct = default)
        => AllStreamCore(sortSpecs: null, batchSize, ct);

    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<TEntity> AllStream(CancellationToken ct)
        => AllStreamCore(sortSpecs: null, batchSize: null, ct);

    public static IAsyncEnumerable<TEntity> AllStream(string sort, int? batchSize = null, CancellationToken ct = default)
        => AllStreamCore(SortSpecParser.ParseStrict<TEntity>(sort), batchSize, ct);

    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<TEntity> AllStream(string sort, CancellationToken ct)
        => AllStreamCore(SortSpecParser.ParseStrict<TEntity>(sort), batchSize: null, ct);

    public static IAsyncEnumerable<TEntity> AllStream(Action<ISortBuilder<TEntity>> sort, int? batchSize = null, CancellationToken ct = default)
        => AllStreamCore(SortBuilder<TEntity>.Build(sort), batchSize, ct);

    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<TEntity> AllStream(Action<ISortBuilder<TEntity>> sort, CancellationToken ct)
        => AllStreamCore(SortBuilder<TEntity>.Build(sort), batchSize: null, ct);

    private static IAsyncEnumerable<TEntity> AllStreamCore(IReadOnlyList<SortSpec>? sortSpecs, int? batchSize, CancellationToken ct)
    {
        var query = sortSpecs is { Count: > 0 } ? QueryDefinition.All.WithSort(sortSpecs) : QueryDefinition.All;
        return StreamCore(query, batchSize, ct);
    }

    public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, int? batchSize = null, CancellationToken ct = default)
        => QueryStreamCore(Lower(predicate), sortSpecs: null, batchSize, ct);

    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, CancellationToken ct)
        => QueryStreamCore(Lower(predicate), sortSpecs: null, batchSize: null, ct);

    public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, string sort, int? batchSize = null, CancellationToken ct = default)
        => QueryStreamCore(Lower(predicate), SortSpecParser.ParseStrict<TEntity>(sort), batchSize, ct);

    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, string sort, CancellationToken ct)
        => QueryStreamCore(Lower(predicate), SortSpecParser.ParseStrict<TEntity>(sort), batchSize: null, ct);

    private static IAsyncEnumerable<TEntity> QueryStreamCore(Filter filter, IReadOnlyList<SortSpec>? sortSpecs, int? batchSize, CancellationToken ct)
    {
        var query = QueryDefinition.All.Where(filter);
        if (sortSpecs is { Count: > 0 }) query = query.WithSort(sortSpecs);
        return StreamCore(query, batchSize, ct);
    }

    /// <summary>
    /// Read every matching Entity for a <b>bulk</b> operation — a transfer, an export — using the strongest
    /// strategy the routed provider supports (DATA-0113).
    ///
    /// <para>A provider that advertises <c>ProviderBoundedPaging</c> is streamed, so a bulk operation over a
    /// large table stays provider-bounded exactly as DATA-0107 requires. A provider that does not is read with
    /// one explicitly materialized query — the alternative being that the operation simply does not work on
    /// the Data pillar's own floor adapter, which is not a boundary worth defending. The three adapters this
    /// reaches keep their whole set resident or local, so the materialized read costs what every other read
    /// on them already costs.</para>
    ///
    /// <para>The choice is never silent: it records a <c>koan.data.stream.execution</c> fact either way, and
    /// <paramref name="onMaterialized"/> lets a caller add its own user-facing notice. This lives here, not in
    /// each consumer, so the next bulk consumer inherits the decision instead of re-deriving it.</para>
    /// </summary>
    /// <param name="predicate">Optional filter; <see langword="null"/> reads everything.</param>
    /// <param name="batchSize">Bound for the streamed path's candidate page.</param>
    /// <param name="onMaterialized">Invoked once, before the first item, only when the read is materialized.</param>
    /// <param name="ct">Cancellation observed between items on both paths.</param>
    internal static async IAsyncEnumerable<TEntity> BulkRead(
        Expression<Func<TEntity, bool>>? predicate,
        int? batchSize,
        Action? onMaterialized,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (bounded, provider, facts) = ResolveBulkStrategy();
        if (bounded)
        {
            var stream = predicate is null ? AllStream(batchSize, ct) : QueryStream(predicate, batchSize, ct);
            await foreach (var entity in stream.WithCancellation(ct).ConfigureAwait(false))
                yield return entity;
            yield break;
        }

        onMaterialized?.Invoke();
        QueryStreamCoordinator.RecordMaterializedBulkRead<TEntity>(facts, provider);

        var materialized = predicate is null
            ? await All(ct).ConfigureAwait(false)
            : await Query(predicate, ct).ConfigureAwait(false);
        foreach (var entity in materialized)
        {
            ct.ThrowIfCancellationRequested();
            yield return entity;
        }
    }

    /// <summary>
    /// Resolve, once, whether the Entity's routed provider can stream, and the provider/fact channel used to
    /// report the choice. Asked before any read: catching <c>QueryStreamRejectedException</c> instead would
    /// also swallow unsupported-sort and offset-overflow rejections, which are real errors.
    /// </summary>
    private static (bool Bounded, string Provider, IKoanRuntimeFactRecorder? Facts) ResolveBulkStrategy()
    {
        var dataService = Service; // Preserve the standard missing/disposed-host failure contract.
        var services = AppHost.Current!;
        var carrierRegistry = services.GetService(typeof(KoanContextCarrierRegistry)) as KoanContextCarrierRegistry;
        var facts = services.GetService(typeof(IKoanRuntimeFactRecorder)) as IKoanRuntimeFactRecorder;

        // Resolve under the caller's ambient context: routing is context-sensitive, so the capability answer
        // must come from the repository the read would actually use.
        using (EnterCapturedContext(EntityContext.Current, carrierRegistry, carrierRegistry?.Capture()))
        {
            var repo = dataService.GetRepository<TEntity, TKey>();
            var sourceRegistry = (DataSourceRegistry?)services.GetService(typeof(DataSourceRegistry))
                ?? throw new InvalidOperationException("Data source registry is unavailable. Ensure AddKoanDataCore() ran during startup.");
            var (provider, _) = AdapterResolver.ResolveForEntity<TEntity>(services, sourceRegistry);
            return (DataCaps.Describe(repo, provider).Has(DataCaps.Query.ProviderBoundedPaging), provider, facts);
        }
    }

    private static async IAsyncEnumerable<TEntity> StreamCore(
        QueryDefinition query,
        int? batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        query = query with { Filter = Filter.Snapshot(query.Filter), Sort = query.Sort.ToArray() };
        var dataService = Service; // Preserve the standard missing/disposed-host failure contract.
        var services = AppHost.Current!;
        var capturedDataContext = EntityContext.Current;
        var carrierRegistry = services.GetService(typeof(KoanContextCarrierRegistry)) as KoanContextCarrierRegistry;
        var capturedCarriers = carrierRegistry?.Capture();

        IDataRepository<TEntity, TKey> repo;
        string provider;
        using (EnterCapturedContext(capturedDataContext, carrierRegistry, capturedCarriers))
        {
            repo = dataService.GetRepository<TEntity, TKey>();
            var sourceRegistry = (DataSourceRegistry?)services.GetService(typeof(DataSourceRegistry))
                ?? throw new InvalidOperationException("Data source registry is unavailable. Ensure AddKoanDataCore() ran during startup.");
            (provider, _) = AdapterResolver.ResolveForEntity<TEntity>(services, sourceRegistry);
        }
        var facts = services.GetService(typeof(IKoanRuntimeFactRecorder)) as IKoanRuntimeFactRecorder;

        await foreach (var item in QueryStreamCoordinator.Execute<TEntity, TKey>(
                           repo,
                           query,
                           provider,
                           batchSize,
                           facts,
                           () => EnterCapturedContext(capturedDataContext, carrierRegistry, capturedCarriers),
                           ct).ConfigureAwait(false))
            yield return item;
    }

    private static IDisposable EnterCapturedContext(
        EntityContext.ContextState? dataContext,
        KoanContextCarrierRegistry? carrierRegistry,
        IReadOnlyDictionary<string, string>? carriers)
    {
        var carrierScope = carrierRegistry?.Restore(carriers, ContextIngressTrust.HostTrusted)
                           ?? NoOpDisposable.Instance;
        try
        {
            var dataScope = dataContext is null
                ? KoanContext.Suppress<EntityContext.ContextState>()
                : KoanContext.Push(dataContext);
            return new CapturedContextScope(dataScope, carrierScope);
        }
        catch
        {
            carrierScope.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Materialized paging helpers
    // ------------------------------------------------------------------
    public static Task<IReadOnlyList<TEntity>> FirstPage(int size, CancellationToken ct = default)
        => PageCore(1, size, QueryDefinition.All, ct);

    public static Task<IReadOnlyList<TEntity>> FirstPage(int size, string sort, CancellationToken ct = default)
        => PageCore(1, size, QueryDefinition.All.WithSort<TEntity>(sort), ct);

    public static Task<IReadOnlyList<TEntity>> FirstPage(int size, Action<ISortBuilder<TEntity>> sort, CancellationToken ct = default)
        => PageCore(1, size, QueryDefinition.All.WithSort<TEntity>(sort), ct);

    public static Task<IReadOnlyList<TEntity>> Page(int page, int size, CancellationToken ct = default)
        => PageCore(page, size, QueryDefinition.All, ct);

    public static Task<IReadOnlyList<TEntity>> Page(int page, int size, string sort, CancellationToken ct = default)
        => PageCore(page, size, QueryDefinition.All.WithSort<TEntity>(sort), ct);

    public static Task<IReadOnlyList<TEntity>> Page(int page, int size, Action<ISortBuilder<TEntity>> sort, CancellationToken ct = default)
        => PageCore(page, size, QueryDefinition.All.WithSort<TEntity>(sort), ct);

    private static async Task<IReadOnlyList<TEntity>> PageCore(int page, int size, QueryDefinition query, CancellationToken ct)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        if (page <= 0) throw new System.ArgumentOutOfRangeException(nameof(page));
        if (size <= 0) throw new System.ArgumentOutOfRangeException(nameof(size));
        var requested = query.WithPagination(page, size).WithCountStrategy(null);
        var repo = Repo;
        var q = RequireQuery(repo);
        var filterSupport = ResolveFilterSupport(repo);
        var (adapterQuery, residual) = FilterPushdownCoordinator.Plan(requested, filterSupport, typeof(TEntity));
        var adapterResult = await DataQueryExecution<TEntity, TKey>.QueryCandidates(repo, q, adapterQuery, ct);
        var pageResult = FilterPushdownCoordinator.Finalize(requested, adapterQuery, residual, adapterResult).Page;
        await DataQueryExecution<TEntity, TKey>.MaterializeVisible(repo, pageResult, ct);
        DataQueryExecution<TEntity, TKey>.ValidateEvidence(adapterResult.ReadEvidence, pageResult);
        return pageResult;
    }

    // ------------------------------------------------------------------
    // Partition-scoped helpers (ambient via EntityContext)
    // ------------------------------------------------------------------
    public static IDisposable WithPartition(string? partition) =>
        partition is null ? NoOpDisposable.Instance : EntityContext.Partition(partition);

    public static Task<TEntity?> Get(TKey id, string partition, CancellationToken ct = default)
    { using var _ = WithPartition(partition); return Repo.Get(id, ct); }

    public static Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, string partition, CancellationToken ct = default)
    { using var _ = WithPartition(partition); return Repo.GetMany(ids, ct); }

    public static async Task<IReadOnlyList<TEntity>> All(string partition, CancellationToken ct = default)
    {
        using var _ = WithPartition(partition);
        return (await QueryWithCount(QueryDefinition.All, ct)).Items;
    }

    public static async Task<IReadOnlyList<TEntity>> Query(Expression<Func<TEntity, bool>> predicate, string partition, CancellationToken ct = default)
    {
        using var _ = WithPartition(partition);
        return (await QueryWithCount(predicate, QueryDefinition.All, ct)).Items;
    }

    public static Task<TEntity> Upsert(TEntity model, string partition, CancellationToken ct = default)
    {
        var context = EntityContext.Current;
        if (context?.TransactionCoordinator != null)
        {
            var repo = Repo;
            ((IDataOperationGate)repo).Demand(DataOperationEffect.Write, "deferred partition entity upsert");
            context.TransactionCoordinator.TrackSave<TEntity, TKey>(model, context with { Partition = partition });
            return Task.FromResult(model);
        }
        using var _ = WithPartition(partition);
        return Repo.Upsert(model, ct);
    }

    public static Task<bool> Delete(TKey id, string partition, CancellationToken ct = default)
    {
        var context = EntityContext.Current;
        if (context?.TransactionCoordinator != null)
        {
            var repo = Repo;
            ((IDataOperationGate)repo).Demand(DataOperationEffect.Write, "deferred partition entity delete");
            context.TransactionCoordinator.TrackDelete<TEntity, TKey>(id, context with { Partition = partition });
            return Task.FromResult(true);
        }
        using var _ = WithPartition(partition);
        return Repo.Delete(id, ct);
    }

    public static Task<int> UpsertMany(IEnumerable<TEntity> models, string partition, CancellationToken ct = default)
    { using var _ = WithPartition(partition); return Repo.UpsertMany(models, ct); }

    public static Task<int> DeleteMany(IEnumerable<TKey> ids, string partition, CancellationToken ct = default)
    { using var _ = WithPartition(partition); return Repo.DeleteMany(ids, ct); }

    public static Task<int> DeleteAll(string partition, CancellationToken ct = default)
    { using var _ = WithPartition(partition); return Repo.DeleteAll(ct); }

    public static async Task<int> Delete(Expression<Func<TEntity, bool>> predicate, string partition, CancellationToken ct = default)
    {
        using var _ = WithPartition(partition);
        var repo = Repo;
        await using var horizon = await EnterMutationHorizon(repo, "predicate entity delete", ct);
        var items = (await QueryWithCount(predicate, QueryDefinition.All, ct)).Items;
        var ids = items.Select(e => e.Id);
        return await repo.DeleteMany(ids, ct);
    }

    // ------------------------------------------------------------------
    // Instruction / raw SQL execution sugar
    // ------------------------------------------------------------------
    public static Task<TResult> Execute<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        return DataServiceExecuteExtensions.Execute<TEntity, TResult>(Service, instruction, ct);
    }

    public static Task<TResult> Execute<TResult>(Instruction instruction, IDataService data, CancellationToken ct = default)
        => DataServiceExecuteExtensions.Execute<TEntity, TResult>(data, instruction, ct);

    public static Task<int> Execute(string sql, CancellationToken ct = default)
    {
        return DataServiceExecuteExtensions.Execute<TEntity, int>(Service, InstructionSql.NonQuery(sql), ct);
    }

    public static Task<int> Execute(string sql, IDataService data, object? parameters = null, CancellationToken ct = default)
        => DataServiceExecuteExtensions.Execute<TEntity, int>(data, InstructionSql.NonQuery(sql, parameters), ct);

    public static Task<int> Execute(
        string sql,
        DataOperationEffect effect,
        object? parameters = null,
        CancellationToken ct = default)
        => DataServiceExecuteExtensions.Execute<TEntity, int>(
            Service,
            InstructionSql.NonQuery(sql, effect, parameters),
            ct);

    public static Task<int> Execute(
        string sql,
        DataOperationEffect effect,
        IDataService data,
        object? parameters = null,
        CancellationToken ct = default)
        => DataServiceExecuteExtensions.Execute<TEntity, int>(
            data,
            InstructionSql.NonQuery(sql, effect, parameters),
            ct);


    // ------------------------------------------------------------------
    // Bounded cross-context movement
    // ------------------------------------------------------------------
    public static CopyTransferBuilder<TEntity, TKey> Copy() => new(null);
    public static CopyTransferBuilder<TEntity, TKey> Copy(Expression<Func<TEntity, bool>> predicate)
        => new(predicate ?? throw new ArgumentNullException(nameof(predicate)));
    public static MoveTransferBuilder<TEntity, TKey> Move() => new(null);
    public static MoveTransferBuilder<TEntity, TKey> Move(Expression<Func<TEntity, bool>> predicate)
        => new(predicate ?? throw new ArgumentNullException(nameof(predicate)));
    public static MirrorTransferBuilder<TEntity, TKey> Mirror(MirrorMode mode = MirrorMode.Push)
        => new(mode, null);
    public static MirrorTransferBuilder<TEntity, TKey> Mirror(
        Expression<Func<TEntity, bool>> predicate,
        MirrorMode mode = MirrorMode.Push)
        => new(mode, predicate ?? throw new ArgumentNullException(nameof(predicate)));

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        private NoOpDisposable() { }
        public void Dispose() { }
    }

    private sealed class CapturedContextScope(IDisposable dataScope, IDisposable carrierScope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { dataScope.Dispose(); }
            finally { carrierScope.Dispose(); }
        }
    }
}
