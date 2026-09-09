namespace Koan.Data.Abstractions;

/// <summary>Result of a provider-enforced bounded candidate read.</summary>
public sealed record BoundedQueryResult<TEntity>(
    IReadOnlyList<TEntity> Items,
    int CandidatesExamined,
    bool CandidateLimitExceeded)
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Text.Json.Serialization.JsonIgnore]
    [System.Runtime.Serialization.IgnoreDataMember]
    public IQueryReadEvidence? ReadEvidence { get; init; }
}
