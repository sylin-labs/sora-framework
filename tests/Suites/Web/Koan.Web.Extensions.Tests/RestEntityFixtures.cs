using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Core.Model;
using Koan.Web.Authorization;
using Koan.Web.Controllers;
using Koan.Web.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koan.Web.Extensions.Tests;

// ARCH-0092 Phase 2 — terse [RestEntity] entities + one explicit-controller entity for the precedence test.
// Short [StorageName]s keep the in-memory store key tidy (the fully-qualified type name carries dots).

/// <summary>Terse default — CRUD auto-exposed at <c>api/trinket</c> (kebab of the type name).</summary>
[RestEntity]
[StorageName("rest_trinkets")]
public sealed class Trinket : Entity<Trinket>
{
    public string Name { get; set; } = "";
}

/// <summary>Lifecycle parity probe for the generated REST path.</summary>
[RestEntity]
[StorageName("rest_lifecycle_widgets")]
public sealed class LifecycleWidget : Entity<LifecycleWidget>
{
    public string Name { get; set; } = "";
}

/// <summary>Terse with an explicit route override — CRUD at <c>api/gizmos</c>, not <c>api/gizmo</c>.</summary>
[RestEntity("api/gizmos")]
[StorageName("rest_gizmos")]
public sealed class Gizmo : Entity<Gizmo>
{
    public string Label { get; set; } = "";
}

/// <summary>
/// Precedence: carries <c>[RestEntity]</c> AND a hand-written <see cref="CogController"/>. The explicit
/// controller must win — <c>api/cogs</c> is live and the terse <c>api/cog</c> is never registered.
/// </summary>
[RestEntity]
[StorageName("rest_cogs")]
public sealed class Cog : Entity<Cog>
{
    public string Teeth { get; set; } = "";
}

[ApiController]
[Route("api/cogs")]
public sealed class CogController : EntityController<Cog>
{
}

/// <summary>
/// ARCH-0092 Phase 3.2 — a gated entity. <c>[RequireScope]</c> is enforced by the unified seam inside the
/// shared <c>EntityEndpointService</c>, so the same declaration that gates REST also gates the MCP edge.
/// </summary>
[RestEntity]
[RequireScope("vault:read")]
[StorageName("rest_vaults")]
public sealed class Vault : Entity<Vault>
{
    public string Secret { get; set; } = "";
}

/// <summary>
/// SEC-0004 — a PER-ACTION gate: read and write require DIFFERENT scopes — a shape the entity-wide
/// <c>[RequireScope]</c> floor structurally could not express. Drives the per-action e2e proof.
/// </summary>
[RestEntity]
[Access(read: "has:scope:strongbox:read", write: "has:scope:strongbox:write")]
[StorageName("rest_strongboxes")]
public sealed class Strongbox : Entity<Strongbox>
{
    public string Contents { get; set; } = "";
}

/// <summary>
/// SEC-0004 — read + write open, delete admin-only. Drives the <c>Koan-Access</c> projection list header
/// (a non-admin sees <c>read, write</c>; an admin sees <c>read, write, remove</c>).
/// </summary>
[RestEntity]
[Access(remove: "is:admin")]
[StorageName("rest_ledgers")]
public sealed class Ledger : Entity<Ledger>
{
    public string Entry { get; set; } = "";
}

/// <summary>
/// SEC-0005 — an <c>[Audit]</c> entity: every successful write/remove writes one <see cref="AgentAction"/> through
/// the normal entity path. Open access (no <c>[Access]</c>) so the audit, not the gate, is what the spec exercises.
/// </summary>
[RestEntity]
[Audit]
[StorageName("rest_audited")]
public sealed class Audited : Entity<Audited>
{
    public string Note { get; set; } = "";
}

