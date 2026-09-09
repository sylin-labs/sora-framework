using System.Collections.Frozen;
using Koan.Data.Abstractions.Sorting;

namespace Koan.Data.Abstractions;

/// <summary>
/// A repository query result enriched with per-axis metadata describing what the adapter
/// actually pushed down. Materialized-result coordination uses these flags to evaluate an unpushed
/// filter, finish sort, paginate, or project in the correctness-safe order. Provider-bounded stream
/// coordination instead requires provider-handled candidate pagination and total ordering, then
/// evaluates any residual pointwise before requesting the next candidate page.
/// </summary>
public sealed class RepositoryQueryResult<TEntity>
{
    /// <summary>Data-owned evidence attached after native counterpart execution.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Text.Json.Serialization.JsonIgnore]
    [System.Runtime.Serialization.IgnoreDataMember]
    public IQueryReadEvidence? ReadEvidence { get; set; }
    public static readonly IReadOnlySet<SortSpec> NoSortHandled = FrozenSet<SortSpec>.Empty;

    /// <summary>Items returned by the adapter, in the order it intends (sorted iff it pushed sort down).</summary>
    public required IReadOnlyList<TEntity> Items { get; init; }

    /// <summary>True only when the provider applied the complete filter present in its query definition.</summary>
    public bool FilterHandled { get; init; }

    /// <summary>
    /// Total cardinality of the unpaginated result when the query requested a count and the adapter
    /// supplied one; null when no count was requested or no total is available.
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>True when <see cref="TotalCount"/> is approximate (e.g. from table stats).</summary>
    public bool IsEstimate { get; init; }

    /// <summary>The count work actually performed; <see cref="CountExecutionKind.None"/> when no total was produced.</summary>
    public CountExecutionKind CountExecution { get; init; }

    /// <summary>Sort specs the adapter pushed down. Empty means none — coordinator sorts in memory.</summary>
    public IReadOnlySet<SortSpec> SortHandled { get; init; } = NoSortHandled;

    /// <summary>True when the adapter applied pagination server-side.</summary>
    public bool PaginationHandled { get; init; }

    /// <summary>
    /// True when the adapter reached this answer by holding the whole candidate set in memory.
    ///
    /// <para>The axes above report <i>who</i> applied each part of the query, and for most adapters that also
    /// answers the question underneath it — the store applied the filter, so the work stayed in the store.
    /// For an adapter with no query engine it does not. The key-value and JSON floors load every record and
    /// use the framework's own evaluator and sorter, then report each axis as handled, because from the
    /// coordinator's side the work is genuinely done and no fallback is needed.</para>
    ///
    /// <para>Both readings are true, which is the problem: an application is not asking who did the work, it
    /// is asking whether answering cost a bounded amount of it. That question had no field, so the in-memory
    /// fallback recorded at the coordinator was structurally blind to exactly the adapters where everything
    /// is in memory. This is the missing half — an adapter that absorbs the work says so, and the guarantee
    /// stops depending on which layer happened to perform it.</para>
    /// </summary>
    public bool MaterializedAllCandidates { get; init; }

    /// <summary>True when <see cref="SortHandled"/> covers every spec in the query.</summary>
    public bool SortFullyHandled(QueryDefinition query)
    {
        if (query.Sort.Count == 0) return true;
        if (SortHandled.Count == 0) return false;
        foreach (var spec in query.Sort)
            if (!SortHandled.Contains(spec)) return false;
        return true;
    }
}
