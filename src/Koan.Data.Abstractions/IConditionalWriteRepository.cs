using Koan.Data.Abstractions.Filtering;

namespace Koan.Data.Abstractions;

/// <summary>
/// Optimistic-concurrency / atomic-claim capability (JOBS-0005 §20.3): replace an entity by Id <em>if and only if</em>
/// the currently-stored row still matches a guard predicate — a compare-and-set. Declared by adapters that can do this
/// atomically (relational conditional <c>UPDATE … WHERE Id = … AND &lt;guard&gt;</c>; Mongo <c>ReplaceOne</c> with a
/// guarded filter). Probed via <see cref="Capabilities.DataCaps.Write.ConditionalReplace"/>. Required conditional
/// intent rejects unsupported adapters; ordinary read-then-save is not a substitute for this guarantee.
/// </summary>
public interface IConditionalWriteRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    /// <summary>
    /// Atomically replace the stored row for <c>model.Id</c> with <paramref name="model"/>, only if the row currently
    /// in the store still matches <paramref name="guard"/>. The guard is evaluated against the <em>stored</em> row
    /// (its pre-update state). Returns <c>true</c> if the replace applied; <c>false</c> if the guard no longer held
    /// (another writer won the race — the store is unchanged).
    /// </summary>
    Task<bool> ConditionalReplaceAsync(TEntity model, Filter guard, CancellationToken ct = default);
}
