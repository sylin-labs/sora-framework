using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Security.Claims;
using Koan.Core;
using Koan.Web.Endpoints;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 — the non-generic marker for entity-access realizations. <see cref="KoanDiscoverableAttribute"/> so
/// implementers auto-register (no manual wiring), and exposes the principal-INDEPENDENT gate declaration the
/// singleton gate cache reads off a throwaway instance.
/// </summary>
[KoanDiscoverable]
public interface IEntityAccessRealization
{
    /// <summary>The gate declaration — principal-independent (structure only), safe to read on an unbound instance.</summary>
    AccessGate BuildGate();

    /// <summary>True when <c>Constrain(Create)</c> contributes a <c>Where</c> predicate but no <c>Stamp</c> — a
    /// likely footgun (a Where on create is a no-op, so a forged owner is NOT overwritten). Probed at boot.</summary>
    bool IsCreateUnstamped();
}

/// <summary>
/// SEC-0004 (§B, §D) — the fine-grained access realization for one entity, discovered automatically (derive it and
/// it registers). Declare ownership ONCE via <see cref="Owner"/> and scope rows with a <see cref="Constrain"/>
/// one-liner; optionally tighten the gate with <see cref="ReadGate"/>/<see cref="WriteGate"/>/<see cref="RemoveGate"/>
/// (or <see cref="ConfigureGate"/>). The single <see cref="Owner"/> predicate feeds Constrain's <c>Where</c>/<c>Stamp</c>,
/// the gate's owner term, and mass-delete bounding — one source of truth.
/// </summary>
/// <remarks>
/// TWO AXES, do not conflate: the GATE (<see cref="ReadGate"/>/<see cref="WriteGate"/>/<see cref="RemoveGate"/>) is
/// the coarse read/write/remove identity check and MUST be principal-independent (declarations only — never touch
/// <see cref="Principal"/>/<see cref="CurrentUserId"/>), because the singleton cache reads it once on an unbound
/// instance. The CONSTRAIN axis (<see cref="AccessAction"/> read/create/update/delete) is the per-request,
/// principal-bound row transform. The endpoint <see cref="Bind"/>s the principal before any Owner/Constrain runs.
/// <para>
/// Canonical realization (freeze-ownership by default — stamp on create AND update so a payload cannot reassign
/// the owner; a domain that wants owner transfer simply omits the update stamp):
/// <code>
/// protected override Expr&lt;Order&gt; Owner =&gt; o =&gt; o.CustomerId == CurrentUserId;
/// public override IAccessFilter&lt;Order&gt; Constrain(IAccessFilter&lt;Order&gt; q, AccessAction a) =&gt; a switch
/// {
///     AccessAction.Create =&gt; q.Stamp(o =&gt; o.CustomerId, CurrentUserId),
///     AccessAction.Update =&gt; q.Where(Owner).Stamp(o =&gt; o.CustomerId, CurrentUserId),
///     _ =&gt; q.Where(Owner),
/// };
/// </code>
/// </para>
/// </remarks>
public abstract class EntityAccess<TEntity> : IEntityAccessRealization
    where TEntity : class
{
    /// <summary>The current principal — anonymous until <see cref="Bind"/> runs (so an unbound instance is safe).</summary>
    protected ClaimsPrincipal Principal { get; private set; } = new();

    /// <summary>The request service provider (set by <see cref="Bind"/>).</summary>
    protected IServiceProvider? Services { get; private set; }

    /// <summary>The current user's stable id — <c>NameIdentifier</c> then <c>sub</c>. Override for a per-entity claim.</summary>
    protected virtual string? CurrentUserId
        => Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Principal.FindFirst("sub")?.Value;

    /// <summary>The ownership predicate, declared ONCE. Must be a simple comparison (<c>o.Field == value</c>) so it
    /// can be lowered to the unified filter for mass-delete pushdown. <c>null</c> = no ownership concept.</summary>
    protected virtual Expression<Func<TEntity, bool>>? Owner => null;

    /// <summary>SEC-0004 (§C): the bound <see cref="Owner"/> predicate, for the per-row projection's owner probe.
    /// Read after <see cref="Bind"/> so it closes over the current principal's <see cref="CurrentUserId"/>.</summary>
    internal Expression<Func<TEntity, bool>>? OwnerExpression => Owner;

    /// <summary>Scope rows / stamp the owner, per action. Default = no-op (all rows — allow-by-default). See the
    /// canonical example in the type remarks.</summary>
    public virtual IAccessFilter<TEntity> Constrain(IAccessFilter<TEntity> q, AccessAction action) => q;

    /// <summary>The read gate (principal-INDEPENDENT). Default open.</summary>
    protected virtual ActionGate ReadGate => ActionGate.Open;

    /// <summary>The write gate (principal-INDEPENDENT). Default open.</summary>
    protected virtual ActionGate WriteGate => ActionGate.Open;

    /// <summary>The remove gate (principal-INDEPENDENT). Default open.</summary>
    protected virtual ActionGate RemoveGate => ActionGate.Open;

    /// <summary>Assemble the whole-entity gate from the per-action overrides. Override directly for custom-verb gates
    /// (<see cref="AccessGate.Custom"/>). MUST stay principal-independent.</summary>
    protected virtual AccessGate ConfigureGate()
    {
        var byAction = new Dictionary<string, ActionGate>(StringComparer.OrdinalIgnoreCase);
        if (!ReadGate.IsOpen) byAction[EntityAuthorizeActions.Read] = ReadGate;
        if (!WriteGate.IsOpen) byAction[EntityAuthorizeActions.Write] = WriteGate;
        if (!RemoveGate.IsOpen) byAction[EntityAuthorizeActions.Remove] = RemoveGate;
        return byAction.Count == 0
            ? AccessGate.Open
            : new AccessGate(byAction, new Dictionary<string, ActionGate>(StringComparer.OrdinalIgnoreCase));
    }

    AccessGate IEntityAccessRealization.BuildGate() => ConfigureGate();

    bool IEntityAccessRealization.IsCreateUnstamped()
    {
        var filter = new AccessFilter<TEntity>();
        Constrain(filter, AccessAction.Create);
        return filter.Filter is not null && !filter.HasStamps;
    }

    /// <summary>Bind the per-request principal/services before Owner/Constrain run (called by the endpoint/hook).</summary>
    internal void Bind(EntityRequestContext context)
    {
        Principal = context.User;
        Services = context.Services;
    }
}
