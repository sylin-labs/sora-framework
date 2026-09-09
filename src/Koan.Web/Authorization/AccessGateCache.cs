using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 — compiles and caches the per-entity <see cref="AccessGate"/>. The gate provider consumes this so the
/// request path is a dictionary lookup + a pure evaluator call; reflection/parse/lowering happens once.
/// </summary>
public interface IAccessGateCache
{
    /// <summary>The compiled gate for <paramref name="entityType"/> (<see cref="AccessGate.Open"/> when the entity
    /// declares neither <c>[Access]</c> nor a legacy floor attribute — allow-by-default).</summary>
    AccessGate GetOrCompile(Type entityType);

    /// <summary>Principal-independent read/write gates declared on a property.</summary>
    AccessGate GetOrCompile(PropertyInfo property);
}

/// <summary>
/// SEC-0004 — the default <see cref="IAccessGateCache"/>. Lazily compiles on first sight (thread-safe), so the
/// provider works WITHOUT a hard dependency on the higher <c>Koan.Web.Extensions</c>/<c>Koan.Mcp</c> discovery; the
/// boot-time <see cref="AccessGateRegistrar"/> forces all parse errors early when that discovery is present.
/// Compilation merges three sources by precedence: an explicit <c>[Access]</c> per-action value &gt;
/// <c>[Access(all:)]</c> (resolved in the parser) &gt; lowered legacy floor sugar &gt; open.
/// </summary>
public sealed class AccessGateCache : IAccessGateCache
{
    private static readonly string[] Actions =
        { EntityAuthorizeActions.Read, EntityAuthorizeActions.Write, EntityAuthorizeActions.Remove };

    private readonly ConcurrentDictionary<Type, AccessGate> _cache = new();
    private readonly ConcurrentDictionary<PropertyInfo, AccessGate> _members = new();
    private readonly ILogger<AccessGateCache>? _logger;
    private readonly Func<Type, Type?>? _realizationFor;

    public AccessGateCache(ILogger<AccessGateCache>? logger = null, Func<Type, Type?>? realizationFor = null)
    {
        _logger = logger;
        _realizationFor = realizationFor;
    }

    public AccessGate GetOrCompile(Type entityType) => _cache.GetOrAdd(entityType, Compile1);

    public AccessGate GetOrCompile(PropertyInfo property) => _members.GetOrAdd(property, Compile);

    /// <summary>Compile a property declaration. Field removal and row ownership have no member meaning.</summary>
    public static AccessGate Compile(PropertyInfo property)
    {
        var access = property.GetCustomAttribute<AccessAttribute>(inherit: true);
        if (access is null) return AccessGate.Open;
        var name = $"{property.DeclaringType?.Name}.{property.Name}";
        if (access.Remove is not null)
            throw new AccessGateException($"[Access] on {name} cannot declare remove. Use write for property assignment and patch removal.");
        var gate = AccessGateParser.Parse(name, access.Read ?? access.All, access.Write ?? access.All, null, null);
        if (DeclaresOwner(gate))
            throw new AccessGateException($"[Access] on {name} cannot declare owner. Property gates use principal roles, scopes or claims.");
        return gate;
    }

    private AccessGate Compile1(Type entityType)
    {
        var realizationType = _realizationFor?.Invoke(entityType);
        AccessGate? realizationGate = null;
        if (realizationType is not null)
        {
            // The gate declaration is principal-INDEPENDENT (structure only), so a throwaway UNBOUND instance is
            // safe — the singleton cache must never hold the scoped per-request realization.
            var probe = (IEntityAccessRealization)Activator.CreateInstance(realizationType)!;
            realizationGate = probe.BuildGate();
            if (probe.IsCreateUnstamped())
            {
                _logger?.LogWarning(
                    "EntityAccess for {Entity} constrains Create with a Where but no Stamp — a Where on create is a " +
                    "no-op, so a forged owner is NOT overwritten. Use q.Stamp(o => o.OwnerField, CurrentUserId) on create.",
                    entityType.Name);
            }
        }

        var gate = Compile(entityType, realizationGate);

        // The owner-inert warning only applies when NO realization exists — with a realization, Constrain (Slice B)
        // makes `owner` honest at the row level, so the gate-time degrade is expected and silent.
        if (realizationType is null && DeclaresOwner(gate))
        {
            _logger?.LogWarning(
                "[Access] on {Entity} declares 'owner' but no EntityAccess realization is present — 'owner' " +
                "degrades to 'authenticated' (any signed-in principal passes the gate) and rows are not narrowed.",
                entityType.Name);
        }
        return gate;
    }

