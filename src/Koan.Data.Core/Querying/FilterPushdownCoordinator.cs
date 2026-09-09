using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Core.Sorting;

namespace Koan.Data.Core.Querying;

/// <summary>The finalized page of a query plus its true total cardinality.</summary>
public readonly record struct FinalizedQuery<TEntity>(
    IReadOnlyList<TEntity> Page,
    long TotalCount,
    bool IsEstimate,
    bool FellBackInMemory);

/// <summary>
/// The single owner of the partial-pushdown algorithm. An adapter is a translator + executor; this
/// coordinator is the orchestrator. It splits the caller's filter against the adapter's declared
/// <see cref="FilterSupport"/>, invokes the adapter with only the pushable portion, then applies
/// — in the only correctness-safe order — the residual filter, the unhandled sort, and pagination.
/// Pagination is applied <b>after</b> the residual, which structurally eliminates the relational
/// mis-pagination bug (old adapters paginated the unfiltered set, then filtered). Because this lives
/// in exactly one place, no adapter carries fallback logic.
/// </summary>
public static class FilterPushdownCoordinator
{
    /// <summary>
    /// Plan the adapter call for a query: the pushable-only definition to hand the adapter, and the
    /// residual the coordinator must evaluate afterwards. When a residual exists, pagination is stripped
    /// from the adapter definition (the page must be taken after the residual filter).
    /// </summary>
    public static (QueryDefinition AdapterQuery, Filter? Residual) Plan(QueryDefinition query, FilterSupport caps, Type entityType)
    {
        query = EnsureOrderForPage(query, entityType);
        if (query.Filter is null) return (query, null);

        var split = FilterSplitter.Split(query.Filter, caps, entityType);
        if (Filter.HasCounterpart(query.Filter) && split.Residual is not null)
            throw new NotSupportedException("Counterpart queries require native execution of the complete predicate. Use a supported connector, identity shape, and fully translatable filter.");
        var adapterQuery = query.Where(split.Pushable);
        if (split.Residual is not null)
            adapterQuery = adapterQuery
                .WithoutPagination()
                .WithCountStrategy(null);
        return (adapterQuery, split.Residual);
    }

    /// <summary>
    /// Supplies the total order a page is taken against.
    ///
    /// <para>A page is a window onto an order, so paging without one is not a weaker query — it is a
    /// meaningless one, and the store is free to return different rows for page two than the rows page one
    /// implied. Each adapter used to answer this privately, and one of the answers was
    /// <c>ORDER BY (SELECT NULL)</c>: enough to satisfy SQL Server's requirement that OFFSET have an ORDER BY,
    /// and no ordering at all.</para>
    ///
    /// <para>Naming a sort is not the same as naming a total order, and this is the half that was missed the
    /// first time. Ordering a page by Status, where a hundred rows share each Status, leaves the store free to
    /// break those ties differently on each request — so page two repeats and skips exactly as it would with no
    /// sort at all. The identity is therefore appended as a tiebreaker to every paginated read, not only to one
    /// the caller left unordered. MySQL was doing this privately and was the only store whose paged reads were
    /// stable over a non-unique key; the decision belongs here, once, for all of them.</para>
    ///
    /// <para>A caller's own keys are never displaced or reordered — the tiebreaker only settles rows they left
    /// equal, and is skipped when the caller already ordered by the identity, since a key cannot break its own
    /// ties.</para>
    ///
    /// <para>Only paginated queries pay for it. An unpaged read has no window to be a window of, so it keeps
    /// whatever order the store finds cheapest.</para>
    /// </summary>
    private static QueryDefinition EnsureOrderForPage(QueryDefinition query, Type entityType)
    {
        if (!query.HasPagination) return query;
        if (AggregateMetadata.GetIdSpec(entityType)?.Prop is not { } identity) return query;
        if (query.Sort.Any(spec => IsIdentity(spec, identity))) return query;
        var tiebreak = new SortSpec(
            new MemberPath(entityType, [identity], identity.PropertyType, traversesCollection: false, collectionSegmentIndex: -1),
            Desc: false);
        return query.WithSort(query.HasSort ? [.. query.Sort, tiebreak] : [tiebreak]);
    }

    private static bool IsIdentity(SortSpec spec, System.Reflection.PropertyInfo identity) =>
        spec.Path.Members.Count == 1 &&
        string.Equals(spec.Path.Members[0].Name, identity.Name, StringComparison.Ordinal);

    /// <summary>
    /// Finalize an adapter result against the original query and the planned residual: apply the residual
    /// filter, finish any sort the adapter didn't handle, recount when the residual changed the set, then
    /// paginate after — unless the adapter already paginated natively (only valid when there was no residual).
    /// </summary>
    public static FinalizedQuery<TEntity> Finalize<TEntity>(
        QueryDefinition query,
        QueryDefinition adapterQuery,
        Filter? residual,
        RepositoryQueryResult<TEntity> adapter)
    {
        QueryReceiptValidator.Validate(adapterQuery, adapter);
        IReadOnlyList<TEntity> items = adapter.Items;
        var residualApplied = false;

        if (residual is not null)
        {
            var predicate = InMemoryFilterEvaluator.Compile<TEntity>(residual);
            items = items.Where(predicate).ToList();
            residualApplied = true;
        }

        var sortFallback = query.HasSort && !adapter.SortFullyHandled(query);
        if (sortFallback)
            items = InMemorySorter.Apply(items, query.Sort);

        long total;
        var isEstimate = adapter.IsEstimate;
        if (!residualApplied && adapter.TotalCount is { } adapterTotal)
        {
            total = adapterTotal;
        }
        else
        {
            total = items.Count;
            isEstimate = false;
        }

        var page = items;
        if (query.HasPagination && !adapter.PaginationHandled)
        {
            var pageSize = query.EffectivePageSize();
            var skip = query.EffectiveOffset();
            page = items.Skip(skip).Take(pageSize).ToList();
        }

        var paginationFallback = query.HasPagination && !adapter.PaginationHandled;
        var fellBack = residualApplied || sortFallback || paginationFallback;
        // An adapter that answered by holding everything in memory reports the same thing a fallback does —
        // this read was not bounded — even though no axis fell back, because it applied every axis itself.
        // Reporting only the coordinator's own fallbacks left the floors invisible.
        if (fellBack || adapter.MaterializedAllCandidates)
            QueryFallbackFacts.Record<TEntity>(
                residualApplied, sortFallback, paginationFallback, adapter.MaterializedAllCandidates);
        return new FinalizedQuery<TEntity>(page, total, isEstimate, fellBack);
    }
}
