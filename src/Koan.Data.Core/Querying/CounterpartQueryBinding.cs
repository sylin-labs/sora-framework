using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core.Pipeline;
using Koan.Data.Core.Semantics;

namespace Koan.Data.Core;

internal sealed partial class RepositoryFacade<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private async Task<TResult> ExecuteCounterpartRead<TResult>(
        QueryDefinition query, DataSegmentationBinding segmentation,
        Func<BoundCounterpartRead, Task<TResult>> execute, CancellationToken ct)
    {
        query = query with { Filter = Filter.Snapshot(query.Filter), Sort = query.Sort.ToArray() };
        var capabilities = DataCaps.Describe(_inner, _inner.GetType().Name);
        var support = capabilities.Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;
        if (!support.SupportsSameIdIn || _inner is not ICounterpartQueryRepository native)
            throw new NotSupportedException("The selected connector cannot execute native counterpart predicates.");
        if (_routeBinding is null || _resolveRoute is null)
            throw new NotSupportedException("Counterpart reads require a host-bound Data route.");

        var outer = CaptureCounterpartScope(segmentation);
        if (outer.Filter is not null) RequireScopeForRead(outer.Filter);
        var outerPartition = EntityContext.Current?.Partition ?? string.Empty;
        var stamps = new List<CounterpartScopeStamp>
        {
            new(outerPartition, outer, native.BindCounterpartTarget())
        };
        var full = Filter.And(query.Filter, outer.Filter)!;
        var nodes = new List<SameIdInFilter>();
        Collect(full, nodes);
        var replacements = new Dictionary<SameIdInFilter, BoundSameIdInFilter>(SameCounterpartComparer.Instance);
        var leases = new List<GuardedOperation>();
        try
        {
            foreach (var group in nodes.Distinct(SameCounterpartComparer.Instance).GroupBy(
                node => node.Partition is null ? outerPartition : string.IsNullOrWhiteSpace(node.Partition) ? string.Empty : node.Partition,
                StringComparer.Ordinal))
            {
                if (group.Any(node => node.EntityType != typeof(TEntity) || Filter.HasCounterpart(node.Predicate)))
                    throw new NotSupportedException("Counterpart queries require the same Entity type and cannot contain nested counterparts.");
                using var targetContext = EntityContext.With(partition: group.Key);
                var targetRoute = _resolveRoute();
                if (targetRoute is null || targetRoute.RepositoryIdentity != _routeBinding.RepositoryIdentity ||
                    targetRoute.Plan.RouteIdentity != _routeBinding.Plan.RouteIdentity)
                    throw new NotSupportedException("A counterpart partition cannot change the resolved Data source, adapter, or route generation.");
                var lease = await Guard(DataOperationEffect.Read, "entity counterpart read", ct, ensureReadiness: false);
                leases.Add(lease);
                var target = CaptureCounterpartScope(lease.Segmentation);
                if (outer.Bindings.Count != target.Bindings.Count || outer.Bindings.Any(pair =>
                        !target.Bindings.TryGetValue(pair.Key, out var value) || !Filter.EquivalentValue(pair.Value, value)))
                    throw new NotSupportedException("A counterpart partition cannot change isolation bindings. Keep the same tenant and managed scope for both operands.");
                if (target.Filter is not null) RequireScopeForRead(target.Filter);
                var targetBinding = native.BindCounterpartTarget();
                stamps.Add(new(EntityContext.Current?.Partition ?? string.Empty, target, targetBinding));
                foreach (var node in group)
                {
                    var predicate = Filter.And(node.Predicate, target.Filter)!;
                    if (Filter.HasCounterpart(predicate) || FilterSplitter.Split(predicate, support, typeof(TEntity)).Residual is not null)
                        throw new NotSupportedException("The complete counterpart predicate and read scopes must be natively translatable without nested counterparts.");
                    replacements.Add(node, new BoundSameIdInFilter(predicate, targetBinding));
                }
            }

            var bound = Rewrite(full, replacements);
            if (FilterSplitter.Split(bound, support, typeof(TEntity)).Residual is not null)
                throw new NotSupportedException("The complete counterpart query must be native; CLR residuals are not supported.");
            return await execute(new BoundCounterpartRead(query.Where(bound), outerPartition,
                () => CounterpartScopeStillMatches(stamps, native)));
        }
        finally
        {
            DisposeCounterpartLeases(leases);
        }
    }

    private CounterpartScope CaptureCounterpartScope(DataSegmentationBinding segmentation)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (segmentation.Values is not null)
            foreach (var pair in segmentation.Values) values.Add("segmentation:" + pair.Key, pair.Value);
        Filter? result = segmentation.ReadFilter;
        foreach (var contributor in _readContributors)
        {
            if (contributor is not ManagedEqualityReadContributor)
            {
                result = Filter.And(result, contributor.ReadFilter(typeof(TEntity)));
                continue;
            }
            foreach (var descriptor in _managed.Where(static field => field.AutoReadFilter))
            {
                var value = descriptor.ValueProvider();
                if (value is not null && value is not string && !value.GetType().IsValueType)
                    throw new NotSupportedException("Counterpart isolation requires immutable scalar managed equality bindings.");
                values.Add("managed:" + descriptor.StorageName, value);
                if (value is not null) result = Filter.And(result, Filter.Eq(descriptor.StorageName, value));
            }
        }
        return new(Filter.Snapshot(result, requireImmutableValues: true), values);
    }

    private sealed record CounterpartScope(Filter? Filter, Dictionary<string, object?> Bindings);
    private sealed record CounterpartScopeStamp(string Partition, CounterpartScope Scope, CounterpartQueryTarget Target);

    private sealed class SameCounterpartComparer : IEqualityComparer<SameIdInFilter>
    {
        public static SameCounterpartComparer Instance { get; } = new();
        public bool Equals(SameIdInFilter? left, SameIdInFilter? right) => Filter.Equivalent(left, right);
        public int GetHashCode(SameIdInFilter node) => HashCode.Combine(node.EntityType, node.Partition);
    }

    private bool CounterpartScopeStillMatches(List<CounterpartScopeStamp> stamps, ICounterpartQueryRepository native)
    {
        foreach (var stamp in stamps)
        {
            using var context = EntityContext.With(partition: stamp.Partition);
            if (_resolveRoute!()?.RepositoryIdentity != _routeBinding!.RepositoryIdentity) return false;
            var scope = CaptureCounterpartScope(_segmentation.Bind("entity counterpart evidence"));
            if (!Equals(native.BindCounterpartTarget(), stamp.Target) ||
                !Filter.Equivalent(scope.Filter, stamp.Scope.Filter) ||
                scope.Bindings.Count != stamp.Scope.Bindings.Count || scope.Bindings.Any(pair =>
                    !stamp.Scope.Bindings.TryGetValue(pair.Key, out var value) || !Filter.EquivalentValue(pair.Value, value))) return false;
        }
        return true;
    }

    private static void Collect(Filter filter, List<SameIdInFilter> nodes)
    {
        switch (filter)
        {
            case SameIdInFilter same: nodes.Add(same); break;
            case BoundSameIdInFilter: throw new NotSupportedException("Only Data may bind counterpart query targets.");
            case AllOf all: foreach (var operand in all.Operands) Collect(operand, nodes); break;
            case AnyOf any: foreach (var operand in any.Operands) Collect(operand, nodes); break;
            case Not not: Collect(not.Operand, nodes); break;
        }
    }

    private static Filter Rewrite(Filter filter, Dictionary<SameIdInFilter, BoundSameIdInFilter> replacements)
        => filter switch
        {
            SameIdInFilter same => replacements[same],
            AllOf all => new AllOf(all.Operands.Select(operand => Rewrite(operand, replacements)).ToArray()),
            AnyOf any => new AnyOf(any.Operands.Select(operand => Rewrite(operand, replacements)).ToArray()),
            Not not => new Not(Rewrite(not.Operand, replacements)),
            _ => filter
        };

    // GuardedOperation forwards the synchronous DataOperationLease disposal. Keep stack changes in
    // this caller's execution context, including when a target bind fails halfway through the tree.
    private static void DisposeCounterpartLeases(List<GuardedOperation> leases)
    {
        for (var i = leases.Count - 1; i >= 0; i--) leases[i].DisposeAsync().GetAwaiter().GetResult();
    }

    private sealed class BoundCounterpartRead(QueryDefinition query,
        string partition, Func<bool> isCurrentScope)
    {
        public QueryDefinition Query { get; } = query;
        public IQueryReadEvidence Evidence(IReadOnlyList<TEntity> rows)
            => new CounterpartReadEvidence(partition, rows, isCurrentScope);
    }

    private sealed class CounterpartReadEvidence(string partition,
        IReadOnlyList<TEntity> rows, Func<bool> isCurrentScope) : IQueryReadEvidence
    {
        private readonly Dictionary<object, TKey> _rows = rows.ToDictionary(row => (object)row, row => row.Id, ReferenceEqualityComparer.Instance);
        public Type EntityType => typeof(TEntity);
        public Type KeyType => typeof(TKey);
        public string? Partition => partition;
        public bool CoversRows(IEnumerable<object> candidates)
            => candidates.All(row => row is TEntity entity && _rows.TryGetValue(row, out var id) &&
                EqualityComparer<TKey>.Default.Equals(id, entity.Id));
        public bool IsCurrentScope() => isCurrentScope();
    }
}