    /// <summary>Compile the gate for one entity type from <c>[Access]</c> + lowered legacy sugar (no realization;
    /// the unit-test entry point). Throws <see cref="AccessGateException"/> on a malformed <c>[Access]</c> value.</summary>
    public static AccessGate Compile(Type entityType) => Compile(entityType, realizationGate: null);

    /// <summary>Compile with precedence: realization gate (highest) &gt; explicit <c>[Access]</c> (and its <c>all</c>)
    /// &gt; lowered legacy floor &gt; open.</summary>
    internal static AccessGate Compile(Type entityType, AccessGate? realizationGate)
    {
        var access = entityType.GetCustomAttribute<AccessAttribute>(inherit: true);
        var explicitGate = access is null
            ? null
            : AccessGateParser.Parse(entityType.Name, access.Read, access.Write, access.Remove, access.All);

        var legacy = LowerLegacyFloor(entityType);

        if (realizationGate is null && explicitGate is null && legacy is null) return AccessGate.Open;

        var byAction = new Dictionary<string, ActionGate>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions)
        {
            if (realizationGate is not null && realizationGate.ByAction.TryGetValue(action, out var rg))
            {
                byAction[action] = rg; // realization (fluent) gate wins
            }
            else if (explicitGate is not null && explicitGate.ByAction.TryGetValue(action, out var ex))
            {
                byAction[action] = ex; // explicit [Access] (or its `all`) for the actions it names
            }
            else if (legacy is not null && legacy.ByAction.TryGetValue(action, out var lg))
            {
                byAction[action] = lg; // legacy floor fills the unspecified actions (migration-friendly)
            }
            // else: open — leave the action out of the map
        }
        var custom = realizationGate is { Custom.Count: > 0 } ? realizationGate.Custom : EmptyCustom();
        return new AccessGate(byAction, custom);
    }

    /// <summary>True when the entity uses an <c>owner</c> term but (Slice A) cannot narrow rows yet — the registrar
    /// turns this into a boot warning that <c>owner</c> degrades to <c>authenticated</c> until Constrain ships.</summary>
    public static bool DeclaresOwner(AccessGate gate)
        => gate.ByAction.Values.Concat(gate.Custom.Values).Any(g => g.AnyOf.Any(b => b.RequiresOwner));

    // Lower [AllowAnonymous] / [Authorize] / [Authorize(Roles=)] / [RequireScope] into ONE entity-wide ActionGate
    // applied to read+write+remove — exactly reproducing EntityFloorAuthorizationProvider's prior decision matrix.
    internal static AccessGate? LowerLegacyFloor(Type entityType)
    {
        // [AllowAnonymous] opens the entity outright (mirrors ASP.NET precedence): an Anyone BAG (not an open
        // action) so the evaluator returns a concrete Allow even when unauthenticated.
        if (entityType.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null)
        {
            return EntityWide(new ActionGate(new[] { AccessBag.AnyoneBag }));
        }

        var authorize = entityType.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToArray();
        var requireScopes = entityType.GetCustomAttributes<RequireScopeAttribute>(inherit: true).ToArray();
        if (authorize.Length == 0 && requireScopes.Length == 0) return null;

        var grants = new List<Grant>();

        // [Authorize(Roles="a,b")] — roles within one attribute are any-of; ACROSS attributes they AND. The first
        // role-set is the bag's IsRolesAnyOf; each additional becomes a RoleAnyOf grant (AND-evaluated, OR-internal).
        var roleSets = authorize
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .Select(a => (IReadOnlyList<string>)a.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(r => r.Count > 0)
            .ToList();

        IReadOnlyList<string> isRolesAnyOf = Array.Empty<string>();
        for (var i = 0; i < roleSets.Count; i++)
        {
            if (i == 0) isRolesAnyOf = roleSets[0];
            else grants.Add(new Grant.RoleAnyOf(roleSets[i]));
        }

        // [RequireScope("x","y")] (stacked) — all scopes required (AND), distinct.
        foreach (var scope in requireScopes
                     .SelectMany(a => a.Scopes)
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            grants.Add(new Grant.Scope(scope));
        }

        var bag = new AccessBag(isRolesAnyOf, grants, RequiresOwner: false, Anyone: false, Authenticated: true);
        return EntityWide(new ActionGate(new[] { bag }));
    }

    private static AccessGate EntityWide(ActionGate gate)
    {
        var byAction = new Dictionary<string, ActionGate>(StringComparer.OrdinalIgnoreCase)
        {
            [EntityAuthorizeActions.Read] = gate,
            [EntityAuthorizeActions.Write] = gate,
            [EntityAuthorizeActions.Remove] = gate,
        };
        return new AccessGate(byAction, EmptyCustom());
    }

    private static IReadOnlyDictionary<string, ActionGate> EmptyCustom()
        => new Dictionary<string, ActionGate>(StringComparer.OrdinalIgnoreCase);
}
