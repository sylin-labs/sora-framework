using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Koan.Web.Hooks;
using Koan.Data.Abstractions.Filtering;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 (§B) — the WHOLE read-path Constrain wiring. A framework-owned open-generic
/// <see cref="IRequestOptionsHook{TEntity}"/> that, for any entity with an <see cref="EntityAccess{TEntity}"/>
/// realization, contributes <c>Constrain(_, Read)</c>'s narrowing predicates onto the WEB-0068 predicate rail
/// (<c>QueryOptions.Filter</c>). The collection/by-id/query paths AND-compose it exactly like a hand-written
/// visibility hook, and because <c>GovernedRelationshipExpander</c> re-runs <c>BuildOptions</c> per related type,
/// <c>?with=all</c> per-type narrowing lights up with zero extra wiring. No realization → no predicate →
/// byte-identical to today (the backward-compat contract that keeps the WEB-0068 regression suites green).
/// </summary>
internal sealed class EntityAccessConstrainHook<TEntity> : IRequestOptionsHook<TEntity>
    where TEntity : class
{
    // After user hooks. AND-composition is order-independent; last keeps the visibility floor visually last.
    public int Order => int.MaxValue;

    public Task OnBuildingOptions(HookContext<TEntity> ctx, QueryOptions opts)
    {
        var access = ctx.Services.GetService<EntityAccess<TEntity>>();
        if (access is null) return Task.CompletedTask;

        access.Bind(ctx.Request); // principal from EntityRequestContext.User (cross-surface; never IHttpContextAccessor)
        var filter = new AccessFilter<TEntity>();
        access.Constrain(filter, AccessAction.Read);
        opts.Filter = Filter.And(opts.Filter, filter.Filter);
        return Task.CompletedTask;
    }
}
