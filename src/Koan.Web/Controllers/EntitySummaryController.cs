using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Mvc;

namespace Koan.Web.Controllers;

/// <summary>
/// Controller variant of <see cref="EntityController{TEntity}"/> that projects collection responses
/// to <typeparamref name="TSummary"/> by default. Single-item GETs (<c>GET /{id}</c>) and write
/// endpoints stay on the full <typeparamref name="TEntity"/>; only list-style reads are projected.
/// </summary>
/// <remarks>
/// <para>
/// Use this when the catalog list view needs only a subset of the full entity's fields — for
/// instance a row-based browser that doesn't need <c>Description</c>, embedded change history,
/// or denormalized duplicate links. The detail page still hits <c>GET /{id}</c> and gets the
/// full object.
/// </para>
/// <para>
/// Pass <c>?view=full</c> on the list request to opt out of projection and receive the underlying
/// <typeparamref name="TEntity"/> collection — useful for admin UIs that want everything in one
/// payload.
/// </para>
/// <para>
/// <typeparamref name="TSummary"/> is a static-abstract <see cref="IProjectionOf{TEntity, TSelf}"/>
/// implementer; one <c>From(TEntity)</c> method per summary type is the only ceremony required.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The underlying entity exposed by the inherited CRUD surface.</typeparam>
/// <typeparam name="TSummary">The projection type returned by the list endpoint.</typeparam>
public abstract class EntitySummaryController<TEntity, TSummary> : EntityController<TEntity>
    where TEntity : class, Koan.Data.Abstractions.IEntity<string>
    where TSummary : IProjectionOf<TEntity, TSummary>
{
    /// <summary>Query-string flag that bypasses projection and returns the full entity collection.</summary>
    public const string FullViewToken = "full";

    /// <inheritdoc />
    protected override string DefaultCollectionView => "summary";

    /// <inheritdoc />
    public override async Task<IActionResult> GetCollection(CancellationToken ct)
    {
        var result = await base.GetCollection(ct);
        if (!ShouldProject(result)) return result;
        var objResult = (ObjectResult)result;
        objResult.Value = ProjectCollection(objResult.Value!);
        return objResult;
    }

    private bool ShouldProject(IActionResult result)
    {
        if (result is not ObjectResult obj || obj.Value is null) return false;
        // Caller can opt out via ?view=full.
        if (HttpContext is { } ctx && ctx.Request.Query.TryGetValue("view", out var v) &&
            string.Equals(v.ToString(), FullViewToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // Only project when the payload is a flat sequence of TEntity. Custom shapes
        // (?shape=map | ?shape=dict | hook-supplied wrappers) pass through untouched.
        return obj.Value is IEnumerable<TEntity>;
    }

    private static IReadOnlyList<TSummary> ProjectCollection(object payload)
    {
        var entities = (IEnumerable<TEntity>)payload;
        return entities.Select(TSummary.From).ToList();
    }
}
