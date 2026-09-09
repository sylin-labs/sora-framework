using System;
using System.Linq.Expressions;
using Koan.Data.Abstractions.Filtering;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 (§B) — the query-transform builder an <see cref="EntityAccess{TEntity}"/> realization composes in its
/// <c>Constrain</c> one-liner. It ACCUMULATES (never executes a query): <c>Where</c> narrowing predicates
/// (AND-composed into the read query and used for the row-ownership check + mass-delete bounding) and
/// <see cref="Stamp{TProp}"/> server-truth owner writes (applied to the payload on create, and on update to freeze
/// ownership). The same declaration drives the collection filter, the out-of-scope-is-404 single fetch, and the
/// mass-operation bound — one mechanism, every surface, DB-pushable via the WEB-0068 rail.
/// </summary>
public interface IAccessFilter<TEntity>
{
    /// <summary>AND a narrowing predicate (scope rows / verify ownership). Returns <c>this</c> (fluent).</summary>
    IAccessFilter<TEntity> Where(Expression<Func<TEntity, bool>> predicate);

    /// <summary>Require the same entity identity in the selected partition to satisfy the predicate.
    /// Null inherits the current partition; an empty string selects the default partition. Read queries only.</summary>
    IAccessFilter<TEntity> Where(Expression<Func<TEntity, bool>> predicate, string? partition);

    /// <summary>Record a server-truth write of <paramref name="value"/> to the <paramref name="selector"/> member —
    /// applied to the payload on create (and on update to freeze ownership), overwriting any client-sent value.
    /// Returns <c>this</c> (fluent). A <see cref="Stamp{TProp}"/> on the read path contributes nothing.</summary>
    IAccessFilter<TEntity> Stamp<TProp>(Expression<Func<TEntity, TProp>> selector, TProp value);

    /// <summary>The complete normalized constraint. Null means no constraint.</summary>
    Filter? Filter { get; }
}
