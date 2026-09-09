using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Core.Sorting;

namespace Koan.Data.Core.KeyValue;

/// <summary>
/// The key-value / document storage-model family base (ARCH-0103 §5, §9.2). It realizes the three AODB modes once for
/// the whole family — Shared (managed-field write-stamp + cross-scope write guard + managed-aware read filter), Container
/// (a distinct physical store per ambient particle), Database (a distinct physical store per routed source) — and owns
/// the entire <see cref="IDataRepository{TEntity,TKey}"/> / <see cref="IQueryRepository{TEntity,TKey}"/> /
/// <see cref="IBatchSet{TEntity,TKey}"/> / <see cref="IInstructionExecutor{TEntity}"/> surface. A concrete adapter
/// supplies only the thin backend primitives below (get / scan / write / remove / clear over the current physical
/// store) plus any extra capabilities it announces.
///
/// <para><b>Off ⇒ byte-identical:</b> with no managed field registered (<c>ManagedFieldRegistry.IsEmpty</c>) the write
/// stamps nothing, the guard never runs, and <see cref="KvFilterEvaluator"/> routes the whole filter to
/// <see cref="InMemoryFilterEvaluator"/> over the entity — the exact pre-rebuild behaviour. Declaring
/// <see cref="DataCaps.Isolation.RowScoped"/> is what lets the facade's read scope reach the adapter at all; it does not
/// change the off path.</para>
/// </summary>
public abstract class KeyValueStore<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IBoundedQueryRepository<TEntity, TKey>,
    IDescribesCapabilities,
    IInstructionExecutor<TEntity>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    // ==================== Backend primitives (the thin per-adapter seam) ====================
    // Each resolves the CURRENT physical store from the ambient context (the routed source captured at Create + the
    // EntityContext partition), exactly as the pre-rebuild adapters resolved their per-(partition) store.

    /// <summary>Read one record from the current store, or <c>null</c> when absent.</summary>
    protected abstract Task<KvRecord<TEntity>?> ReadAsync(TKey id, CancellationToken ct);

    /// <summary>Every record in the current store (the full-keyspace scan the family's filter contract is built on).</summary>
    protected abstract Task<IReadOnlyList<KvRecord<TEntity>>> ScanAsync(CancellationToken ct);

    /// <summary>
    /// Read at most <paramref name="maxCandidates"/> records from the physical store. Implementations
    /// must stop enumeration at the bound; delegating to <see cref="ScanAsync"/> is not conformant.
    /// </summary>
    protected abstract Task<IReadOnlyList<KvRecord<TEntity>>> ScanBoundedAsync(
        int maxCandidates,
        CancellationToken ct);

    /// <summary>Write (insert or replace by id) a record into the current store.</summary>
    protected abstract Task WriteAsync(TKey id, KvRecord<TEntity> record, CancellationToken ct);

    /// <summary>Remove one record from the current store; <c>true</c> when a record was removed.</summary>
    protected abstract Task<bool> RemoveAsync(TKey id, CancellationToken ct);

    /// <summary>Clear the current store, returning the number of records removed.</summary>
    protected abstract Task<int> ClearAsync(CancellationToken ct);

    /// <summary>Announce the backend-specific extra capabilities (bulk / atomic / fast-remove / TTL …). The family
    /// caps (LINQ, Full filter, RowScoped) are added by <see cref="Describe"/>.</summary>
    protected abstract void DescribeBackend(ICapabilities caps);

    // ==================== Capability declaration ====================

    public void Describe(ICapabilities caps)
    {
        caps.Add(DataCaps.Query.Linq)
            .Add(DataCaps.Query.Filter, FilterSupport.Full)
            // The AODB three-mode ledger (ARCH-0103 §6) — the family realizes all three uniformly: Shared persists the
            // framework-managed discriminator (sidecar / injected JSON) + pushes a scalar equality (the hybrid evaluator)
            // + guards a cross-scope write; Container = a distinct store/keyspace/file per ambient partition; Database =
            // a per-source store/connection/index. Co-defined with the AodbConformanceSpecsBase cells that prove each.
            .Add(DataCaps.Isolation.RowScoped)
            .Add(DataCaps.Isolation.ContainerScoped)
            .Add(DataCaps.Isolation.DatabaseScoped);
        DescribeBackend(caps);
    }

    /// <summary>The declared capability set, built once from <see cref="Describe"/> — the single source of truth the
    /// batch path negotiates against (e.g. an atomic-batch request on a backend that did not announce
    /// <see cref="DataCaps.Write.AtomicBatch"/> fails loud, rather than silently running non-atomically).</summary>
    private CapabilitySet? _capabilities;
    private CapabilitySet Capabilities => _capabilities ??= CapabilitySet.Build(GetType().Name, Describe);

    // ==================== Read ====================

    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // The facade lowers a SCOPED key-read to a managed Query (IDOR); raw Get(id) is reached only when unscoped.
        var rec = await ReadAsync(id, ct).ConfigureAwait(false);
        return rec is { } r ? r.Entity : null;
    }

    public async Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        var idList = ids as IReadOnlyList<TKey> ?? ids.ToList();
        var results = new TEntity?[idList.Count];
        for (var i = 0; i < idList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rec = await ReadAsync(idList[i], ct).ConfigureAwait(false);
            results[i] = rec is { } r ? r.Entity : null;
        }
        return results;
    }

    public async Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var records = await ScanAsync(ct).ConfigureAwait(false);

        return ExecuteQuery(records, query);
    }

    public async Task<BoundedQueryResult<TEntity>> QueryBoundedCandidates(
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct = default)
    {
        if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        ct.ThrowIfCancellationRequested();

        var records = await ScanBoundedAsync(checked(maxCandidates + 1), ct).ConfigureAwait(false);
        if (records.Count > maxCandidates)
            return new BoundedQueryResult<TEntity>([], records.Count, CandidateLimitExceeded: true);

        var result = ExecuteQuery(records, query);
        return new BoundedQueryResult<TEntity>(result.Items, records.Count, CandidateLimitExceeded: false);
    }

    private static RepositoryQueryResult<TEntity> ExecuteQuery(
        IReadOnlyList<KvRecord<TEntity>> records,
        QueryDefinition query)
    {

        IEnumerable<TEntity> items = query.Filter is null
            ? records.Select(r => r.Entity)
            : records.Where(KvFilterEvaluator.Compile<TEntity>(query.Filter)).Select(r => r.Entity);

        // Materialize once for stable filtering and ordering. Expose a total only when the caller
        // requested one; ordinary pages and streams must not acquire count semantics implicitly.
        var filtered = items as IReadOnlyList<TEntity> ?? items.ToList();
        long? totalCount = query.CountStrategy is null ? null : filtered.Count;

        var sortHandled = RepositoryQueryResult<TEntity>.NoSortHandled;
        IEnumerable<TEntity> ordered;
        if (query.HasSort)
        {
            ordered = InMemorySorter.Apply(filtered, query.Sort);
            sortHandled = query.Sort.ToFrozenSet();
        }
        else
        {
            // Match the in-memory reference / relational adapters: an unsorted query falls back to a stable Id order so
            // results — and any pagination over them — are deterministic. With GUID v7 ids this is also insertion order.
            ordered = filtered.OrderBy(static e => e.Id, Comparer<TKey>.Default);
        }

        var paginationHandled = false;
        if (query.HasPagination)
        {
            var skip = query.EffectiveOffset();
            ordered = ordered.Skip(skip).Take(query.EffectivePageSize());
            paginationHandled = true;
        }

        var list = ordered as IReadOnlyList<TEntity> ?? ordered.ToList();
        return new RepositoryQueryResult<TEntity>
        {
            Items = list,
            FilterHandled = query.Filter is not null,
            TotalCount = totalCount,
            IsEstimate = false,
            CountExecution = totalCount is null ? CountExecutionKind.None : CountExecutionKind.Exact,
            SortHandled = sortHandled,
            PaginationHandled = paginationHandled,
            // Every record was loaded to answer this: the axes above say who applied each part, this says
            // what it cost.
            MaterializedAllCandidates = true,
        };
    }

    public async Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var records = await ScanAsync(ct).ConfigureAwait(false);
        if (query.Filter is null) return new CountResult(records.Count, false);
        var count = records.LongCount(KvFilterEvaluator.Compile<TEntity>(query.Filter));
        return new CountResult(count, false);
    }

    // ==================== Write ====================

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var record = await GuardAndSnapshotAsync(model, ct).ConfigureAwait(false);
        await WriteAsync(model.Id, record, ct).ConfigureAwait(false);
        return model;
    }

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        var list = models as IReadOnlyList<TEntity> ?? models.ToList();
        if (list.Count == 0) return 0;

        // Guard + stamp every row first (the per-row Shared-mode contract), THEN hand the whole set to WriteManyAsync.
        // The default WriteManyAsync loops WriteAsync, but a backend with a native bulk write (Json: one file persist;
        // Redis: MSET) overrides it — so bulk upsert stays one physical write, not N (the family's perf floor).
        var records = new List<KvRecord<TEntity>>(list.Count);
        foreach (var m in list)
        {
            ct.ThrowIfCancellationRequested();
            records.Add(await GuardAndSnapshotAsync(m, ct).ConfigureAwait(false));
        }
        await WriteManyAsync(records, ct).ConfigureAwait(false);
        return records.Count;
    }

    public Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return RemoveAsync(id, ct);
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        var list = ids as IReadOnlyList<TKey> ?? ids.ToList();
        ct.ThrowIfCancellationRequested();
        return await RemoveManyAsync(list, ct).ConfigureAwait(false);
    }

    /// <summary>The cross-scope write guard + managed-value stamp for one row, shared by <see cref="Upsert"/> and
    /// <see cref="UpsertMany"/>. Guards only the GUARDED isolation values (<c>ManagedFieldWriteScope.Current</c>), never
    /// the unguarded operation overrides — the KV analogue of the relational <c>ON CONFLICT … WHERE json_extract(...)</c>.</summary>
    protected async Task<KvRecord<TEntity>> GuardAndSnapshotAsync(TEntity model, CancellationToken ct)
    {
        var guarded = ManagedFieldWriteScope.Current;
        if (guarded is { Count: > 0 })
        {
            var existing = await ReadAsync(model.Id, ct).ConfigureAwait(false);
            if (existing is { } ex)
            {
                foreach (var kv in guarded)
                {
                    var have = ex.Managed is { } m && m.TryGetValue(kv.Key, out var v) ? v : null;
                    if (!ManagedValueEquals(have, kv.Value)) throw CrossScopeWrite(model.Id);
                }
            }
        }
        return new KvRecord<TEntity>(model, SnapshotManaged());
    }

    /// <summary>Persist a batch of already-guarded, already-stamped records. The default loops <see cref="WriteAsync"/>
    /// (correct for every backend); a backend with a native bulk write overrides it to collapse N physical writes into
    /// one (Json persists its file once; Redis uses MSET) so bulk upsert is not O(N) round-trips / O(N²) file rewrites.</summary>
    protected virtual async Task WriteManyAsync(IReadOnlyList<KvRecord<TEntity>> records, CancellationToken ct)
    {
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            await WriteAsync(r.Entity.Id, r, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Remove a batch of ids, returning the number removed. The default loops <see cref="RemoveAsync"/>; a
    /// backend with a native bulk delete overrides it (Json persists once; Redis issues one DEL).</summary>
    protected virtual async Task<int> RemoveManyAsync(IReadOnlyList<TKey> ids, CancellationToken ct)
    {
        var count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (await RemoveAsync(id, ct).ConfigureAwait(false)) count++;
        }
        return count;
    }

    public Task<int> DeleteAll(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // The facade scopes RemoveAll (it loads the visible rows then deletes them by id), so a raw clear of the current
        // store is reached only when unscoped — clearing exactly the current particle/source set.
        return ClearAsync(ct);
    }

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await ClearAsync(ct).ConfigureAwait(false);
    }

    // ==================== Batch ====================

    public IBatchSet<TEntity, TKey> CreateBatch() => new KvBatch(this);

    private sealed class KvBatch : IBatchSet<TEntity, TKey>
    {
        private readonly KeyValueStore<TEntity, TKey> _repo;
        private readonly List<TEntity> _adds = new();
        private readonly List<TEntity> _updates = new();
        private readonly List<TKey> _deletes = new();
        private readonly List<(TKey id, Action<TEntity> mutate)> _mutations = new();

        public KvBatch(KeyValueStore<TEntity, TKey> repo) => _repo = repo;

        public IBatchSet<TEntity, TKey> Add(TEntity entity) { _adds.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TEntity entity) { _updates.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Delete(TKey id) { _deletes.Add(id); return this; }
        public IBatchSet<TEntity, TKey> Update(TKey id, Action<TEntity> mutate) { _mutations.Add((id, mutate)); return this; }
        public IBatchSet<TEntity, TKey> Clear() { _adds.Clear(); _updates.Clear(); _deletes.Clear(); _mutations.Clear(); return this; }

        public async Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Honor the atomic-batch contract from the declared capability: a backend that does not announce
            // AtomicBatch (e.g. the JSON file floor) cannot guarantee an all-or-nothing batch, so a RequireAtomic
            // request fails loud rather than running the writes one-by-one as if atomic.
            if (options?.RequireAtomic == true && !_repo.Capabilities.Has(DataCaps.Write.AtomicBatch))
                throw new NotSupportedException(
                    $"The {_repo.GetType().Name} adapter does not support atomic batch transactions for " +
                    $"{typeof(TEntity).Name} (it did not announce DataCaps.Write.AtomicBatch).");

            foreach (var (id, mutate) in _mutations)
            {
                ct.ThrowIfCancellationRequested();
                var rec = await _repo.ReadAsync(id, ct).ConfigureAwait(false);
                if (rec is { } r) { mutate(r.Entity); _updates.Add(r.Entity); }
            }

            // Route through the bulk paths (UpsertMany / DeleteMany) so each backend collapses the batch into its native
            // bulk write — the same per-row guard + stamp runs, but a JSON file is persisted once, not per row.
            var add = _adds.Count > 0 ? await _repo.UpsertMany(_adds, ct).ConfigureAwait(false) : 0;
            var upd = _updates.Count > 0 ? await _repo.UpsertMany(_updates, ct).ConfigureAwait(false) : 0;
            var del = _deletes.Count > 0 ? await _repo.DeleteMany(_deletes, ct).ConfigureAwait(false) : 0;

            return new BatchResult(add, upd, del);
        }
    }

    // ==================== Instructions ====================

    public virtual async Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ct.ThrowIfCancellationRequested();
        switch (instruction.Name)
        {
            case DataInstructions.EnsureCreated:
                return (TResult)(object)true;
            case DataInstructions.Clear:
                return (TResult)(object)(await ClearAsync(ct).ConfigureAwait(false));
            default:
                throw new NotSupportedException(
                    $"Instruction '{instruction.Name}' is not supported by the {GetType().Name} key-value adapter for {typeof(TEntity).Name}.");
        }
    }

    // ==================== Managed-field helpers ====================

    /// <summary>Snapshot the values to stamp onto this write — the guarded isolation values merged with the unguarded
    /// operation overrides (<c>ManagedFieldWriteScope.Effective</c>). <c>null</c> off any scope (the byte-identical path).</summary>
    protected static IReadOnlyDictionary<string, object?>? SnapshotManaged()
    {
        var eff = ManagedFieldWriteScope.Effective;
        if (eff is null || eff.Count == 0) return null;
        return new Dictionary<string, object?>(eff, StringComparer.Ordinal);
    }

    private static bool ManagedValueEquals(object? a, object? b)
        => a is null ? b is null : a.Equals(b);

    private static InvalidOperationException CrossScopeWrite(TKey id)
        => new($"Rejected a cross-scope write to '{typeof(TEntity).Name}' id '{id}': the row is owned by a different " +
               "managed scope (e.g. tenant/classification). A managed-field-scoped entity cannot overwrite another scope's row.");
}
