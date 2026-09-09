using System;
using System.Collections.Generic;

namespace Koan.Data.Core;

public sealed class QueryResult<TEntity>
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Text.Json.Serialization.JsonIgnore]
    [System.Runtime.Serialization.IgnoreDataMember]
    public Koan.Data.Abstractions.IQueryReadEvidence? ReadEvidence { get; init; }
    public required IReadOnlyList<TEntity> Items { get; init; }
    public required long TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public bool RepositoryHandledPagination { get; init; }
    public bool ExceededSafetyLimit { get; init; }
    public bool IsEstimate { get; init; }

    public long TotalPages => PageSize <= 0 ? 0 : (long)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}
