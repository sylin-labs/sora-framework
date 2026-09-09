namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 (§C) — the well-known keys for the per-row capability projection (the <c>can:[]</c> manifest, the
/// honesty counterweight to allow-by-default). A surface opts a request INTO the projection (REST via
/// <c>?access=true</c>, the MCP edge by default) and reads the computed manifest back to render it in its own
/// idiom — one computation in the shared endpoint, two faces.
/// </summary>
public static class AccessProjection
{
    /// <summary>Context item a surface sets to opt a request INTO the per-row projection (the MCP edge sets it by
    /// default; REST sets it from <c>?access=true</c>). Presence is the opt-in; the value is unused.</summary>
    public const string RequestKey = "Koan.Access.Project";

    /// <summary>Context item the endpoint writes the computed manifest to (id → <c>{ can }</c>), for a surface to
    /// render — the REST <c>access</c> sidecar and the MCP tool-result <c>access</c> metadata both read it.</summary>
    public const string ManifestKey = "Koan.Access.Manifest";

    /// <summary>The REST query toggle (<c>?access=true</c>) that opts a collection response into the <c>access</c>
    /// sidecar. Mirrors the existing <c>?all=true</c> boolean toggle, and is decoupled from <c>?with=</c>
    /// relationship expansion (which is gated separately by <c>AllowRelationshipExpansion</c>).</summary>
    public const string QueryToggle = "access";
    internal static object Wrap(object payload, Dictionary<string, object> manifest)
        => Activator.CreateInstance(typeof(Envelope<>).MakeGenericType(payload.GetType()), payload, manifest)!;

    private sealed record Envelope<T>(
        [property: Newtonsoft.Json.JsonProperty("items")] T Items,
        [property: Newtonsoft.Json.JsonProperty("access")] Dictionary<string, object> Access);
}
