using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Koan.Data.Abstractions;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Web.Attributes;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Koan.Web.Infrastructure;
using System.Net.Mime;
using Koan.Web.Options;
using Koan.Data.Abstractions.Instructions;
using Koan.Web.Queries;
using Koan.Web.PatchOps;

namespace Koan.Web.Controllers;

/// <summary>
/// Generic controller providing CRUD endpoints for any aggregate.
/// Integrates pluggable hooks, pagination, capability headers, and content shaping.
/// </summary>
[ApiController]
public abstract class EntityController<TEntity, TKey> : ControllerBase
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private IEntityEndpointService<TEntity, TKey>? _endpointService;

    protected EntityController()
    {
    }

    protected EntityController(IEntityEndpointService<TEntity, TKey> endpointService)
    {
        _endpointService = endpointService;
    }

    // ARCH-0092 (§D): the CanRead/CanWrite/CanRemove virtuals were hard-cut — authorization is no longer a
    // REST-only controller toggle. Base CRUD is gated by the unified IAuthorize seam inside the shared
    // IEntityEndpointService (so REST and MCP enforce the same declaration). Declare access on the entity with
    // standard [Authorize]/[AllowAnonymous] + [RequireScope].

    protected IEntityEndpointService<TEntity, TKey> EndpointService =>
        _endpointService ?? HttpContext.RequestServices.GetRequiredService<IEntityEndpointService<TEntity, TKey>>();

    private EntityEndpointOptions EndpointOptions => HttpContext.RequestServices.GetRequiredService<IOptions<EntityEndpointOptions>>().Value;

    private EntityRequestContextBuilder ContextBuilder => HttpContext.RequestServices.GetRequiredService<EntityRequestContextBuilder>();

    /// <summary>The collection view used only when the request omits an explicit view.</summary>
    protected virtual string DefaultCollectionView => EndpointOptions.DefaultView;

    protected virtual string GetDisplay(TEntity e)
        => e?.ToString() ?? "";

    private EntityRequestContext CreateRequestContext(QueryOptions options, CancellationToken ct)
        => ContextBuilder.Build(options, ct, HttpContext, HttpContext?.User);

    private IActionResult ResolveShortCircuit(EntityEndpointResult result)
    {
        // ARCH-0092 (§D): a seam denial is carried as a transport-agnostic AuthorizeDecision — translate it to
        // the REST status (Challenge → 401, Forbid → 403) here.
        if (result.DeniedDecision is { } decision)
        {
            return decision is AuthorizeDecision.Challenge
                ? Unauthorized()
                : StatusCode(StatusCodes.Status403Forbidden);
        }

        return result.ShortCircuitResult ?? PrepareResponse(result.ShortCircuitPayload ?? result.Payload);
    }

    private static Dictionary<string, string?> ToQueryDictionary(IQueryCollection query)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in query)
        {
            dict[kv.Key] = kv.Value.ToString();
        }
        return dict;
    }

    private string? GetRelationshipParameter(IQueryCollection query)
    {
        if (!EndpointOptions.AllowRelationshipExpansion) return null;
        return query.TryGetValue("with", out var value) ? value.ToString() : null;
    }

    private string? GetShapeParameter(IQueryCollection query)
    {
        if (!query.TryGetValue("shape", out var value)) return null;
        var shape = value.ToString();
        return EndpointOptions.IsShapeAllowed(shape) ? shape : null;
    }

    private static bool GetBooleanQueryValue(IQueryCollection query, string key)
    {
        return query.TryGetValue(key, out var value) &&
               bool.TryParse(value.ToString(), out var result) && result;
    }

    protected virtual PaginationPolicy GetPaginationPolicy()
    {
        if (HttpContext?.RequestServices is null)
        {
            throw new InvalidOperationException("RequestServices is not available.");
        }

        var services = HttpContext.RequestServices;
        var methodInfo = ControllerContext?.ActionDescriptor?.MethodInfo;

        PaginationAttribute? LocateAttribute()
        {
            var methodAttr = methodInfo?.GetCustomAttribute<PaginationAttribute>();
            if (methodAttr is not null)
            {
                return methodAttr;
            }

            var controllerAttr = GetType().GetCustomAttribute<PaginationAttribute>();
            if (controllerAttr is not null)
            {
                return controllerAttr;
            }

            return null;
        }

        var attr = LocateAttribute();
        return PaginationPolicy.Resolve(services, attr);
    }

    protected virtual QueryOptions BuildOptions()
    {
        // Delegate to static parser for pure query transformation. Sort fields are resolved against TEntity here;
        // unresolvable fields throw InvalidSortFieldException, which the controller actions convert to 400.
        return EntityQueryParser.Parse<TEntity>(HttpContext.Request.Query, EndpointOptions);
    }

    protected virtual ObjectResult PrepareResponse(object? content)
    {
        Response.Headers["Vary"] = "Accept";
        // SEC-0004 (§C): the single Koan-Access list header (the permitted verbs for this principal) is computed
        // from the seam inside the endpoint service and flows via result.Headers (ApplyResponseMetadata) — the
        // single-item form of the per-row capability projection, not the old CanRead/CanWrite/CanRemove virtuals.
        return new ObjectResult(content);
    }

    private void ApplyResponseMetadata(EntityEndpointResult result)
    {
        foreach (var header in result.Headers)
        {
            Response.Headers[header.Key] = header.Value;
        }
    }

    [HttpGet("")]
    [Produces(MediaTypeNames.Application.Json)]
    public virtual async Task<IActionResult> GetCollection(CancellationToken ct)
    {

        var query = HttpContext.Request.Query;
        var policy = GetPaginationPolicy();
        var paginationRequested = query.ContainsKey("page") || query.ContainsKey("pageSize") || query.ContainsKey("size");
        var clientRequestedAll = GetBooleanQueryValue(query, "all");

        int page = 1;
        if (query.TryGetValue("page", out var vp) && int.TryParse(vp, out var parsedPage))
        {
            if (parsedPage < 0)
            {
                return BadRequest(new { error = "page must be >= 0" });
            }

            page = parsedPage < 1 ? 1 : parsedPage;
        }

        int requestedSize = policy.DefaultSize;
        if (query.TryGetValue("pageSize", out var vps) && int.TryParse(vps, out var parsedSize))
        {
            requestedSize = parsedSize;
        }
        else if (query.TryGetValue("size", out var vs) && int.TryParse(vs, out var aliasSize))
        {
            requestedSize = aliasSize;
        }

        if (requestedSize < 1)
        {
            requestedSize = policy.DefaultSize;
        }

        var pageSize = Math.Min(requestedSize, policy.MaxSize);

        var applyPagination = policy.Mode switch
        {
            PaginationMode.On => true,
            PaginationMode.Required => true,
            PaginationMode.Optional => paginationRequested && !clientRequestedAll,
            PaginationMode.Off => false,
            _ => true
        };

        QueryOptions options;
        try
        {
            options = BuildOptions();
        }
        catch (Koan.Data.Abstractions.Sorting.InvalidSortFieldException ex)
        {
            return BadRequest(new { error = ex.Message, field = ex.Field });
        }
        if (!query.TryGetValue("view", out var requestedView) || string.IsNullOrWhiteSpace(requestedView))
            options.View = DefaultCollectionView;
        options.Page = applyPagination ? page : 0;
        options.PageSize = applyPagination ? pageSize : 0;

        if (!string.IsNullOrWhiteSpace(policy.DefaultSort) && options.Sort.Count == 0)
        {
            try
            {
                options.Sort.AddRange(Koan.Data.Core.Sorting.SortSpecParser.ParseStrict<TEntity>(policy.DefaultSort));
            }
            catch (Koan.Data.Abstractions.Sorting.InvalidSortFieldException ex)
            {
                return BadRequest(new { error = $"Invalid PaginationAttribute.DefaultSort: {ex.Message}", field = ex.Field });
            }
        }

        var context = CreateRequestContext(options, ct);
        var request = new EntityCollectionRequest
        {
            Context = context,
            FilterJson = query.TryGetValue("filter", out var f) ? f.ToString() : null,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null,
            IgnoreCase = GetBooleanQueryValue(query, "ignoreCase"),
            With = GetRelationshipParameter(query),
            Shape = GetShapeParameter(query) ?? options.Shape,
            ForcePagination = applyPagination,
            ApplyPagination = applyPagination,
            PaginationRequested = paginationRequested,
            ClientRequestedAll = clientRequestedAll,
            Policy = policy,
            IncludeTotalCount = applyPagination && policy.IncludeCount,
            AbsoluteMaxRecords = policy.AbsoluteMaxRecords,
            Accept = HttpContext.Request.Headers["Accept"].ToString(),
            BasePath = HttpContext.Request.Path.HasValue ? HttpContext.Request.Path.Value : "/",
            // SEC-0004 (§C): ?access=true opts into the per-row `access` sidecar (decoupled from ?with= expansion).
            IncludeAccess = GetBooleanQueryValue(query, Koan.Web.Authorization.AccessProjection.QueryToggle),
            QueryParameters = ToQueryDictionary(query)
        };

        var result = await EndpointService.GetCollection(request);
        ApplyResponseMetadata(result);

        if (result.IsShortCircuited)
        {
            return ResolveShortCircuit(result);
        }

        return PrepareResponse(result.Payload ?? result.Items);
    }

    // POST /query
    [HttpPost("query")]
    [Produces(MediaTypeNames.Application.Json)]
    public virtual async Task<IActionResult> Query([FromBody] object body, CancellationToken ct)
    {

        QueryOptions options;
        try
        {
            options = BuildOptions();
        }
        catch (Koan.Data.Abstractions.Sorting.InvalidSortFieldException ex)
        {
            return BadRequest(new { error = ex.Message, field = ex.Field });
        }
        var context = CreateRequestContext(options, ct);

        string? filterJson = null;
        string? set = null;
        bool ignoreCase = false;

        // X-entitycontroller-query-parse: parse the body explicitly and fail loud on bad input (GET-parity).
        // The previous bare catch swallowed JObject.Parse + (int) cast failures and ran the query with
        // DEFAULTS — silently dropping the filter and returning unfiltered results (200). A non-object body
        // now 400s, page/size mirror the GET-list path (non-coercible -> ignored; negative page rejected),
        // and coercion no longer throws.
        JObject jobj;
        try
        {
            if (JToken.Parse(JsonConvert.SerializeObject(body)) is not JObject parsed)
            {
                return BadRequest(new { error = "Request body must be a JSON object." });
            }
            jobj = parsed;
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid request body." });
        }

        if (jobj.TryGetValue("filter", out var f)) filterJson = f.ToString(Formatting.None);
        if (jobj.TryGetValue("page", out var pageToken) && TryCoerceQueryInt(pageToken, out var pageValue))
        {
            if (pageValue < 0) return BadRequest(new { error = "page must be >= 0" });
            options.Page = pageValue;
        }
        if (jobj.TryGetValue("size", out var sizeToken) && TryCoerceQueryInt(sizeToken, out var sizeValue))
        {
            options.PageSize = sizeValue;
        }
        if (jobj.TryGetValue("set", out var st) && st.Type == JTokenType.String) set = st.ToString();
        // ADR-0093: body sort field. Accepts string array of URL-grammar specs (e.g. ["-createdAt", "+title"]).
        // Body sort overrides any ?sort= query-string value to keep the schema unambiguous.
        if (jobj.TryGetValue("sort", out var sortNode))
        {
            IEnumerable<string>? rawSpecs = null;
            if (sortNode.Type == JTokenType.Array)
            {
                rawSpecs = sortNode.Children<JToken>()
                    .Where(t => t.Type == JTokenType.String)
                    .Select(t => t.Value<string>()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s));
            }
            else if (sortNode.Type == JTokenType.String)
            {
                var raw = sortNode.Value<string>();
                if (!string.IsNullOrWhiteSpace(raw)) rawSpecs = new[] { raw };
            }

            if (rawSpecs is not null)
            {
                try
                {
                    var collected = new List<SortSpec>();
                    foreach (var spec in rawSpecs)
                    {
                        collected.AddRange(Koan.Data.Core.Sorting.SortSpecParser.ParseStrict<TEntity>(spec));
                    }
                    options.Sort.Clear();
                    options.Sort.AddRange(collected);
                }
                catch (Koan.Data.Abstractions.Sorting.InvalidSortFieldException ex)
                {
                    return BadRequest(new { error = ex.Message, field = ex.Field });
                }
            }
        }
        if (jobj.TryGetValue("", out var opt) && opt.Type == JTokenType.Object)
        {
            var o = (JObject)opt;
            if (o.TryGetValue("ignoreCase", out var ic) && ic.Type == JTokenType.Boolean && (bool)ic) ignoreCase = true;
        }

        var request = new EntityQueryRequest
        {
            Context = context,
            FilterJson = filterJson,
            Set = set,
            IgnoreCase = ignoreCase,
            Accept = HttpContext.Request.Headers["Accept"].ToString()
        };

        var result = await EndpointService.Query(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited)
        {
            return ResolveShortCircuit(result);
        }
        return PrepareResponse(result.Payload ?? result.Items);
    }

    // Coerce a JSON token to an int for paging without throwing (GET-parity: non-coercible -> ignored,
    // out-of-range integers treated as absent rather than overflowing).
    private static bool TryCoerceQueryInt(JToken token, out int value)
    {
        value = 0;
        switch (token.Type)
        {
            case JTokenType.Integer:
                var l = token.Value<long>();
                if (l < int.MinValue || l > int.MaxValue) return false;
                value = (int)l;
                return true;
            case JTokenType.String:
                return int.TryParse(
                    token.Value<string>(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            default:
                return false;
        }
    }

    [HttpGet("new")]
    public virtual async Task<IActionResult> GetNew(CancellationToken ct)
    {
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var request = new EntityGetNewRequest
        {
            Context = context,
            Accept = HttpContext.Request.Headers["Accept"].ToString()
        };
        var result = await EndpointService.GetNew(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload ?? result.Model);
    }

    [HttpGet("{id}")]
    public virtual async Task<IActionResult> GetById([FromRoute] TKey id, CancellationToken ct)
    {
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var request = new EntityGetByIdRequest<TKey>
        {
            Context = context,
            Id = id,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null,
            With = GetRelationshipParameter(query),
            Accept = HttpContext.Request.Headers["Accept"].ToString()
        };
        var result = await EndpointService.GetById(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload ?? result.Model);
    }

    [HttpPost("")]
    public virtual async Task<IActionResult> Upsert([FromBody][ValidateNever] TEntity model, CancellationToken ct)
    {
        if (model is null) return BadRequest(new { error = "Request body is required" });
        return await ExecuteUpsert(model, ct);
    }

    // WEB-0073: governed full-replace shim. The verb adds no authority of its own - the body travels
    // the identical Upsert choke point (validation, [Access], hooks, stamps, audit, facts). The only
    // PUT-specific rule is id authority: the route wins. The body's id is checked at the JSON level
    // (the Entity constructor assigns an id at bind time, so the bound model cannot reveal whether
    // the client sent one), then the route id is forced before materialization.
    [HttpPut("{id}")]
    public virtual async Task<IActionResult> Put([FromRoute] TKey id, CancellationToken ct)
    {
        Microsoft.AspNetCore.Http.HttpRequestRewindExtensions.EnableBuffering(HttpContext.Request);
        string raw;
        using (var reader = new System.IO.StreamReader(HttpContext.Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(ct);
            HttpContext.Request.Body.Position = 0;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return BadRequest(new { error = "Request body is required" });

        Newtonsoft.Json.Linq.JObject body;
        try
        {
            body = Newtonsoft.Json.Linq.JObject.Parse(raw);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Invalid PUT body: {ex.Message}" });
        }

        // Defense-in-depth, mirroring PATCH: a body that states an id must agree with the route.
        if (body["id"] is not null)
        {
            var bodyId = body["id"]!.ToObject<TKey>();
            if (bodyId is not null && !EqualityComparer<TKey>.Default.Equals(bodyId, id))
            {
                return Conflict(new { code = Infrastructure.KoanWebConstants.Codes.Put.IdMismatch, message = "Route id does not match body id.", routeId = id, bodyId });
            }
        }
        body["id"] = JValue.FromObject(id);

        var model = body.ToObject<TEntity>();
        if (model is null) return BadRequest(new { error = "Request body could not be bound to the entity" });
        return await ExecuteUpsert(model, ct, id);
    }

    private Task<IActionResult> ExecuteUpsert(TEntity model, CancellationToken ct)
        => ExecuteUpsert(model, ct, default!);

    private async Task<IActionResult> ExecuteUpsert(TEntity model, CancellationToken ct, TKey routeId)
    {
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var request = new EntityUpsertRequest<TEntity, TKey>
        {
            Context = context,
            Model = model,
            RouteId = routeId,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null,
            Accept = HttpContext.Request.Headers["Accept"].ToString()
        };
        var result = await EndpointService.Upsert(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload ?? result.Model);
    }

    [HttpPost("bulk")]
    public virtual async Task<IActionResult> UpsertMany([FromBody][ValidateNever] IEnumerable<TEntity> models, CancellationToken ct)
    {
        if (models is null) return BadRequest(new { error = "Request body is required" });
        var list = models.ToList();
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var request = new EntityUpsertManyRequest<TEntity>
        {
            Context = context,
            Models = list,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null
        };
        var result = await EndpointService.UpsertMany(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload);
    }

    [HttpDelete("{id}")]
    public virtual async Task<IActionResult> Delete([FromRoute] TKey id, CancellationToken ct)
    {
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var request = new EntityDeleteRequest<TKey>
        {
            Context = context,
            Id = id,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null,
            Accept = HttpContext.Request.Headers["Accept"].ToString()
        };
        var result = await EndpointService.Delete(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload ?? result.Model);
    }

    [HttpDelete("bulk")]
    public virtual async Task<IActionResult> DeleteMany([FromBody] IEnumerable<TKey> ids, CancellationToken ct)
    {
        var list = ids?.ToList() ?? new List<TKey>();
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var request = new EntityDeleteManyRequest<TKey>
        {
            Context = context,
            Ids = list,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null
        };
        var result = await EndpointService.DeleteMany(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload);
    }

    [HttpDelete("")]
    public virtual async Task<IActionResult> DeleteByQuery([FromQuery] string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest();
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var request = new EntityDeleteByQueryRequest
        {
            Context = context,
            Query = q!,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null
        };
        var result = await EndpointService.DeleteByQuery(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload);
    }

    [HttpDelete("all")]
    public virtual async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var request = new EntityDeleteAllRequest
        {
            Context = context,
            Set = query.TryGetValue("set", out var setVal) ? setVal.ToString() : null
        };
        var result = await EndpointService.DeleteAll(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload);
    }

    // Single PATCH action that dispatches by Content-Type. Previously this was three sibling
    // actions distinguished only by [Consumes(...)], but ASP.NET Core's media-type matcher treats
    // a `+json` structured suffix (application/json-patch+json, application/merge-patch+json) as
    // a subset of application/json — so all three endpoints matched any JSON-shaped payload and
    // the router raised AmbiguousMatchException. One action with explicit dispatch sidesteps that.
    [HttpPatch("{id}")]
    [Consumes(
        Infrastructure.KoanWebConstants.ContentTypes.ApplicationJsonPatch,
        Infrastructure.KoanWebConstants.ContentTypes.ApplicationMergePatch,
        Infrastructure.KoanWebConstants.ContentTypes.ApplicationJson)]
    public virtual async Task<IActionResult> Patch([FromRoute] TKey id, CancellationToken ct)
    {

        // Buffer the body so we can parse it with the right shape per Content-Type.
        Microsoft.AspNetCore.Http.HttpRequestRewindExtensions.EnableBuffering(HttpContext.Request);
        string raw;
        using (var reader = new System.IO.StreamReader(HttpContext.Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(ct);
            HttpContext.Request.Body.Position = 0;
        }

        var contentType = HttpContext.Request.ContentType ?? Infrastructure.KoanWebConstants.ContentTypes.ApplicationJson;
        // Take only the media type portion (strip ;charset=...).
        var mediaType = contentType.Split(';', 2)[0].Trim();

        object patch;
        var kind = EntityPatchKind.PartialJson;
        try
        {
            if (string.Equals(mediaType, Infrastructure.KoanWebConstants.ContentTypes.ApplicationJsonPatch, StringComparison.OrdinalIgnoreCase))
            {
                var doc = string.IsNullOrWhiteSpace(raw)
                    ? new JsonPatchDocument<TEntity>()
                    : JsonConvert.DeserializeObject<JsonPatchDocument<TEntity>>(raw)
                        ?? new JsonPatchDocument<TEntity>();
                patch = doc;
                kind = EntityPatchKind.JsonPatch6902;
            }
            else
            {
                var token = string.IsNullOrWhiteSpace(raw) ? JValue.CreateNull() : JToken.Parse(raw);

                // Defense-in-depth: an id inside the payload must agree with the route.
                if (token["id"] is not null)
                {
                    var payloadId = token["id"]!.ToObject<TKey>();
                    if (payloadId is not null && !Equals(payloadId, id))
                    {
                        return BadRequest(new { code = Infrastructure.KoanWebConstants.Codes.Patch.IdMismatch, message = "Route id does not match payload id." });
                    }
                }

                patch = token!;
                if (string.Equals(mediaType, Infrastructure.KoanWebConstants.ContentTypes.ApplicationMergePatch, StringComparison.OrdinalIgnoreCase))
                    kind = EntityPatchKind.MergePatch7386;
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Invalid PATCH body for content type '{mediaType}': {ex.Message}" });
        }

        // SEC-0004: route through the governed choke point - gate, row-scope constrain, stamps, hooks and
        // audit all live there. The data-layer Patch instruction bypasses every one of those and must never
        // be called directly from a public surface.
        return await PatchNormalized(id, patch, kind, ct);
    }

    private async Task<IActionResult> PatchNormalized(TKey id, object patch, EntityPatchKind kind, CancellationToken ct)
    {
        var options = BuildOptions();
        var context = CreateRequestContext(options, ct);
        var query = HttpContext.Request.Query;
        var set = query.TryGetValue(Koan.Web.Infrastructure.KoanWebConstants.Query.Set, out var setVal) ? setVal.ToString() : null;
        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(set) ? null : set);

        var request = new EntityPatchRequest<TEntity, TKey>
        {
            Context = context,
            Id = id,
            Patch = patch,
            Kind = kind,
            Set = set,
            Accept = HttpContext.Request.Headers["Accept"].ToString(),
            Options = TryBuildPatchOptionsOverride(query) ?? BuildPatchOptions(),
        };

        var result = await EndpointService.Patch(request);
        ApplyResponseMetadata(result);
        if (result.IsShortCircuited) return ResolveShortCircuit(result);
        return PrepareResponse(result.Payload ?? result.Model);
    }

    private Koan.Data.Abstractions.Instructions.PatchPayload<TKey> NormalizeFromJsonPatch(TKey id, JsonPatchDocument<TEntity> doc)
    {
        // Delegate to static normalizer for pure patch transformation
        return PatchNormalizer.NormalizeJsonPatch<TEntity, TKey>(id, doc, BuildPatchOptions());
    }

    private Koan.Data.Abstractions.Instructions.PatchPayload<TKey> NormalizeFromMergePatch(TKey id, Newtonsoft.Json.Linq.JToken body)
    {
        // Delegate to static normalizer for merge patch semantics
        return PatchNormalizer.NormalizeMergePatch(id, body, BuildPatchOptions());
    }

    private Koan.Data.Abstractions.Instructions.PatchPayload<TKey> NormalizeFromPartialJson(TKey id, Newtonsoft.Json.Linq.JToken body)
    {
        // Delegate to static normalizer for partial JSON semantics
        return PatchNormalizer.NormalizePartialJson(id, body, BuildPatchOptions());
    }

    private Koan.Data.Abstractions.Instructions.PatchOptions BuildPatchOptions()
    {
        var opts = HttpContext?.RequestServices?.GetService(typeof(IOptions<Koan.Web.Options.KoanWebOptions>)) as IOptions<Koan.Web.Options.KoanWebOptions>;
        var mergePolicy = opts?.Value.MergePatchNullsForNonNullable ?? Koan.Data.Abstractions.Instructions.MergePatchNullPolicy.SetDefault;
        var partialPolicy = opts?.Value.PartialJsonNulls ?? Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.SetNull;
        return new Koan.Data.Abstractions.Instructions.PatchOptions(mergePolicy, partialPolicy, Koan.Data.Abstractions.Instructions.ArrayBehavior.Replace);
    }

    private Koan.Data.Abstractions.Instructions.PatchOptions? TryBuildPatchOptionsOverride(Microsoft.AspNetCore.Http.IQueryCollection query)
    {
        // Read global override first
        Koan.Data.Abstractions.Instructions.MergePatchNullPolicy? merge = null;
        Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy? partial = null;

        if (query.TryGetValue(Koan.Web.Infrastructure.KoanWebConstants.Query.Nulls, out var global))
        {
            var val = global.ToString().Trim().ToLowerInvariant();
            switch (val)
            {
                case "default":
                    merge = Koan.Data.Abstractions.Instructions.MergePatchNullPolicy.SetDefault;
                    break;
                case "reject":
                    merge = Koan.Data.Abstractions.Instructions.MergePatchNullPolicy.Reject;
                    partial = Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.Reject;
                    break;
                case "null":
                    partial = Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.SetNull;
                    break;
                case "ignore":
                    partial = Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.Ignore;
                    break;
            }
        }

        if (query.TryGetValue(Koan.Web.Infrastructure.KoanWebConstants.Query.MergeNulls, out var mergeStr))
        {
            var val = mergeStr.ToString().Trim().ToLowerInvariant();
            merge = val == "reject"
                ? Koan.Data.Abstractions.Instructions.MergePatchNullPolicy.Reject
                : Koan.Data.Abstractions.Instructions.MergePatchNullPolicy.SetDefault;
        }

        if (query.TryGetValue(Koan.Web.Infrastructure.KoanWebConstants.Query.PartialNulls, out var partialStr))
        {
            var val = partialStr.ToString().Trim().ToLowerInvariant();
            partial = val switch
            {
                "ignore" => Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.Ignore,
                "reject" => Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.Reject,
                _ => Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.SetNull
            };
        }

        if (merge is null && partial is null) return null;
        // Fill the other policy with current defaults to avoid nulls
        var defaults = BuildPatchOptions();
        return new Koan.Data.Abstractions.Instructions.PatchOptions(
            merge ?? defaults.MergeNulls,
            partial ?? defaults.PartialNulls,
            defaults.Arrays);
    }
}

public abstract class EntityController<TEntity> : EntityController<TEntity, string>
    where TEntity : class, IEntity<string>
{ }






















