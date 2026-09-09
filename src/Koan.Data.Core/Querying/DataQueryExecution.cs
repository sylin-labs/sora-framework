using Koan.Data.Abstractions;
using Koan.Data.Core.Execution;

namespace Koan.Data.Core.Querying;

/// <summary>
/// Keeps provider candidates and application-visible Entity materialization as distinct stages.
/// Repositories outside Data's facade remain self-contained and are assumed to own their materialization.
/// </summary>
internal static class DataQueryExecution<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    public static void ValidateEvidence(IQueryReadEvidence? evidence, IEnumerable<TEntity> rows)
    {
        if (evidence is not null && (!evidence.CoversRows(rows.Cast<object>()) || !evidence.IsCurrentScope()))
            throw new InvalidOperationException("Counterpart read evidence no longer covers the returned identities or Data scope. Keep load projections within the selected rows and scope.");
    }

    public static Task<RepositoryQueryResult<TEntity>> QueryCandidates(
        IDataRepository<TEntity, TKey> repository,
        IQueryRepository<TEntity, TKey> queryRepository,
        QueryDefinition query,
        CancellationToken ct)
        => repository is IDataQueryBoundary<TEntity, TKey> boundary
            ? boundary.QueryCandidates(query, ct)
            : queryRepository.Query(query, ct);

    public static Task<BoundedQueryResult<TEntity>> QueryBoundedCandidates(
        IDataRepository<TEntity, TKey> repository,
        IBoundedQueryRepository<TEntity, TKey> boundedRepository,
        QueryDefinition query,
        int maxCandidates,
        CancellationToken ct)
        => repository is IDataQueryBoundary<TEntity, TKey> boundary
            ? boundary.QueryBoundedCandidatesRaw(query, maxCandidates, ct)
            : boundedRepository.QueryBoundedCandidates(query, maxCandidates, ct);

    public static Task MaterializeVisible(
        IDataRepository<TEntity, TKey> repository,
        IReadOnlyList<TEntity> entities,
        CancellationToken ct)
        => repository is IDataQueryBoundary<TEntity, TKey> boundary
            ? boundary.MaterializeVisible(entities, ct)
            : Task.CompletedTask;

    public static ValueTask MaterializeVisible(
        IDataRepository<TEntity, TKey> repository,
        TEntity entity,
        CancellationToken ct)
        => repository is IDataQueryBoundary<TEntity, TKey> boundary
            ? boundary.MaterializeVisible(entity, ct)
            : ValueTask.CompletedTask;
}
