using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Koan.Web.Hooks;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 (§A) — the built-in per-action gate rung. Looks up the precompiled <see cref="AccessGate"/> for the
/// resource entity type (which already folds in <c>[Access]</c>, lowered legacy floor sugar, and — Slice B — an
/// <c>EntityAccess&lt;T&gt;</c> realization) and evaluates the bag for <see cref="AuthorizeRequest.Action"/>. An
/// OPEN action defers (<c>null</c>) so allow-by-default and higher rungs are preserved; a declared action returns a
/// concrete <see cref="AuthorizeDecision"/>. Registered by default in <c>Koan.Web</c> at <c>Order = 50</c>; richer
/// rungs (RBAC floor, named-policy, PDP/ReBAC) stack around it via <see cref="IAuthorizationProvider"/>.
/// </summary>
/// <remarks>
/// At the coarse gate no row is loaded (the resource is the entity <see cref="Type"/>), so an <c>owner</c> term
/// degrades to <c>authenticated</c> — "signed in, so they might own some row." Slice B's Constrain narrows the
/// actual rows; Slice C's per-row projection re-evaluates ownership per item. This keeps the gate from 403-ing a
/// legitimate owner before we know which rows they own (the inverse of the "rules are not filters" footgun).
/// </remarks>
public sealed class EntityFloorAuthorizationProvider : IAuthorizationProvider
{
    private readonly IAccessGateCache _cache;
    private readonly IAgentGrantStore? _grants;

    public EntityFloorAuthorizationProvider(IAccessGateCache cache, IAgentGrantStore? grants = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _grants = grants; // SEC-0005: null = no server-side grants (backward-compatible)
    }

    /// <summary>After the RBAC floor (0), before named-policy (100): the entity gate is authoritative for the
    /// authority it understands (the <c>[Access]</c> declaration), deferring policy/PDP to higher rungs.</summary>
    public int Order => 50;

    public async Task<AuthorizeDecision?> EvaluateAsync(AuthorizeRequest request, CancellationToken ct = default)
    {
        if (request.Resource is not Type entityType)
        {
            return null; // the gate only understands entity-typed resources
        }

        var actionGate = _cache.GetOrCompile(entityType).For(request.Action);
        if (actionGate.IsOpen)
        {
            return null; // open action → defer (allow-by-default preserved)
        }

        // A null subject (reflection-built request / misbehaving provider) is treated as anonymous, never NRE.
        var subject = request.Subject ?? new ClaimsPrincipal(new ClaimsIdentity());
        return await EvaluateGate(actionGate, entityType, subject, _grants, ct).ConfigureAwait(false);
    }

    /// <summary>The shared gate/grant fallback for an entity action or an additional property restriction.</summary>
    internal static async Task<AuthorizeDecision> EvaluateGate(ActionGate actionGate, Type entityType,
        ClaimsPrincipal subject, IAgentGrantStore? grants, CancellationToken ct)
    {
        // owner degrades to authenticated at the coarse gate (no row to bind).
        var ownerAtGate = subject.Identity?.IsAuthenticated == true;
        var decision = AccessGateEvaluator.Evaluate(actionGate, subject, ownerSatisfied: ownerAtGate);
        if (decision is AuthorizeDecision.Allow)
        {
            return decision; // the token alone satisfies the gate — the common path never loads grants
        }

        // SEC-0005: the token was denied — a server-side AgentGrant may still unlock it. Load the subject's active
        // grants for THIS resource, materialize them as scoped effective-claims, and re-evaluate the SAME gate (so a
        // grant composes with the bag logic / origin / Constrain, never a per-transport bypass). Anonymous = no id =
        // no grants. The common Allow path above never reaches here, so the lookup is the slow-path-only cost.
        var subjectId = AuthSubject.Id(subject);
        if (grants is not null && subjectId is not null)
        {
            var caps = await grants.ActiveCapabilities(subjectId, entityType.Name, ct).ConfigureAwait(false);
            if (caps.Count > 0)
            {
                var granted = GrantClaims.Enrich(subject, caps);
                var regrant = AccessGateEvaluator.Evaluate(actionGate, granted, ownerSatisfied: granted.Identity?.IsAuthenticated == true);
                if (regrant is AuthorizeDecision.Allow) return regrant;
            }
        }

        return decision; // the original (token-only) denial stands
    }
}
