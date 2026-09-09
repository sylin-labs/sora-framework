using System.Linq.Expressions;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core.Axes;
using Koan.Data.Core.Execution;
using Koan.Data.Core.Metadata;
using Koan.Data.Core.Pipeline;
using Koan.Data.Core.Lifecycle;
using Koan.Data.Core.Polymorphism;
using Koan.Data.Core.Querying;
using Koan.Data.Core.Routing;
using Koan.Data.Core.Semantics;
using Koan.Data.Core.Sorting;

namespace Koan.Data.Core;

/// <summary>
/// Adds cross-cutting behaviors on top of an underlying repository:
/// - Ensures identifiers for all upserts (single, many, batch)
/// - Auto-updates [Timestamp] fields on save operations
/// - Owns host-composed Entity Lifecycle around materialization, upsert, and removal
/// - Advertises query/write capabilities
/// - Bridges the structured query (<see cref="IQueryRepository{TEntity,TKey}"/>) and raw query
///   (<see cref="IRawQueryRepository{TEntity,TKey}"/>) surfaces of the inner adapter
/// - Forwards instruction execution when supported by the adapter
/// - Honours <b>managed fields</b> (DATA-0105 §3b) — the invisible framework-managed isolation discriminators a
///   cross-cutting module registers (e.g. Koan.Tenancy). The facade is the gateway for the repository path: it
///   stamps the managed value on writes (the inner adapter persists + verifies it), AND-folds a managed predicate
///   into reads, lowers key-ops to managed-scoped queries (IDOR), scopes RemoveAll/DeleteAll, and fails closed on
///   the paths the managed predicate cannot cover (raw / conditional-replace / a non-isolating adapter).
/// Provider/module decorators sit inside this facade. This is the one application-facing Data boundary,
/// so an inner cache hit or specialized provider path cannot bypass these semantics.
/// </summary>
internal sealed partial class RepositoryFacade<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IBoundedQueryRepository<TEntity, TKey>,
    IRawQueryRepository<TEntity, TKey>,
    IDescribesCapabilities,
    IConditionalWriteRepository<TEntity, TKey>,
    IInstructionExecutor<TEntity>,
    IAxisScopeDiagnostics,
    IDataOperationGate,
    IDataRouteBoundRepository,
    IDataMutationOutcomes<TEntity, TKey>,
    IInsertOnlyRepository<TEntity, TKey>,
    IDataQueryBoundary<TEntity, TKey>,
    Koan.Data.Abstractions.Analytics.IAnalyticsQueryComposer<TEntity>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IDataRepository<TEntity, TKey> _inner;
    private readonly StorageWritePlan _writePlan;
    private readonly StorageFieldTransformPlan _fieldTransforms;
    private readonly StorageFieldTransformPlan.Compiled _fieldTransform;
    private readonly bool _isEntityFamily;
    private readonly IStorageGuard[] _guards;
    private readonly IReadFilterContributor[] _readContributors;
    private readonly IReadOnlyList<ManagedFieldDescriptor> _managed;
    private readonly string _idField;
    private readonly bool _scopeAdapterOk;
    private readonly string? _scopeAdapterError;
    private readonly FilterSupport _filterCaps;
    private readonly bool _skipReadPushabilityCheck;
    private readonly OperationOverrideDescriptor? _deleteOverride;
    private readonly EntityLifecyclePlan<TEntity, TKey>? _lifecycle;
    private readonly DataSegmentationPlan.DataSegmentationScope _segmentation;
    private readonly DataSourcePlan _sourcePlan;
    private readonly DataOperationHorizon? _operationHorizon;
    private readonly DataRouteBinding? _routeBinding;
    private readonly Func<DataRouteBinding?>? _resolveRoute;

    public RepositoryFacade(
        IDataRepository<TEntity, TKey> inner,
        IStorageGuard[]? guards = null,
        IReadFilterContributor[]? readContributors = null,
        EntityLifecyclePlan<TEntity, TKey>? lifecycle = null,
        DataSegmentationPlan.DataSegmentationScope? segmentation = null,
        StorageFieldTransformPlan? fieldTransforms = null,
        DataSourcePlan? sourcePlan = null,
        DataOperationHorizon? operationHorizon = null,
        DataRouteBinding? routeBinding = null,
        Func<DataRouteBinding?>? resolveRoute = null)
    {
        _inner = inner;
        _guards = guards ?? Array.Empty<IStorageGuard>();
        _readContributors = readContributors ?? Array.Empty<IReadFilterContributor>();
        _writePlan = StorageWritePlan.For(typeof(TEntity));
        _fieldTransforms = fieldTransforms ?? new StorageFieldTransformPlan([]);
        _fieldTransform = _fieldTransforms.For(typeof(TEntity));
        _isEntityFamily = EntityTypeCatalog.HasVariants(typeof(TEntity));
        _managed = ManagedFieldRegistry.ForType(typeof(TEntity));
        _segmentation = segmentation ?? DataSegmentationPlan.DataSegmentationScope.Empty;
        _sourcePlan = sourcePlan ?? DataSourcePlan.Default;
        _operationHorizon = operationHorizon;
        _routeBinding = routeBinding;
        _resolveRoute = resolveRoute;
        _idField = AggregateMetadata.GetIdSpec(typeof(TEntity))?.Prop.Name ?? "Id";
        // The adapter is inspected once iff this entity could ever be scoped: it has a managed descriptor (write-stamp
        // + equality read) OR a NON-default read-filter contributor (a predicate axis). The built-in equality
        // contributor alone is not a trigger — with no managed descriptor it yields no filter, so a non-tenant app is
        // a true no-op (byte-identical: no DataCaps.Describe, no FilterSupport). The result is consulted only when a
        // scope is actually active at runtime (DATA-0106 §4 fail-closed deferral).
        var couldScope = _managed.Count > 0 || !_segmentation.IsEmpty || HasNonDefaultReadContributor();
        if (couldScope) (_scopeAdapterOk, _scopeAdapterError, _filterCaps) = InspectScopeAdapter();
        else { _scopeAdapterOk = true; _filterCaps = FilterSupport.None; }
        // Hot-path ([koan-design-principles] §2/§4): the equality (tenancy) read-filter shape is static per (type,adapter)
        // — Eq over a fixed managed field — so its pushability is a CONSTANT, proven ONCE here. When the only active read
        // scope is the built-in equality contributor (no predicate axis) and that shape is pushable, the per-read Split is
        // skipped (RequireScopeForRead degrades to the single bool check — byte-identical to the pre-DATA-0106 read cost).
        // A predicate axis (dynamic shape) keeps the per-read Split as the source of truth.
        _skipReadPushabilityCheck = couldScope && _scopeAdapterOk
            && !HasNonDefaultReadContributor() && EqualityShapeIsPushable();
        // The delete operation-override (soft-delete) is a per-(type) constant — registered boot-time, AppliesTo is a
        // static predicate — so resolve it ONCE here (mirrors _managed), not per delete (no per-op registry lock).
        _deleteOverride = OperationOverrideRegistry.ForDelete(typeof(TEntity));
        _lifecycle = lifecycle;
    }

    private bool HasNonDefaultReadContributor()
    {
        for (var i = 0; i < _readContributors.Length; i++)
            if (_readContributors[i] is not ManagedEqualityReadContributor) return true;
        return false;
    }

    // The built-in equality contributor only ever emits Filter.Eq(StorageName, value) for AutoReadFilter descriptors.
    // Pushability of an Eq depends only on (field, operator) — never the value — so it is a per-(type,adapter) constant
    // we can settle once at construction and skip on every subsequent read (fix for the per-read Split regression).
    private bool EqualityShapeIsPushable()
    {
        foreach (var d in _managed)
        {
            if (!d.AutoReadFilter) continue;
            var probe = Filter.Eq(d.StorageName, "_");   // the value is irrelevant to pushability
            if (FilterSplitter.Split(probe, _filterCaps, typeof(TEntity)).Residual is not null) return false;
        }
        foreach (var field in _segmentation.Fields)
        {
            var probe = Filter.On(
                FieldPath.Managed(field.StorageName, field.ClrType),
                FilterOperator.Eq,
                FilterValue.Of("_"));
            if (FilterSplitter.Split(probe, _filterCaps, typeof(TEntity)).Residual is not null) return false;
        }
        return true;
    }

    // --- field-transform helpers (ARCH-0098 §0). All are no-op fast paths when the type has no transform. ---

    private StorageWritePlan WritePlanFor(TEntity entity)
        => !_isEntityFamily || entity.GetType() == typeof(TEntity)
            ? _writePlan
            : StorageWritePlan.For(entity.GetType());

    private StorageFieldTransformPlan.Compiled FieldTransformFor(TEntity entity)
        => !_isEntityFamily || entity.GetType() == typeof(TEntity)
            ? _fieldTransform
            : _fieldTransforms.For(entity.GetType());

    /// <summary>The persist payload for a write: an encrypted clone when a transform exists, else the entity itself.</summary>
    private TEntity WritePayload(TEntity entity)
    {
        var transform = FieldTransformFor(entity);
        return transform.HasTransforms ? (TEntity)transform.CloneForWrite(entity) : entity;
    }

    /// <summary>Restore plaintext on a single returned entity, in place.</summary>
    private TEntity? Reverse(TEntity? entity)
    {
        if (entity is not null)
        {
            var transform = FieldTransformFor(entity);
            if (transform.HasTransforms) transform.ApplyOnRead(entity);
        }
        return entity;
    }

    /// <summary>Restore plaintext on every entity in a query result, in place.</summary>
    private RepositoryQueryResult<TEntity> Reverse(RepositoryQueryResult<TEntity> result)
    {
        if (_fieldTransform.HasTransforms || _isEntityFamily)
            for (var i = 0; i < result.Items.Count; i++)
            {
                var e = result.Items[i];
                if (e is null) continue;
                var transform = FieldTransformFor(e);
                if (transform.HasTransforms) transform.ApplyOnRead(e);
            }
        return result;
    }

    /// <summary>Restore plaintext on every non-null entity in a get-many result, in place.</summary>
    private IReadOnlyList<TEntity?> Reverse(IReadOnlyList<TEntity?> items)
    {
        if (_fieldTransform.HasTransforms || _isEntityFamily)
            for (var i = 0; i < items.Count; i++)
            {
                var e = items[i];
                if (e is null) continue;
                var transform = FieldTransformFor(e);
                if (transform.HasTransforms) transform.ApplyOnRead(e);
            }
        return items;
    }

    private bool AnyFieldTransforms(IList<TEntity> entities)
    {
        if (!_isEntityFamily) return _fieldTransform.HasTransforms;
        for (var i = 0; i < entities.Count; i++)
            if (FieldTransformFor(entities[i]).HasTransforms) return true;
        return false;
    }

    /// <summary>Persist an Upsert under the current managed write scope (the shared tenant/managed-field path).</summary>
    private async Task<TEntity> PersistUpsert(
        TEntity payload,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        var values = CurrentManagedValues(segmentation);
        if (values is null) return await _inner.Upsert(payload, ct);
        using (ManagedFieldWriteScope.Enter(values)) return await _inner.Upsert(payload, ct);
    }

    private bool HasManaged => _managed.Count > 0 || !_segmentation.IsEmpty;

    // Inspect (do NOT throw at construction) whether the adapter can isolate a scoped entity. It must announce every
    // required isolation capability — over BOTH the managed descriptors (write-stamp + equality read) AND the
    // read-filter contributors (a predicate axis declares its own) — AND be an IQueryRepository (key-ops lower to
    // scoped queries). The adapter's FilterSupport is captured for the per-read pushability check (§4b). We defer the
    // throw to the first operation that actually has a scope in effect, so a non-tenant app — or a referenced-but-off
    // axis on a non-isolating adapter — is a true no-op (zero regression). Fail-closed when used.
    private (bool ok, string? error, FilterSupport caps) InspectScopeAdapter()
    {
        var describe = DataCaps.Describe(_inner, _inner.GetType().Name);
        var caps = describe.Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;
        foreach (var d in _managed)
        {
            if (d.RequiredCapability is { } req && !describe.Has(req))
                return (false,
                    $"Entity '{typeof(TEntity).Name}' is in an active managed scope requiring isolation capability '{req.Id}', " +
                    $"but the adapter '{_inner.GetType().Name}' does not announce it. Route it to an isolating adapter, or exempt the entity.",
                    caps);
        }
        foreach (var c in _readContributors)
        {
            if (c.RequiredCapability is { } req && !describe.Has(req))
                return (false,
                    $"Entity '{typeof(TEntity).Name}' is read-scoped by a contributor requiring isolation capability '{req.Id}', " +
                    $"but the adapter '{_inner.GetType().Name}' does not announce it. Route it to an isolating adapter, or exempt the entity.",
                    caps);
        }
        if (!_segmentation.IsEmpty && !describe.Has(DataCaps.Isolation.RowScoped))
            return (false,
                $"Entity '{typeof(TEntity).Name}' requires shared-row isolation, but the adapter " +
                $"'{_inner.GetType().Name}' does not announce '{DataCaps.Isolation.RowScoped.Id}'. " +
                "Route it to an adapter that supports shared-row isolation, or mark a genuine control-plane entity [HostScoped].",
                caps);
        if (_inner is not IQueryRepository<TEntity, TKey>)
            return (false,
                $"Entity '{typeof(TEntity).Name}' is in an active managed scope, but the adapter '{_inner.GetType().Name}' does not " +
                "implement IQueryRepository. Managed isolation lowers key operations to scoped queries, so it requires pushdown query support.",
                caps);
        return (true, null, caps);
    }

    /// <summary>Fail-closed for a scoped WRITE: the adapter must satisfy the static isolation contract (capability + query).</summary>
    private void RequireScopeStatic()
    {
        if (!_scopeAdapterOk) throw new InvalidOperationException(_scopeAdapterError);
    }

    /// <summary>
    /// Fail-closed for a scoped READ: the static contract PLUS the folded predicate must be <b>fully pushable</b> by the
    /// adapter (DATA-0106 §4b). An isolation filter MUST be enforced at the store — a residual would fetch cross-scope
    /// rows into process memory and skew Count, which is itself a leak. Bias-to-strict: a contributor that yields a
    /// filter the adapter cannot push fails closed even if it declared no capability (a null capability is no free pass).
    /// </summary>
    private void RequireScopeForRead(Filter folded)
    {
        RequireScopeStatic();
        if (_skipReadPushabilityCheck) return;   // equality-only shape: pushability proven once at construction
        var split = FilterSplitter.Split(folded, _filterCaps, typeof(TEntity));
        if (split.Residual is not null)
            throw new InvalidOperationException(
                $"Entity '{typeof(TEntity).Name}' is read-scoped by a predicate the adapter '{_inner.GetType().Name}' cannot fully " +
                "push down. An isolation filter must be enforced at the store, never evaluated in memory (a residual would fetch " +
                "cross-scope rows and skew Count). Route it to an adapter that pushes the predicate, or narrow the axis to a pushable shape.");
    }

    // --- managed-field helpers (no-op fast paths when nothing is registered / nothing is in scope) ---

    /// <summary>The managed values to stamp on the current write, or <c>null</c> when none is in scope (off / host).</summary>
    private IReadOnlyDictionary<string, object?>? CurrentManagedValues(DataSegmentationBinding segmentation)
    {
        if (!HasManaged) return null;
        Dictionary<string, object?>? values = null;
        foreach (var d in _managed)
        {
            var v = d.ValueProvider();
            if (v is null) continue;                 // off / host scope ⇒ this field is not stamped
            (values ??= new(StringComparer.Ordinal))[d.StorageName] = v;
        }
        if (segmentation.Values is { } segmented)
        {
            values ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var value in segmented) values[value.Key] = value.Value;
        }
        if (values is not null) RequireScopeStatic();   // an active scope on a non-isolating adapter fails closed
        return values;
    }

    // --- operation-semantics override (ARCH-0101 §4) — the soft-delete plane ---

    /// <summary>The delete override for a single keyed delete, or <c>null</c> when none is registered OR this exact
    /// entity is being hard-deleted (a target-scoped <c>.HardDelete()</c> bypass). The mass-delete paths use
    /// <see cref="_deleteOverride"/> directly — there is no batch hard-delete, so they always apply the override.
    /// Either way the read-scoping (IDOR) below is RETAINED — the bypass is plane-specific.</summary>
    private OperationOverrideDescriptor? DeleteOverrideFor(TKey id)
        => _deleteOverride is not null && !OperationOverrideBypass.IsBypassedFor(typeof(TEntity), id)
            ? _deleteOverride
            : null;

    /// <summary>
    /// Re-persist a VISIBLE (already read-scoped) row with the override's state field set (e.g. <c>__deleted=true</c>),
    /// through the UNGUARDED operation-override write channel so the mutable field is injected but never conflict-guarded.
    /// The isolation stamp (tenant) stays GUARDED. The row round-trips through the field transform (plaintext → re-encrypt)
    /// only when one exists (else both are no-ops).
    /// </summary>
    private async Task OverrideUpsert(
        TEntity row,
        OperationOverrideDescriptor ov,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        Reverse(row);                       // ensure plaintext before re-encrypting on write (no-op without a transform)
        WritePlanFor(row).ApplyAll(row);    // identity + [Timestamp(OnSave)] — the override IS a write
        var overrides = new Dictionary<string, object?>(StringComparer.Ordinal) { [ov.Field] = ov.OnDeleteValue };
        var values = CurrentManagedValues(segmentation);
        var payload = WritePayload(row);
        if (values is null)
            using (ManagedFieldWriteScope.EnterOverrides(overrides)) await _inner.Upsert(payload, ct);
        else
            using (ManagedFieldWriteScope.Enter(values, overrides)) await _inner.Upsert(payload, ct);
    }

    /// <summary>
    /// The AND-fold of every registered read-filter contributor's predicate isolating reads to the current ambient
    /// scope, or <c>null</c> when none constrains it (off / host / no axis) — DATA-0106 §2. Equality (tenancy) flows
    /// through the built-in <see cref="ManagedEqualityReadContributor"/>; a predicate axis (moderation) contributes its
    /// own. The tri-state is byte-identical to the former bespoke <c>ManagedReadFilter</c>: zero survivors ⇒ null (the
    /// unfiltered fast path); one ⇒ that filter; many ⇒ <c>Filter.All(survivors)</c> — no 1-element AllOf, no null operand.
    /// </summary>
    private Filter? ReadScopeFilter(DataSegmentationBinding segmentation)
    {
        var folded = FoldReadScope(segmentation);
        if (folded is null) return null;
        RequireScopeForRead(folded);                     // active scope on a non-isolating / non-pushing adapter fails closed
        return folded;
    }

    /// <summary>The AND-fold of every contributor's predicate in the current ambient, or <c>null</c> when nothing
    /// scopes now — WITHOUT the fail-closed throw (shared by <see cref="ReadScopeFilter"/> and the §9 diagnostics).</summary>
    private Filter? FoldReadScope(DataSegmentationBinding segmentation)
    {
        var contributed = ReadScopeFold.Fold(_readContributors, typeof(TEntity));
        var segmented = segmentation.ReadFilter;
        if (contributed is null) return segmented;
        if (segmented is null) return contributed;
        return Filter.All(contributed, segmented);
    }

    // --- IAxisScopeDiagnostics (ARCH-0101 §8/§9): the non-throwing read-scope inspection DataAxis.Explain + the boot
    // pre-flight read. The facade is the ONE authority — it already computed _scopeAdapterOk / _filterCaps at construction. ---
    string IAxisScopeDiagnostics.AdapterName => _inner.GetType().Name;
    bool IAxisScopeDiagnostics.CouldScope => _managed.Count > 0 || !_segmentation.IsEmpty || HasNonDefaultReadContributor();
    bool IAxisScopeDiagnostics.ScopeAdapterOk => _scopeAdapterOk;
    string? IAxisScopeDiagnostics.ScopeAdapterError => _scopeAdapterOk ? null : _scopeAdapterError;
    Filter? IAxisScopeDiagnostics.CurrentReadScope()
        => FoldReadScope(_segmentation.Bind("entity scope diagnostics"));
    bool IAxisScopeDiagnostics.IsFullyPushable(Filter folded)
        => _skipReadPushabilityCheck || FilterSplitter.Split(folded, _filterCaps, typeof(TEntity)).Residual is null;

    /// <summary>
    /// Whether any registered read-filter contributor constrains the current read of this type (ambient-active) — DATA-0106 §4.
    /// The raw-query and conditional-replace paths cannot carry the isolation predicate, so they fail closed when this is
    /// true. This trips for a PURE predicate axis (no managed field) too, which <c>CurrentManagedValues()</c> alone misses.
    /// </summary>
    private bool IsReadScoped(DataSegmentationBinding segmentation)
    {
        if (segmentation.ReadFilter is not null) return true;
        for (var i = 0; i < _readContributors.Length; i++)
            if (_readContributors[i].ReadFilter(typeof(TEntity)) is not null) return true;
        return false;
    }

    private QueryDefinition ApplyManaged(QueryDefinition query, Filter managed)
        => query.Where(query.Filter is null ? managed : Filter.All(query.Filter, managed));

    private QueryDefinition ScopedById(TKey id, Filter managed)
        => QueryDefinition.All.Where(Filter.All(Filter.Eq(_idField, id), managed));

    private QueryDefinition ScopedByIds(IReadOnlyList<TKey> ids, Filter managed)
        => QueryDefinition.All.Where(Filter.All(Filter.In(_idField, ids.Cast<object?>().ToList()), managed));

    // ARCH-0084: forward the inner provider's unified capabilities (native IDescribesCapabilities,
    // else the legacy-marker bridge) — so the facade is correct regardless of how inner declares.
    public void Describe(ICapabilities caps)
        => DataCaps.Describe(_inner, _inner.GetType().Name).CopyInto(caps);

    public async Task EnsureReady(CancellationToken ct = default)
    {
        await using var operation = await Guard(
            DataOperationEffect.SchemaOrAdmin,
            "entity ensure ready",
            ct,
            ensureReadiness: false);
        await _inner.EnsureReady(ct);
    }

    void IDataOperationGate.Demand(DataOperationEffect effect, string operation)
        => _sourcePlan.Demand(effect, operation);

    string IDataRouteBoundRepository.RouteNamespace =>
        _routeBinding?.Namespace ?? $"data-route:{_sourcePlan.RouteIdentity}:0";

    internal DataRouteBinding? RouteBinding => _routeBinding;

    private GuardedOperationAwaitable Guard(
        DataOperationEffect effect,
        string operation,
        CancellationToken ct,
        bool ensureReadiness = true)
        => new(GuardAsync(effect, operation, ct, ensureReadiness));

    private async Task<GuardedOperation> GuardAsync(
        DataOperationEffect effect,
        string operation,
        CancellationToken ct,
        bool ensureReadiness)
    {
        // Source policy is the first semantic action. It runs before cancellation observation,
        // segmentation callbacks, cross-cutting guards, readiness, lifecycle, or provider work.
        _sourcePlan.Demand(effect, operation);
        ct.ThrowIfCancellationRequested();
        DataOperationLease? lease = null;
        try
        {
            if (_operationHorizon is not null && _routeBinding is not null)
                lease = await _operationHorizon.Enter(_routeBinding, effect, operation, ct);
            var segmentation = _segmentation.Bind(operation);
            // Generic fail-closed pre-op checks at the chokepoint, BEFORE touching the store (DATA-0105 §0).
            // Cross-cutting modules register guards (Koan.Tenancy registers the tenant gate, ARCH-0095 P1); the
            // data core never names them. No registered guard ⇒ empty loop ⇒ no-op.
            for (var i = 0; i < _guards.Length; i++) _guards[i].Guard(typeof(TEntity));
            // The legacy EnsureReady contract can provision. Only the unrestricted source cell may enter it;
            // constrained sources rely on adapter-earned non-creating reachability and shape validation.
            if (ensureReadiness && _sourcePlan.UsesLegacyProvisioningReadiness)
                await _inner.EnsureReady(ct);
            return new GuardedOperation(segmentation, lease);
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class GuardedOperation(
        DataSegmentationBinding segmentation,
        DataOperationLease? lease) : IAsyncDisposable
    {
        public DataSegmentationBinding Segmentation { get; } = segmentation;

        internal GuardedOperation Activate()
        {
            lease?.Activate();
            return this;
        }

        public ValueTask DisposeAsync() => lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private readonly struct GuardedOperationAwaitable(Task<GuardedOperation> operation)
    {
        public Awaiter GetAwaiter() => new(operation.GetAwaiter());

        internal readonly struct Awaiter(
            System.Runtime.CompilerServices.TaskAwaiter<GuardedOperation> inner) :
            System.Runtime.CompilerServices.ICriticalNotifyCompletion
        {
            public bool IsCompleted => inner.IsCompleted;

            public GuardedOperation GetResult()
            {
                var operation = inner.GetResult();
                try
                {
                    return operation.Activate();
                }
                catch
                {
                    operation.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    throw;
                }
            }

            public void OnCompleted(Action continuation) => inner.OnCompleted(continuation);

            public void UnsafeOnCompleted(Action continuation) => inner.UnsafeOnCompleted(continuation);
        }
    }

    // Raw facade reads used by lifecycle Prior/remove preparation. They retain every Data-owned
    // guard, isolation, and field-transform decision already established by the caller, but do not
    // recursively emit a Load lifecycle while another lifecycle operation is in progress.
    private async Task<TEntity?> ReadOne(TKey id, DataSegmentationBinding segmentation, CancellationToken ct)
    {
        var managed = ReadScopeFilter(segmentation);
        if (managed is null) return Reverse(await _inner.Get(id, ct));
        var result = await RequireQuery().Query(ScopedById(id, managed), ct);
        return Reverse(result.Items.Count > 0 ? result.Items[0] : null);
    }

    private async Task<IReadOnlyList<TEntity?>> ReadMany(
        IReadOnlyList<TKey> ids,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        var managed = ReadScopeFilter(segmentation);
        IReadOnlyList<TEntity?> raw;
        if (managed is null)
        {
            raw = await _inner.GetMany(ids, ct);
        }
        else
        {
            var result = await RequireQuery().Query(ScopedByIds(ids, managed), ct);
            raw = result.Items.Cast<TEntity?>().ToArray();
        }
        return Reverse(NormalizeGetMany(ids, raw));
    }

    private static IReadOnlyList<TEntity?> NormalizeGetMany(
        IReadOnlyList<TKey> ids,
        IReadOnlyList<TEntity?> returned)
    {
        // The conforming hot path is allocation-free: validate positional identity and return it unchanged.
        if (returned.Count == ids.Count)
        {
            var positional = true;
            for (var i = 0; i < ids.Count; i++)
            {
                var entity = returned[i];
                if (entity is not null && !EqualityComparer<TKey>.Default.Equals(entity.Id, ids[i]))
                {
                    positional = false;
                    break;
                }
            }
            if (positional) return returned;
        }

        var requested = new HashSet<TKey>(ids);
        var byId = new Dictionary<TKey, TEntity>();
        foreach (var entity in returned)
        {
            if (entity is null) continue;
            if (!requested.Contains(entity.Id))
                throw new GetManyReceiptRejectedException(typeof(TEntity).FullName ?? typeof(TEntity).Name);
            byId.TryAdd(entity.Id, entity);
        }

        var normalized = new TEntity?[ids.Count];
        for (var i = 0; i < ids.Count; i++)
            if (byId.TryGetValue(ids[i], out var entity)) normalized[i] = entity;
        return normalized;
    }

    private async Task ApplyLoadLifecycle(IReadOnlyList<TEntity> entities, CancellationToken ct)
    {
        if (_lifecycle is not { HasLoad: true }) return;
        foreach (var entity in entities)
        {
            ct.ThrowIfCancellationRequested();
            await _lifecycle.ApplyLoad(entity, ct);
        }
    }

    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Read, "entity get", ct);
        var segmentation = operationScope.Segmentation;
        var entity = await ReadOne(id, segmentation, ct);
        if (entity is not null && _lifecycle is { HasLoad: true })
            await _lifecycle.ApplyLoad(entity, ct);
        return entity;
    }

    public async Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Read, "entity get many", ct);
        var segmentation = operationScope.Segmentation;
        var idList = ids as IReadOnlyList<TKey> ?? ids.ToList();
        var entities = await ReadMany(idList, segmentation, ct);
        if (_lifecycle is { HasLoad: true })
            foreach (var entity in entities)
                if (entity is not null) await _lifecycle.ApplyLoad(entity, ct);
        return entities;
    }

    // --- structured query ---

    public async Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        query = query with { Filter = Filter.Snapshot(query.Filter), Sort = query.Sort.ToArray() };
        var support = DataCaps.Describe(_inner, _inner.GetType().Name).Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;
        var (adapterQuery, residual) = FilterPushdownCoordinator.Plan(query, support, typeof(TEntity));
        var result = await QueryCandidates(adapterQuery, ct);
        var finalized = FilterPushdownCoordinator.Finalize(query, adapterQuery, residual, result);
        await ApplyLoadLifecycle(finalized.Page, ct);
        DataQueryExecution<TEntity, TKey>.ValidateEvidence(result.ReadEvidence, finalized.Page);
        return new RepositoryQueryResult<TEntity>
        {
            Items = finalized.Page,
            FilterHandled = true,
            SortHandled = query.Sort.ToHashSet(),
            PaginationHandled = query.HasPagination,
            TotalCount = query.CountStrategy is null ? null : finalized.TotalCount,
            CountExecution = query.CountStrategy is null ? CountExecutionKind.None :
                finalized.IsEstimate ? CountExecutionKind.Fast : CountExecutionKind.Exact,
            IsEstimate = query.CountStrategy is not null && finalized.IsEstimate,
            MaterializedAllCandidates = result.MaterializedAllCandidates || finalized.FellBackInMemory,
            ReadEvidence = result.ReadEvidence
        };
    }

    private async Task<RepositoryQueryResult<TEntity>> QueryCandidates(
        QueryDefinition query,
        CancellationToken ct)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        await using var operationScope = await Guard(DataOperationEffect.Read, "entity query", ct);
        if (Filter.HasCounterpart(query.Filter))
        {
            return await ExecuteCounterpartRead(query, operationScope.Segmentation, async bound =>
            {
                var result = await RequireQuery().Query(bound.Query, ct);
                QueryReceiptValidator.Validate(bound.Query, result);
                result.ReadEvidence = bound.Evidence(result.Items);
                return Reverse(result);
            }, ct);
        }
        var segmentation = operationScope.Segmentation;
        var managed = ReadScopeFilter(segmentation);
        return Reverse(await RequireQuery().Query(managed is null ? query : ApplyManaged(query, managed), ct));
    }

    Task<RepositoryQueryResult<TEntity>> IDataQueryBoundary<TEntity, TKey>.QueryCandidates(
        QueryDefinition query,
        CancellationToken ct)
        => QueryCandidates(query, ct);

    Task IDataQueryBoundary<TEntity, TKey>.MaterializeVisible(
        IReadOnlyList<TEntity> entities,
        CancellationToken ct)
        => ApplyLoadLifecycle(entities, ct);

    ValueTask IDataQueryBoundary<TEntity, TKey>.MaterializeVisible(
        TEntity entity,
        CancellationToken ct)
        => MaterializeVisible(entity, ct);

    private async ValueTask MaterializeVisible(TEntity entity, CancellationToken ct)
    {
        if (_lifecycle is not { HasLoad: true }) return;
        _ = await _lifecycle.ApplyLoad(entity, ct);
    }

    public async Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        await using var operationScope = await Guard(DataOperationEffect.Read, "entity count", ct);
        if (Filter.HasCounterpart(query.Filter))
        {
            return await ExecuteCounterpartRead(query, operationScope.Segmentation,
                bound => RequireQuery().Count(bound.Query, ct), ct);
        }
        var segmentation = operationScope.Segmentation;
        var managed = ReadScopeFilter(segmentation);
        return await RequireQuery().Count(managed is null ? query : ApplyManaged(query, managed), ct);
    }

    public async Task<BoundedQueryResult<TEntity>> QueryBoundedCandidates(
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct = default)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        var result = await QueryBoundedCandidatesRaw(query, maxCandidates, ct);
        await ApplyLoadLifecycle(result.Items, ct);
        DataQueryExecution<TEntity, TKey>.ValidateEvidence(result.ReadEvidence, result.Items);
        return result;
    }

    private async Task<BoundedQueryResult<TEntity>> QueryBoundedCandidatesRaw(
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        await using var operationScope = await Guard(DataOperationEffect.Read, "entity bounded query", ct);
        if (Filter.HasCounterpart(query.Filter))
        {
            var native = _inner as IBoundedQueryRepository<TEntity, TKey>
                ?? throw new NotSupportedException("The selected connector cannot bound counterpart query candidates.");
            var counterpartResult = await ExecuteCounterpartRead(query, operationScope.Segmentation, async bound =>
            {
                var result = await native.QueryBoundedCandidates(bound.Query, maxCandidates, ct);
                if (result.Items.Count > maxCandidates)
                    throw new InvalidOperationException("The connector exceeded the counterpart candidate bound.");
                return result with { ReadEvidence = bound.Evidence(result.Items) };
            }, ct);
            foreach (var entity in counterpartResult.Items)
            {
                var transform = FieldTransformFor(entity);
                if (transform.HasTransforms) transform.ApplyOnRead(entity);
            }
            return counterpartResult;
        }
        var segmentation = operationScope.Segmentation;
        var managed = ReadScopeFilter(segmentation);
        var bounded = _inner as IBoundedQueryRepository<TEntity, TKey>
            ?? throw new NotSupportedException(
                $"The adapter backing {typeof(TEntity).Name} does not support provider-enforced bounded candidate reads.");
        var result = await bounded.QueryBoundedCandidates(
            managed is null ? query : ApplyManaged(query, managed),
            maxCandidates,
            ct);
        if (_fieldTransform.HasTransforms || _isEntityFamily)
            foreach (var entity in result.Items)
            {
                var transform = FieldTransformFor(entity);
                if (transform.HasTransforms) transform.ApplyOnRead(entity);
            }
        return result;
    }

    Task<BoundedQueryResult<TEntity>> IDataQueryBoundary<TEntity, TKey>.QueryBoundedCandidatesRaw(
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct)
        => QueryBoundedCandidatesRaw(query, maxCandidates, ct);

    private IQueryRepository<TEntity, TKey> RequireQuery()
        => _inner as IQueryRepository<TEntity, TKey>
           ?? throw new NotSupportedException(
               $"The adapter backing {typeof(TEntity).Name} does not implement IQueryRepository.");

    // --- raw query escape hatch ---
    // The managed predicate CANNOT cover an opaque raw query (DATA-0105 §3.5): RLS is the backstop. With no
    // RLS capability, a managed-scoped raw read fails closed under an active scope rather than leak cross-scope.

    public async Task<RepositoryQueryResult<TEntity>> QueryRaw(string query, object? parameters, QueryDefinition shaping, CancellationToken ct = default)
    {
        Filter.RequireRowOnly(shaping.Filter, "A raw query");
        await using var operationScope = await Guard(DataOperationEffect.Read, "entity raw query", ct);
        var segmentation = operationScope.Segmentation;
        GuardRawAgainstActiveScope(segmentation);
        if (_inner is not IRawQueryRepository<TEntity, TKey> raw)
            throw new NotSupportedException($"The adapter backing {typeof(TEntity).Name} does not support raw queries.");
        var result = Reverse(await raw.QueryRaw(query, parameters, shaping, ct));
        await ApplyLoadLifecycle(result.Items, ct);
        return result;
    }

    public async Task<CountResult> CountRaw(string query, object? parameters, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Read, "entity raw count", ct);
        var segmentation = operationScope.Segmentation;
        GuardRawAgainstActiveScope(segmentation);
        return _inner is IRawQueryRepository<TEntity, TKey> raw
            ? await raw.CountRaw(query, parameters, ct)
            : throw new NotSupportedException($"The adapter backing {typeof(TEntity).Name} does not support raw queries.");
    }

    // The opaque raw SQL cannot carry the isolation predicate — neither the managed equality NOR a predicate axis's
    // (moderation) read-filter. So a raw read fails closed when EITHER an active managed write-scope OR any active
    // read-filter contributor constrains the type (DATA-0106 §4 — the trigger rides the contributor union, not just
    // _managed.Count; a pure predicate axis has no managed field and would otherwise slip through). RLS is the backstop.
    private void GuardRawAgainstActiveScope(DataSegmentationBinding segmentation)
    {
        if (IsReadScoped(segmentation) || (HasManaged && CurrentManagedValues(segmentation) is not null))
            throw new NotSupportedException(
                $"Raw queries on scoped entity '{typeof(TEntity).Name}' are not isolated by the read-filter " +
                "predicate (the SQL is opaque). They are gated behind a store-level isolation backstop (e.g. RLS); with " +
                "none available the call fails closed under an active scope rather than read across scopes.");
    }

    // --- writes ---

    private ValueTask<TEntity?> ReadPrior(
        TEntity entity,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
        => EqualityComparer<TKey>.Default.Equals(entity.Id, default!)
            ? new ValueTask<TEntity?>((TEntity?)null)
            : new ValueTask<TEntity?>(ReadOne(entity.Id, segmentation, ct));

    private async Task<TEntity> PersistPreparedUpsert(
        TEntity model,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        WritePlanFor(model).ApplyAll(model);
        var payload = WritePayload(model);
        if (ReferenceEquals(payload, model)) return await PersistUpsert(model, segmentation, ct);
        await PersistUpsert(payload, segmentation, ct);
        return model;
    }

    private async Task<int> PersistPreparedMany(
        IList<TEntity> models,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        if (models.Count == 0) return 0;
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            WritePlanFor(model).ApplyAll(model);
        }

        var payloads = AnyFieldTransforms(models)
            ? (IList<TEntity>)models.Select(WritePayload).ToList()
            : models;
        var values = CurrentManagedValues(segmentation);
        var reported = values is null
            ? await _inner.UpsertMany(payloads, ct)
            : await PersistScoped();
        if (reported != models.Count)
            throw new BulkMutationReceiptRejectedException(
                typeof(TEntity).FullName ?? typeof(TEntity).Name,
                models.Count,
                reported,
                DataCommitOutcome.Unknown);
        return reported;

        async Task<int> PersistScoped()
        {
            using (ManagedFieldWriteScope.Enter(values!))
                return await _inner.UpsertMany(payloads, ct);
        }
    }

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity upsert", ct);
        var segmentation = operationScope.Segmentation;
        if (_lifecycle is not { HasUpsert: true })
            return await PersistPreparedUpsert(model, segmentation, ct);

        var context = await _lifecycle.BeginUpsert(model, token => ReadPrior(model, segmentation, token), ct);
        var persisted = await PersistPreparedUpsert(context.Current, segmentation, ct);
        await _lifecycle.CompleteUpsert(context, persisted);
        return context.Current;
    }

    public async Task<MutationResult<TEntity, TKey>> Insert(TEntity model, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity insert", ct, ensureReadiness: false);
        ArgumentNullException.ThrowIfNull(model);
        if (EntityContext.Current?.TransactionCoordinator is not null)
            throw new NotSupportedException(
                "Atomic insertion cannot run in a deferred coordination scope. Perform the insertion outside that scope.");
        if (!DataCaps.Describe(_inner, _inner.GetType().Name).Has(DataCaps.Write.InsertOnly) ||
            _inner is not IInsertOnlyRepository<TEntity, TKey> inserts)
            throw new NotSupportedException(
                $"The adapter backing {typeof(TEntity).Name} cannot guarantee atomic insertion for this identity shape. " +
                "Select a connector and identity shape that support insert-only writes.");
        if (_sourcePlan.UsesLegacyProvisioningReadiness) await _inner.EnsureReady(ct);

        // An attempted insert has no prior row. Never turn read visibility into an absence proof,
        // and never fetch a hidden collision into lifecycle state.
        EntityLifecycleContext<TEntity>? context = null;
        if (_lifecycle is { HasUpsert: true })
            context = await _lifecycle.BeginUpsert(model, _ => new ValueTask<TEntity?>((TEntity?)null), ct);
        var current = context?.Current ?? model;
        WritePlanFor(current).ApplyAll(current);
        var submittedKey = current.Id;
        var payload = WritePayload(current);
        if (!EqualityComparer<TKey>.Default.Equals(payload.Id, submittedKey))
            throw new InvalidOperationException("A field transform cannot change an insertion's identity. Preserve the submitted identity in the storage payload.");
        var values = CurrentManagedValues(operationScope.Segmentation);
        MutationResult<TEntity, TKey> result;
        if (values is null) result = await inserts.Insert(payload, ct);
        else
        {
            using (ManagedFieldWriteScope.Enter(values)) result = await inserts.Insert(payload, ct);
        }

        if (result is null)
            throw new MutationReceiptRejectedException(typeof(TEntity).FullName ?? typeof(TEntity).Name,
                "Insertion must return a commit receipt.", DataCommitOutcome.Unknown);
        var sameKey = EqualityComparer<TKey>.Default.Equals(result.Key, payload.Id) &&
            (EqualityComparer<TKey>.Default.Equals(submittedKey, default!) ||
             EqualityComparer<TKey>.Default.Equals(submittedKey, result.Key));
        if (sameKey && result.Outcome == MutationOutcome.Conflict &&
            result.CommitOutcome == DataCommitOutcome.NotCommitted && result.Entity is null)
            return result;
        if (!sameKey || EqualityComparer<TKey>.Default.Equals(result.Key, default!) ||
            result.Outcome != MutationOutcome.Inserted || result.CommitOutcome != DataCommitOutcome.Committed ||
            result.Entity is null || !EqualityComparer<TKey>.Default.Equals(result.Entity.Id, result.Key))
            throw new MutationReceiptRejectedException(
                typeof(TEntity).FullName ?? typeof(TEntity).Name,
                "Insertion must report the same identity and either Inserted/Committed or Conflict/NotCommitted without an entity.",
                DataCommitOutcome.Unknown);

        // A transformed payload is detached. Copy only the native generated identity back; its
        // encrypted field values must never become the application or lifecycle result.
        if (!EqualityComparer<TKey>.Default.Equals(current.Id, result.Key))
            AggregateMetadata.GetIdSpec(current.GetType())!.Prop.SetValue(current, result.Key);
        if (context is not null) await _lifecycle!.CompleteUpsert(context, current);
        return result with { Entity = current };
    }

    async Task<MutationResult<TEntity, TKey>> IDataMutationOutcomes<TEntity, TKey>.UpsertWithOutcome(
        TEntity model,
        CancellationToken ct)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity upsert with outcome", ct);
        var segmentation = operationScope.Segmentation;
        var capabilities = DataCaps.Describe(_inner, _inner.GetType().Name);
        if (!capabilities.Has(DataCaps.Write.MutationOutcomes) ||
            _inner is not IMutationOutcomeRepository<TEntity, TKey> outcomes)
            throw new NotSupportedException(
                $"The adapter backing {typeof(TEntity).Name} does not expose exact native upsert outcomes. " +
                "Use Save when insert/update distinction is not required, or route to an adapter that advertises mutation outcomes.");

        EntityLifecycleContext<TEntity>? context = null;
        if (_lifecycle is { HasUpsert: true })
            context = await _lifecycle.BeginUpsert(model, token => ReadPrior(model, segmentation, token), ct);
        var current = context?.Current ?? model;
        WritePlanFor(current).ApplyAll(current);
        var payload = WritePayload(current);
        var values = CurrentManagedValues(segmentation);
        MutationResult<TEntity, TKey> result;
        if (values is null)
        {
            result = await outcomes.UpsertWithOutcome(payload, ct);
        }
        else
        {
            using (ManagedFieldWriteScope.Enter(values))
                result = await outcomes.UpsertWithOutcome(payload, ct);
        }

        if (!EqualityComparer<TKey>.Default.Equals(result.Key, current.Id) ||
            result.Outcome is not (MutationOutcome.Inserted or MutationOutcome.Updated) ||
            result.CommitOutcome != DataCommitOutcome.Committed)
            throw new MutationReceiptRejectedException(
                typeof(TEntity).FullName ?? typeof(TEntity).Name,
                "A successful native upsert must report the same key, Inserted or Updated, and CommitOutcome=Committed.",
                DataCommitOutcome.Unknown);

        if (context is not null) await _lifecycle!.CompleteUpsert(context, current);
        return result with { Entity = current };
    }

    async Task<MutationResult<TEntity, TKey>> IDataMutationOutcomes<TEntity, TKey>.DeleteWithOutcome(
        TKey id,
        CancellationToken ct)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity delete with outcome", ct);
        var segmentation = operationScope.Segmentation;
        var entity = await ReadOne(id, segmentation, ct);
        if (entity is null)
            return new MutationResult<TEntity, TKey>(
                id, MutationOutcome.Missing, null, DataCommitOutcome.NotCommitted);

        EntityLifecycleContext<TEntity>? context = null;
        if (_lifecycle is { HasRemove: true })
            context = await _lifecycle.BeginRemove(entity, ct);
        var current = context?.Current ?? entity;
        var deleted = await DeleteWithoutLifecycle(current.Id, segmentation, ct);
        if (!deleted)
            return new MutationResult<TEntity, TKey>(
                id, MutationOutcome.Conflict, current, DataCommitOutcome.NotCommitted);
        if (context is not null) await _lifecycle!.CompleteRemove(context);
        return new MutationResult<TEntity, TKey>(
            current.Id, MutationOutcome.Deleted, current, DataCommitOutcome.Committed);
    }

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity batch upsert", ct);
        var segmentation = operationScope.Segmentation;
        var list = models as IList<TEntity> ?? models.ToList();
        if (_lifecycle is { HasUpsert: true })
        {
            // Prepare the complete set before the first write so a domain rejection never creates a
            // framework-induced partial batch. Provider failures retain their normal non-transactional semantics.
            var contexts = new List<EntityLifecycleContext<TEntity>>(list.Count);
            foreach (var model in list)
            {
                ct.ThrowIfCancellationRequested();
                contexts.Add(await _lifecycle.BeginUpsert(model, token => ReadPrior(model, segmentation, token), ct));
            }

            var persisted = contexts.Select(context => context.Current).ToList();
            await PersistPreparedMany(persisted, segmentation, ct);
            for (var i = 0; i < contexts.Count; i++)
                await _lifecycle.CompleteUpsert(contexts[i], persisted[i]);
            return persisted.Count;
        }
        return await PersistPreparedMany(list, segmentation, ct);
    }

    public async Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity delete", ct);
        var segmentation = operationScope.Segmentation;
        if (_lifecycle is not { HasRemove: true })
            return await DeleteWithoutLifecycle(id, segmentation, ct);

        var entity = await ReadOne(id, segmentation, ct);
        if (entity is null) return false;
        var context = await _lifecycle.BeginRemove(entity, ct);
        var removed = await DeleteWithoutLifecycle(context.Current.Id, segmentation, ct);
        if (removed) await _lifecycle.CompleteRemove(context);
        return removed;
    }

    private async Task<bool> DeleteWithoutLifecycle(
        TKey id,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        var managed = ReadScopeFilter(segmentation);
        var ov = DeleteOverrideFor(id);   // null when this exact entity is being hard-deleted (target-scoped bypass)
        if (ov is not null)
        {
            // Soft-delete: load the VISIBLE (read-scoped) row, re-persist with the override field set. The load is
            // still IDOR-scoped, so a soft-delete can only soft-remove a row the caller can see.
            var scoped = managed is null ? QueryDefinition.All.Where(Filter.Eq(_idField, id)) : ScopedById(id, managed);
            var res = await RequireQuery().Query(scoped, ct);
            if (res.Items.Count == 0) return false;
            await OverrideUpsert(res.Items[0], ov, segmentation, ct);
            return true;
        }
        if (managed is null) return await _inner.Delete(id, ct);
        // check-then-delete: only an owned row may be deleted by id (IDOR).
        var res2 = await RequireQuery().Query(ScopedById(id, managed), ct);
        if (res2.Items.Count == 0) return false;
        return await _inner.Delete(id, ct);
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity batch delete", ct);
        var segmentation = operationScope.Segmentation;
        var idList = ids as IReadOnlyList<TKey> ?? ids.ToList();
        if (_lifecycle is { HasRemove: true })
            return await DeleteManyWithLifecycle(idList, segmentation, ct);
        return await DeleteManyWithoutLifecycle(idList, segmentation, ct);
    }

    private async Task<int> DeleteManyWithLifecycle(
        IReadOnlyList<TKey> ids,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        var existing = await ReadMany(ids, segmentation, ct);
        var contexts = new List<EntityLifecycleContext<TEntity>>(existing.Count);
        foreach (var entity in existing)
        {
            if (entity is null) continue;
            contexts.Add(await _lifecycle!.BeginRemove(entity, ct));
        }

        var completed = new List<EntityLifecycleContext<TEntity>>(contexts.Count);
        var deleted = await DeleteManyWithoutLifecycle(
            contexts.Select(context => context.Current.Id).ToArray(), segmentation, ct);
        if (deleted != contexts.Count)
            throw new BulkMutationReceiptRejectedException(
                typeof(TEntity).FullName ?? typeof(TEntity).Name,
                contexts.Count,
                deleted,
                DataCommitOutcome.Unknown);
        completed.AddRange(contexts);
        foreach (var context in completed)
            await _lifecycle!.CompleteRemove(context);
        return completed.Count;
    }

    private async Task<int> DeleteManyWithoutLifecycle(
        IReadOnlyList<TKey> idList,
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        var managed = ReadScopeFilter(segmentation);
        var ov = _deleteOverride;   // mass delete: no batch hard-delete exists, so the override always applies
        if (ov is not null)
        {
            var scoped = managed is null
                ? QueryDefinition.All.Where(Filter.In(_idField, idList.Cast<object?>().ToList()))
                : ScopedByIds(idList, managed);
            var soft = await RequireQuery().Query(scoped, ct);
            foreach (var row in soft.Items) await OverrideUpsert(row, ov, segmentation, ct);
            return soft.Items.Count;
        }
        if (managed is null) return await _inner.DeleteMany(idList, ct);
        var res = await RequireQuery().Query(ScopedByIds(idList, managed), ct);
        var owned = res.Items.Select(e => e.Id).ToList();
        return owned.Count == 0 ? 0 : await _inner.DeleteMany(owned, ct);
    }

    private async Task<long> DeleteVisibleInBoundedPages(
        DataSegmentationBinding segmentation,
        CancellationToken ct)
    {
        if (typeof(TKey) != typeof(string))
            throw new NotSupportedException(
                $"Semantic mass deletion for '{typeof(TEntity).Name}' requires a provider-stable string Entity identity. " +
                "Delete explicit identities or use a provider-native safe removal contract for this key type.");

        var capabilities = DataCaps.Describe(_inner, _inner.GetType().Name);
        if (!capabilities.Has(DataCaps.Query.ProviderBoundedPaging))
            throw new NotSupportedException(
                $"The adapter backing {typeof(TEntity).Name} cannot execute semantic mass deletion with bounded provider pages. " +
                "Delete explicit identities or route to a provider that advertises provider-bounded paging.");

        var queryRepository = RequireQuery();
        var pageSize = Infrastructure.Constants.Defaults.UnboundedLoopPageSize;
        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var managed = ReadScopeFilter(segmentation);
            var query = QueryDefinition.All
                .WithSort<TEntity>(sort => sort.OrderBy(entity => entity.Id))
                .WithPagination(1, pageSize)
                .WithCountStrategy(null);
            if (managed is not null) query = query.Where(managed);

            var page = await queryRepository.Query(query, ct);
            QueryReceiptValidator.Validate(query, page);
            if (!page.PaginationHandled || !page.SortFullyHandled(query))
                throw new QueryReceiptRejectedException(
                    typeof(TEntity).FullName ?? typeof(TEntity).Name,
                    !page.PaginationHandled ? QueryReceiptAxis.Pagination : QueryReceiptAxis.Sort,
                    "Semantic mass deletion requires one provider-bounded, completely ordered page before each mutation step.");
            if (page.Items.Count == 0) return total;

            var ids = page.Items.Select(entity => entity.Id).ToArray();
            var deleted = _lifecycle is { HasRemove: true }
                ? await DeleteManyWithLifecycle(ids, segmentation, ct)
                : await DeleteManyWithoutLifecycle(ids, segmentation, ct);
            if (deleted <= 0)
                throw new BulkMutationReceiptRejectedException(
                    typeof(TEntity).FullName ?? typeof(TEntity).Name,
                    ids.Length,
                    deleted,
                    DataCommitOutcome.Unknown);
            total = checked(total + deleted);
        }
    }

    public async Task<int> DeleteAll(CancellationToken ct = default)
    {
        await using var operationScope = await Guard(DataOperationEffect.Write, "entity delete all", ct);
        var segmentation = operationScope.Segmentation;
        var managed = ReadScopeFilter(segmentation);
        if (_lifecycle is { HasRemove: true } || _deleteOverride is not null || managed is not null ||
            _sourcePlan.StorageLifecycle == StorageLifecycle.External)
            return checked((int)await DeleteVisibleInBoundedPages(segmentation, ct));
        return await _inner.DeleteAll(ct);
    }

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        var effect = strategy == RemoveStrategy.Fast
            ? DataOperationEffect.SchemaOrAdmin
            : DataOperationEffect.Write;
        await using var operationScope = await Guard(effect, "entity remove all", ct);
        var segmentation = operationScope.Segmentation;
        if (_sourcePlan.StorageLifecycle == StorageLifecycle.External && strategy == RemoveStrategy.Optimized)
            strategy = RemoveStrategy.Safe;
        var managed = ReadScopeFilter(segmentation);
        if (strategy != RemoveStrategy.Fast &&
            (_lifecycle is { HasRemove: true } || _deleteOverride is not null || managed is not null))
            return await DeleteVisibleInBoundedPages(segmentation, ct);
        if (managed is null) return await _inner.RemoveAll(strategy, ct);
        // Fast removal cannot preserve a managed scope; Guard already rejected External and this branch refuses
        // to disguise a client scan as optimized work.
        throw new NotSupportedException(
            $"Fast removal for scoped entity '{typeof(TEntity).Name}' cannot preserve the active isolation boundary. " +
            "Use RemoveStrategy.Safe or Optimized so Koan can delete bounded visible pages.");
    }

    // One normalized guard enters before readiness or lifecycle awaits. The optional native primitive
    // never inherits an ordinary upsert fallback or changes the identity selected by the caller.
    public async Task<bool> ConditionalReplaceAsync(TEntity model, Filter guard, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(guard);
        ct.ThrowIfCancellationRequested();
        if (!StableConditionalKey(typeof(TKey)) || EqualityComparer<TKey>.Default.Equals(model.Id, default!) ||
            model.Id is string text && string.IsNullOrWhiteSpace(text))
            throw new NotSupportedException("Conditional replacement requires a non-default immutable scalar identity.");
        if (model.GetType() != typeof(TEntity) || EntityRootDescriptor.For(typeof(TEntity)).IsVariant)
            throw new NotSupportedException("Conditional replacement of Entity variants requires an exact native membership guard and is not supported.");
        var submittedKey = model.Id;
        var routing = EntityContext.Current;
        var frozen = Filter.Snapshot(guard, requireImmutableValues: true)!;
        Filter.RequireRowOnly(frozen, "Conditional replacement");
        var capabilities = DataCaps.Describe(_inner, _inner.GetType().Name);
        if (!capabilities.Has(DataCaps.Write.ConditionalReplace) ||
            _inner is not IConditionalWriteRepository<TEntity, TKey> cas)
            throw new NotSupportedException($"The adapter backing {typeof(TEntity).Name} does not support conditional replacement. Select a supporting connector; ordinary Save cannot preserve the guard.");
        var guardSupport = capabilities.Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;
        if (FilterSplitter.Split(frozen, guardSupport, typeof(TEntity)).Residual is not null)
            throw new NotSupportedException("Conditional replacement requires a complete native row predicate. CLR or residual guards are not supported.");
        DemandConditionalFields(frozen);
        if (routing?.TransactionCoordinator is not null)
            throw new NotSupportedException("Conditional replacement cannot run in a deferred coordination scope.");

        await using var operationScope = await Guard(DataOperationEffect.Write, "entity conditional replace", ct, ensureReadiness: false);
        DemandUnscopedConditional(operationScope.Segmentation);
        if (_sourcePlan.UsesLegacyProvisioningReadiness) await _inner.EnsureReady(ct);
        EntityLifecycleContext<TEntity>? context = null;
        if (_lifecycle is { HasUpsert: true })
            context = await _lifecycle.BeginUpsert(model, token => ReadPrior(model, operationScope.Segmentation, token), ct);

        var current = context?.Current ?? model;
        WritePlanFor(current).ApplyAll(current);
        var payload = WritePayload(current);
        if (current.GetType() != typeof(TEntity) || payload.GetType() != typeof(TEntity))
            throw new NotSupportedException("Conditional replacement cannot substitute a variant during lifecycle or write preparation.");
        if (!Filter.EquivalentValue(submittedKey, current.Id) ||
            !Filter.EquivalentValue(submittedKey, payload.Id))
            throw new InvalidOperationException("Conditional replacement cannot change the submitted identity during lifecycle or write preparation.");
        if (!Equals(routing, EntityContext.Current) ||
            (_resolveRoute is not null && _resolveRoute()?.RepositoryIdentity != _routeBinding?.RepositoryIdentity))
            throw new InvalidOperationException("Conditional replacement routing changed during preparation. Start a new operation in the intended scope.");
        DemandUnscopedConditional(_segmentation.Bind("entity conditional replace"));
        ct.ThrowIfCancellationRequested();
        var replaced = await cas.ConditionalReplaceAsync(payload, frozen, ct);
        if (replaced && context is not null)
            await _lifecycle!.CompleteUpsert(context, current);
        return replaced;
    }

    private void DemandUnscopedConditional(DataSegmentationBinding segmentation)
    {
        if (IsReadScoped(segmentation) || (HasManaged && CurrentManagedValues(segmentation) is not null))
            throw new NotSupportedException($"Conditional replacement for '{typeof(TEntity).Name}' cannot preserve the active managed or read scope. Use a qualified unscoped source; ordinary Save cannot preserve the guard.");
    }

    private static bool StableConditionalKey(Type type) => type.IsPrimitive || type.IsEnum ||
        type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
        type == typeof(DateOnly) || type == typeof(TimeOnly);

    private static void DemandConditionalFields(Filter filter)
    {
        switch (filter)
        {
            case AllOf all:
                foreach (var item in all.Operands) DemandConditionalFields(item);
                break;
            case AnyOf any:
                foreach (var item in any.Operands) DemandConditionalFields(item);
                break;
            case Not not: DemandConditionalFields(not.Operand); break;
            case FieldFilter field:
                var resolved = FieldPathResolver.Resolve(typeof(TEntity), field.Field);
                if (resolved.IsManaged || resolved.Members.Any(member =>
                        member.IsDefined(typeof(Koan.Data.Abstractions.Annotations.ClassifiedAttribute), inherit: true)))
                    throw new NotSupportedException("Conditional guards cannot compare managed or classified fields. Use an ordinary persisted revision field.");
                break;
        }
    }

    public IBatchSet<TEntity, TKey> CreateBatch() => new BatchFacade(this);

    public async Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        var effect = instruction.EffectiveEffect();
        _sourcePlan.Demand(effect, "entity instruction");
        if (!_segmentation.IsEmpty && instruction.Name != DataInstructions.EnsureCreated)
        {
            _ = _segmentation.Bind("entity instruction");
            throw new NotSupportedException(
                $"Instruction '{instruction.Name}' cannot preserve the compiled segmentation guarantee for " +
                $"entity '{typeof(TEntity).Name}'. Use the Entity query/write surface, or model a genuine " +
                "control-plane entity as host-scoped.");
        }
        await using var operationScope = await Guard(effect, "entity instruction", ct);
        if (_inner is IInstructionExecutor<TEntity> exec)
            return await exec.ExecuteAsync<TResult>(instruction, ct);
        throw new NotSupportedException($"Repository for {typeof(TEntity).Name} does not support instruction '{instruction.Name}'.");
    }

    private sealed class BatchFacade : IBatchSet<TEntity, TKey>
    {
        private readonly RepositoryFacade<TEntity, TKey> _outer;
        private readonly List<TEntity> _adds = new();
        private readonly List<TEntity> _updates = new();
        private readonly List<TKey> _deletes = new();
        private readonly List<(TKey id, Action<TEntity> mutate)> _mutations = new();
        private readonly List<(BatchOperation Operation, int LocalIndex)> _logicalOperations = new();
        private bool _savingOrSaved;

        public BatchFacade(RepositoryFacade<TEntity, TKey> outer) => _outer = outer;

        public IBatchSet<TEntity, TKey> Add(TEntity entity)
        {
            _logicalOperations.Add((BatchOperation.Add, _adds.Count));
            _adds.Add(entity);
            return this;
        }

        public IBatchSet<TEntity, TKey> Update(TEntity entity)
        {
            _logicalOperations.Add((BatchOperation.Update, _updates.Count));
            _updates.Add(entity);
            return this;
        }

        public IBatchSet<TEntity, TKey> Delete(TKey id)
        {
            _logicalOperations.Add((BatchOperation.Delete, _deletes.Count));
            _deletes.Add(id);
            return this;
        }

        public IBatchSet<TEntity, TKey> Update(TKey id, Action<TEntity> mutate)
        {
            _logicalOperations.Add((BatchOperation.Mutate, _mutations.Count));
            _mutations.Add((id, mutate));
            return this;
        }

        public IBatchSet<TEntity, TKey> Clear()
        {
            _adds.Clear();
            _updates.Clear();
            _deletes.Clear();
            _mutations.Clear();
            _logicalOperations.Clear();
            return this;
        }

        public async Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            await using var operationScope = await _outer.Guard(DataOperationEffect.Write, "entity batch", ct);
            var segmentation = operationScope.Segmentation;
            if (_savingOrSaved)
                throw new InvalidOperationException("A batch can be saved once. Create a new Entity batch for later work.");
            _savingOrSaved = true;

            var operationCount = checked(_adds.Count + _updates.Count + _mutations.Count + _deletes.Count);
            var explicitUpdateCount = _updates.Count;
            if (options?.MaxItems is <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "BatchOptions.MaxItems must be positive when specified.");
            if (options?.MaxItems is { } maxItems && operationCount > maxItems)
                throw new InvalidOperationException(
                    $"The batch contains {operationCount} operations, exceeding its explicit limit of {maxItems}. " +
                    "Split the work into smaller batches.");
            if (!string.IsNullOrWhiteSpace(options?.IdempotencyKey))
                throw new NotSupportedException(
                    "Entity batches do not yet expose a proved idempotency contract. Remove IdempotencyKey; a dispatched batch is never replayed.");

            // Create and qualify the native batch before deferred loads or Lifecycle callbacks. Construction is a
            // pure execution-plan step; a provider must not open a resource or dispatch work from CreateBatch().
            var native = _outer._inner.CreateBatch();
            var nativeCapabilities = native.ExecutionCapabilities;
            if (options?.RequireAtomic == true)
            {
                var advertised = DataCaps.Describe(_outer._inner, _outer._inner.GetType().Name)
                    .Has(DataCaps.Write.AtomicBatch);
                if (!advertised || !nativeCapabilities.HasFlag(BatchExecutionCapabilities.Atomic))
                    throw new NotSupportedException(
                        $"The adapter backing {typeof(TEntity).Name} does not expose a proved native atomic batch boundary. " +
                        "Remove RequireAtomic or route the Entity to an adapter that advertises and executes atomic batches.");
                if (_outer._deleteOverride is not null && _deletes.Count != 0)
                    throw new NotSupportedException(
                        $"Atomic batch removal is not available for soft-deleted entity '{typeof(TEntity).Name}'. " +
                        "Use a non-atomic batch or a native transaction that explicitly composes the updates.");
            }

            if (_mutations.Count != 0)
            {
                for (var mutationIndex = 0; mutationIndex < _mutations.Count; mutationIndex++)
                {
                    var (id, mutate) = _mutations[mutationIndex];
                    ct.ThrowIfCancellationRequested();
                    // Managed-scoped load (IDOR): a mutate-by-id never loads (and so never re-stamps) another scope's row.
                    var current = await _outer.ReadOne(id, segmentation, ct);
                    if (current is null)
                        throw new BatchMutationTargetNotFoundException(
                            typeof(TEntity).FullName ?? typeof(TEntity).Name,
                            mutationIndex);
                    mutate(current);
                    _updates.Add(current);
                }
            }

            var addContexts = new List<EntityLifecycleContext<TEntity>>(_adds.Count);
            var updateContexts = new List<EntityLifecycleContext<TEntity>>(_updates.Count);
            if (_outer._lifecycle is { HasUpsert: true })
            {
                foreach (var entity in _adds)
                    addContexts.Add(await _outer._lifecycle.BeginUpsert(
                        entity,
                        token => _outer.ReadPrior(entity, segmentation, token),
                        ct));
                foreach (var entity in _updates)
                    updateContexts.Add(await _outer._lifecycle.BeginUpsert(
                        entity,
                        token => _outer.ReadPrior(entity, segmentation, token),
                        ct));
            }

            var removeContexts = new List<EntityLifecycleContext<TEntity>>(_deletes.Count);
            if (_outer._lifecycle is { HasRemove: true })
            {
                foreach (var id in _deletes)
                {
                    var entity = await _outer.ReadOne(id, segmentation, ct);
                    if (entity is not null)
                        removeContexts.Add(await _outer._lifecycle.BeginRemove(entity, ct));
                }
            }

            var adds = addContexts.Count == 0 ? _adds : addContexts.Select(context => context.Current).ToList();
            var updates = updateContexts.Count == 0 ? _updates : updateContexts.Select(context => context.Current).ToList();
            var deletes = removeContexts.Count == 0 ? _deletes : removeContexts.Select(context => context.Current.Id).ToList();

            // A batch is still an Entity write: apply the same compiled identity/timestamp contributors as Upsert.
            foreach (var entity in adds) { ct.ThrowIfCancellationRequested(); _outer.WritePlanFor(entity).ApplyAll(entity); }
            foreach (var entity in updates) { ct.ThrowIfCancellationRequested(); _outer.WritePlanFor(entity).ApplyAll(entity); }

            // Field transform (ARCH-0098 Blocker 1): the native batch persists encrypted CLONES, so a batch write —
            // unlike a [Timestamp] stamp — never lands plaintext at rest. The caller's add/update instances stay plaintext.
            foreach (var entity in adds) native.Add(_outer.WritePayload(entity));
            foreach (var entity in updates) native.Update(_outer.WritePayload(entity));

            // A soft remove is an update, not a native delete. Lower it through the canonical remove path so
            // operation overrides, managed scope and Lifecycle retain exactly the same meaning as Entity.Remove().
            // This necessarily spans multiple writes; fail closed when the caller explicitly requires atomicity.
            var lowerSoftDeletes = _outer._deleteOverride is not null && deletes.Count != 0;
            if (!lowerSoftDeletes)
                foreach (var id in deletes) native.Delete(id);

            var values = _outer.CurrentManagedValues(segmentation);
            BatchResult result;
            if (values is null) result = await native.Save(options, ct);
            else
            {
                // Stamp-AND-verify the whole batch under one scope (all rows share the ambient managed value).
                using (ManagedFieldWriteScope.Enter(values))
                    result = await native.Save(options, ct);
            }

            if (result.Added < 0 || result.Updated < 0 || result.Deleted < 0)
                throw new BatchReceiptRejectedException(
                    typeof(TEntity).FullName ?? typeof(TEntity).Name,
                    "Affected counts cannot be negative.",
                    DataCommitOutcome.Unknown);
            if (result.CommitOutcome != DataCommitOutcome.Committed)
                throw new BatchReceiptRejectedException(
                    typeof(TEntity).FullName ?? typeof(TEntity).Name,
                    "A successfully returned batch must report CommitOutcome=Committed.",
                    result.CommitOutcome);
            if (options?.RequireAtomic == true && result.Atomicity != BatchAtomicity.Atomic)
                throw new BatchReceiptRejectedException(
                    typeof(TEntity).FullName ?? typeof(TEntity).Name,
                    "A required atomic execution must return Atomicity=Atomic.",
                    DataCommitOutcome.Unknown);
            if (result.HasCompleteItemOutcomes &&
                !nativeCapabilities.HasFlag(BatchExecutionCapabilities.CompleteItemOutcomes))
                throw new BatchReceiptRejectedException(
                    typeof(TEntity).FullName ?? typeof(TEntity).Name,
                    "Complete item outcomes were reported by a batch that did not qualify that execution seam.",
                    DataCommitOutcome.Unknown);
            if (nativeCapabilities.HasFlag(BatchExecutionCapabilities.CompleteItemOutcomes) && !lowerSoftDeletes)
            {
                ValidateCompleteOutcomes(
                    result,
                    adds.Count,
                    updates.Count,
                    lowerSoftDeletes ? 0 : deletes.Count);
                result = result with
                {
                    Items = RemapCompleteOutcomes(result.Items, explicitUpdateCount, adds.Count, updates.Count)
                };
            }

            var completedRemoves = removeContexts;
            if (lowerSoftDeletes)
            {
                completedRemoves = new List<EntityLifecycleContext<TEntity>>(removeContexts.Count);
                if (removeContexts.Count != 0)
                {
                    foreach (var context in removeContexts)
                        if (await _outer.DeleteWithoutLifecycle(context.Current.Id, segmentation, ct))
                            completedRemoves.Add(context);
                }
                else
                {
                    var deleted = 0;
                    foreach (var id in deletes)
                        if (await _outer.DeleteWithoutLifecycle(id, segmentation, ct))
                            deleted++;
                    result = result with
                    {
                        Deleted = deleted,
                        Atomicity = BatchAtomicity.NotGuaranteed,
                        HasCompleteItemOutcomes = false,
                        Items = Array.Empty<BatchItemResult>()
                    };
                }

                if (removeContexts.Count != 0)
                    result = result with
                    {
                        Deleted = completedRemoves.Count,
                        Atomicity = BatchAtomicity.NotGuaranteed,
                        HasCompleteItemOutcomes = false,
                        Items = Array.Empty<BatchItemResult>()
                    };
            }

            if (_outer._lifecycle is { } lifecycle)
            {
                for (var i = 0; i < addContexts.Count; i++)
                    await lifecycle.CompleteUpsert(addContexts[i], adds[i]);
                for (var i = 0; i < updateContexts.Count; i++)
                    await lifecycle.CompleteUpsert(updateContexts[i], updates[i]);
                foreach (var context in completedRemoves)
                    await lifecycle.CompleteRemove(context);
            }

            return result;
        }

        private IReadOnlyList<BatchItemResult> RemapCompleteOutcomes(
            IReadOnlyList<BatchItemResult> nativeItems,
            int explicitUpdateCount,
            int addCount,
            int updateCount)
        {
            var byNativeIndex = new BatchItemResult[nativeItems.Count];
            foreach (var item in nativeItems) byNativeIndex[item.Index] = item;

            var logical = new BatchItemResult[_logicalOperations.Count];
            for (var logicalIndex = 0; logicalIndex < _logicalOperations.Count; logicalIndex++)
            {
                var (operation, localIndex) = _logicalOperations[logicalIndex];
                var nativeIndex = operation switch
                {
                    BatchOperation.Add => localIndex,
                    BatchOperation.Update => addCount + localIndex,
                    BatchOperation.Mutate => addCount + explicitUpdateCount + localIndex,
                    BatchOperation.Delete => addCount + updateCount + localIndex,
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
                logical[logicalIndex] = new BatchItemResult(
                    logicalIndex,
                    operation,
                    byNativeIndex[nativeIndex].Outcome);
            }
            return logical;
        }

        private static void ValidateCompleteOutcomes(
            BatchResult result,
            int addCount,
            int updateCount,
            int deleteCount)
        {
            var nativeOperationCount = checked(addCount + updateCount + deleteCount);
            if (!result.HasCompleteItemOutcomes || result.Items.Count != nativeOperationCount)
                throw Rejected("A complete-outcome batch must return one item for every native operation.");

            var seen = new bool[nativeOperationCount];
            var added = 0;
            var updated = 0;
            var deleted = 0;
            foreach (var item in result.Items)
            {
                if (item.Index < 0 || item.Index >= nativeOperationCount || seen[item.Index])
                    throw Rejected("Batch item indexes must be unique, contiguous native-operation positions.");
                seen[item.Index] = true;
                if (item.Outcome == BatchItemOutcome.Unknown)
                    throw Rejected("A successful complete-outcome batch cannot contain Unknown item outcomes.");
                var expectedOperation = item.Index < addCount
                    ? BatchOperation.Add
                    : item.Index < addCount + updateCount
                        ? BatchOperation.Update
                        : BatchOperation.Delete;
                if (item.Operation != expectedOperation)
                    throw Rejected("Batch item operations must match the qualified native add/update/delete positions.");
                if (item.Outcome != BatchItemOutcome.Applied) continue;
                switch (item.Operation)
                {
                    case BatchOperation.Add: added++; break;
                    case BatchOperation.Update:
                    case BatchOperation.Mutate: updated++; break;
                    case BatchOperation.Delete: deleted++; break;
                }
            }

            if (added != result.Added || updated != result.Updated || deleted != result.Deleted)
                throw Rejected("Affected counts must equal the applied per-operation outcomes.");
            return;

            static BatchReceiptRejectedException Rejected(string correction)
                => new(
                    typeof(TEntity).FullName ?? typeof(TEntity).Name,
                    correction,
                    DataCommitOutcome.Unknown);
        }
    }

    /// <summary>
    /// Analytics composition forwards to the adapter repository that owns the mapping and dialect; the
    /// facade has no words of its own. Stores that cannot compose refuse with a corrective.
    /// </summary>
    bool Koan.Data.Abstractions.Analytics.IAnalyticsQueryComposer<TEntity>.TryCompose(
        Koan.Data.Abstractions.Analytics.AnalyticsQuestion question,
        IReadOnlyDictionary<string, object?>? parameterValues,
        out Koan.Data.Abstractions.Analytics.AnalyticsSql sql,
        out string? corrective)
    {
        if (_inner is Koan.Data.Abstractions.Analytics.IAnalyticsQueryComposer<TEntity> composer)
            return composer.TryCompose(question, parameterValues, out sql, out corrective);
        sql = null!;
        corrective =
            "Analytics questions need a record store that can compose aggregate asks. " +
            $"This entity is routed to '{_inner.GetType().Name}', which offers none. " +
            "Reference a relational connector (for example Sylin.Koan.Data.Connector.Sqlite) for the entity's store.";
        return false;
    }
}
