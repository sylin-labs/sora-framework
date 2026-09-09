namespace Koan.Data.Abstractions;

/// <summary>
/// Optional atomic insertion terminal. An existing physical identity, including an invisible row or
/// sibling Entity variant, must never be replaced. Unsupported identity shapes fail before dispatch.
/// </summary>
public interface IInsertOnlyRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    /// <summary>
    /// Returns Inserted/Committed with the submitted entity, or Conflict/NotCommitted with no entity
    /// on a proven identity collision. Generated identities are assigned to the submitted model.
    /// Other failures, including ambiguous commit, must not be reported as a collision.
    /// </summary>
    Task<MutationResult<TEntity, TKey>> Insert(TEntity model, CancellationToken ct = default);
}
