using System;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 (§A, §D) — the terse per-action access gate. Mark an entity <c>[Access(...)]</c> and the unified seam
/// enforces it on every surface (REST + MCP). Each of <c>read</c>/<c>write</c>/<c>remove</c> takes a value that is
/// a comma-separated OR-list of single terms (<c>anyone</c>, <c>authenticated</c>, <c>owner</c>, <c>is:role</c>,
/// <c>has:scope:x</c>, <c>has:role:y</c>, <c>has:claim:z=v</c>); an unspecified action is OPEN (allow-by-default).
/// <c>all</c> sets every action not named explicitly. There is no in-string AND — anything richer drops to a
/// Slice B <c>EntityAccess&lt;T&gt;</c> realization with the compile-safe fluent <c>Gate</c>.
/// </summary>
/// <remarks>
/// Use the named-argument form so a declaration is self-documenting:
/// <c>[Access(read: "anyone", write: "authenticated", remove: "is:admin,owner")]</c>. The
/// <see cref="Access"/> static helpers (<c>Access.Anyone</c>, <c>Access.Is("admin")</c>, …) emit the identical
/// canonical string with refactor-safe identifiers.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AccessAttribute : Attribute
{
    public AccessAttribute(string? read = null, string? write = null, string? remove = null, string? all = null)
    {
        Read = read;
        Write = write;
        Remove = remove;
        All = all;
    }

    /// <summary>The gate for read operations (GET list / by-id / query). <c>null</c> = open.</summary>
    public string? Read { get; }

    /// <summary>The gate for write operations (POST / PUT / PATCH). <c>null</c> = open.</summary>
    public string? Write { get; }

    /// <summary>The gate for remove operations (DELETE). <c>null</c> = open.</summary>
    public string? Remove { get; }

    /// <summary>Applies to every action NOT named by an explicit <see cref="Read"/>/<see cref="Write"/>/<see cref="Remove"/>.</summary>
    public string? All { get; }
}

/// <summary>
/// SEC-0004 — refactor-safe authoring constants/helpers that emit the canonical <c>[Access]</c> string. Using
/// <c>Access.Is(nameof(Roles.Admin))</c> instead of a raw <c>"is:admin"</c> keeps the load-bearing identity names
/// grep-able and renamable. These same names re-alias to the Slice B fluent <c>Gate</c> builder, so there is one
/// authoring vocabulary across both slices.
/// </summary>
public static class Access
{
    /// <summary>The open token — everyone, authenticated or not.</summary>
    public const string Anyone = "anyone";

    /// <summary>Any signed-in principal.</summary>
    public const string Authenticated = "authenticated";

    /// <summary>The principal satisfies the entity's <c>Owner</c> predicate (row-bound from Slice B; "authenticated" at the coarse gate).</summary>
    public const string Owner = "owner";

    /// <summary>A role term — <c>Access.Is("admin")</c> → <c>"is:admin"</c>.</summary>
    public static string Is(string role) => "is:" + role;

    /// <summary>A raw grant term — <c>Access.Has("scope:orders:write")</c> → <c>"has:scope:orders:write"</c>.
    /// Prefer the typed <see cref="HasScope"/>/<see cref="HasRole"/>/<see cref="HasClaim"/> helpers.</summary>
    public static string Has(string grant) => "has:" + grant;

    /// <summary>An OAuth scope grant — <c>Access.HasScope("orders:write")</c> → <c>"has:scope:orders:write"</c>.</summary>
    public static string HasScope(string scope) => "has:scope:" + scope;

    /// <summary>A typed role grant — <c>Access.HasRole("auditor")</c> → <c>"has:role:auditor"</c>.</summary>
    public static string HasRole(string role) => "has:role:" + role;

    /// <summary>A claim grant — <c>Access.HasClaim("tier", "pro")</c> → <c>"has:claim:tier=pro"</c>.</summary>
    public static string HasClaim(string type, string value) => $"has:claim:{type}={value}";

    /// <summary>A transport-origin term — <c>Access.Origin(OriginTier.Local)</c> → <c>"origin:local"</c>. WHERE the
    /// call came from (server-stamped, un-forgeable), distinct from WHO the caller is. Satisfiable anonymously.</summary>
    public static string Origin(OriginTier tier) => "origin:" + Authorization.Origin.Value(tier);

    /// <summary>An OR-list of single terms — <c>Access.Or(Access.Is("admin"), Access.Owner)</c> → <c>"is:admin, owner"</c>.</summary>
    public static string Or(params string[] terms) => string.Join(", ", terms);
}