/// <summary>
/// SEC-0004 Slice B — an OWNED entity. <see cref="MemoAccess"/> scopes every row to its owner: reads narrow,
/// create stamps server-truth, update freezes ownership, delete/mass-delete are bounded.
/// </summary>
[RestEntity]
[StorageName("rest_memos")]
public sealed class Memo : Entity<Memo>
{
    public string? OwnerId { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>The realization: <see cref="Owner"/> declared once; create stamps, update verifies + freezes, read/delete narrow.</summary>
public sealed class MemoAccess : EntityAccess<Memo>
{
    protected override Expression<Func<Memo, bool>>? Owner => m => m.OwnerId == CurrentUserId;

    public override IAccessFilter<Memo> Constrain(IAccessFilter<Memo> q, AccessAction action) => action switch
    {
        AccessAction.Create => q.Stamp(m => m.OwnerId, CurrentUserId),
        AccessAction.Update => q.Where(Owner!).Stamp(m => m.OwnerId, CurrentUserId),
        _ => q.Where(Owner!),
    };
}

/// <summary>
/// SEC-0004 Slice C — PUBLIC read, owner-only write/remove. The read Constrain is OPEN, so a collection returns
/// every principal's rows; the per-row <c>can</c> projection then DIFFERS by row (own rows advertise
/// <c>read, write, remove</c>; others advertise <c>read</c> only). That divergence is the projection's reason to
/// exist — it makes allow-by-default honest.
/// </summary>
[RestEntity]
[Access(read: Access.Anyone, write: Access.Authenticated, remove: Access.Authenticated)]
[StorageName("rest_sprockets")]
public sealed class Sprocket : Entity<Sprocket>
{
    public string? OwnerId { get; set; }
    public string Spec { get; set; } = "";
}

/// <summary>Read open (every row visible); write/remove narrow to the owner; create stamps server-truth.</summary>
public sealed class SprocketAccess : EntityAccess<Sprocket>
{
    protected override Expression<Func<Sprocket, bool>>? Owner => s => s.OwnerId == CurrentUserId;

    public override IAccessFilter<Sprocket> Constrain(IAccessFilter<Sprocket> q, AccessAction action) => action switch
    {
        AccessAction.Create => q.Stamp(s => s.OwnerId, CurrentUserId),
        AccessAction.Update => q.Where(Owner!).Stamp(s => s.OwnerId, CurrentUserId),
        AccessAction.Delete => q.Where(Owner!),
        _ => q, // Read is OPEN — the whole collection is visible; `can` differs per row
    };
}

/// <summary>
/// SEC-0004 Slice C — a CUSTOM verb. <see cref="OrderAccess"/> declares a "fulfill" verb (admin-only) in
/// <see cref="AccessGate.Custom"/>; the per-row projection surfaces it in <c>can</c> exactly when permitted (an
/// admin sees it; others do not), proving custom verbs participate with zero extra wiring.
/// </summary>
[RestEntity]
[StorageName("rest_orders")]
public sealed class Order : Entity<Order>
{
    public string Item { get; set; } = "";
}

/// <summary>Read/write/remove stay open; only the custom "fulfill" verb is gated (is:admin).</summary>
public sealed class OrderAccess : EntityAccess<Order>
{
    protected override AccessGate ConfigureGate()
    {
        var custom = new Dictionary<string, ActionGate>(StringComparer.OrdinalIgnoreCase)
        {
            ["fulfill"] = Gate.Is("admin"),
        };
        return new AccessGate(new Dictionary<string, ActionGate>(StringComparer.OrdinalIgnoreCase), custom);
    }
}

/// <summary>
/// Test authentication handler: a request carrying <c>X-Test-Scopes</c> (space-delimited) and/or
/// <c>X-Test-Roles</c> (space/comma-delimited) is authenticated with those scopes and roles; without either header
/// the request stays anonymous.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Authorization.Count > 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("The supplied test credential is invalid."));
        }

        var hasScopes = Request.Headers.TryGetValue("X-Test-Scopes", out var scopes);
        var hasRoles = Request.Headers.TryGetValue("X-Test-Roles", out var roles);
        var hasUser = Request.Headers.TryGetValue("X-Test-User", out var user);
        if (!hasScopes && !hasRoles && !hasUser)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>();
        if (hasScopes) claims.Add(new Claim("scope", scopes.ToString()));
        foreach (var role in roles.ToString().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        if (hasUser) claims.Add(new Claim(ClaimTypes.NameIdentifier, user.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
