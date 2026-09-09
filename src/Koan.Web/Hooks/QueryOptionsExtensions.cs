using System.Linq.Expressions;
using Koan.Data.Abstractions.Filtering;

namespace Koan.Web.Hooks;

/// <summary>
/// Typed helpers for contributing to <see cref="QueryOptions"/> from hook code. The extension
/// methods lower each lambda immediately into the complete normalized filter.
/// </summary>
public static class QueryOptionsExtensions
{
    /// <summary>
    /// Append a server-side predicate to be AND-composed with the user's <c>?filter=</c> at
    /// query-execution time. Use from <see cref="IRequestOptionsHook{TEntity}"/> to enforce
    /// visibility, tenancy, soft-delete, or any other invariant the framework must apply on every
    /// read regardless of client input. See WEB-0068.
    /// </summary>
    public static void AddPredicate<TEntity>(
        this QueryOptions options,
        Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(predicate);
        options.Filter = Filter.And(options.Filter, Filter.Snapshot(LinqFilterCompiler.Compile(predicate)));
    }
}
