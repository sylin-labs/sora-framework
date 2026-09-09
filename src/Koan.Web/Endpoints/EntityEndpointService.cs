using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Data.Core.Relationships;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Web.Authorization;
using Koan.Web.Hooks;
using Koan.Web.Infrastructure;

namespace Koan.Web.Endpoints;

internal sealed class EntityEndpointService<TEntity, TKey> : IEntityEndpointService<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IDataService _dataService;
    private readonly IEntityHookPipeline<TEntity> _hookPipeline;
    private readonly IAuthorize? _authorize;
    private readonly IAccessGateCache? _gateCache;
    private readonly ILogger<EntityEndpointService<TEntity, TKey>>? _logger;

    // SEC-0005: an [Audit] entity writes one AgentAction per successful MUTATION (write/remove) through the normal
    // entity path; reads are never audited. Computed once per closed generic. A bulk op records one row (EntityId="").
    private static readonly bool IsAudited = typeof(TEntity).GetCustomAttribute<AuditAttribute>(inherit: true) is not null;

    private static async System.Threading.Tasks.Task AuditMutation(EntityRequestContext context, string action, string entityId)
    {
        if (!IsAudited) return;
        await new AgentAction
        {
            Subject = AuthSubject.Id(context.User) ?? "anonymous",
            Resource = typeof(TEntity).Name,
            Action = action,
            EntityId = entityId,
            At = DateTimeOffset.UtcNow,
        }.Save(context.CancellationToken).ConfigureAwait(false);
    }

    public EntityEndpointService(
        IDataService dataService,
        IEntityHookPipeline<TEntity> hookPipeline,
        IAuthorize? authorize = null,
        IAccessGateCache? gateCache = null,
        ILogger<EntityEndpointService<TEntity, TKey>>? logger = null)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _hookPipeline = hookPipeline ?? throw new ArgumentNullException(nameof(hookPipeline));
        _authorize = authorize;
        _gateCache = gateCache;
        _logger = logger;
    }

    // ARCH-0092 (§D): the single cross-surface authorization gate. Maps the operation onto a read/write/remove
    // action and asks the unified IAuthorize seam (resource = the entity type). Returns null to PROCEED (the seam
    // allowed, or no seam is registered = allow-by-default); otherwise the denying decision (Forbid/Challenge),
    // which each surface translates. Both REST and the MCP edge run through this one service, so this is the one
    // place base CRUD is authorized.
    private async Task<AuthorizeDecision?> Gate(EntityRequestContext context, string action)
    {
        if (_authorize is null) return null;
        // Memoize per (action) for the lifetime of this request: the operation's guard and AnnotateAccess both
        // ask for the same verbs, so a verb is evaluated through the seam at most once — which matters once an
        // external PDP/ReBAC rung joins the ladder — and the gate decision stays consistent within the request.
        var key = "Koan.Access.Gate." + action;
        if (context.Items.TryGetValue(key, out var cached)) return (AuthorizeDecision?)cached;
        var decision = await _authorize.AuthorizeAsync(new AuthorizeRequest
        {
            Subject = context.User,
            Action = action,
            Resource = typeof(TEntity),
        }).ConfigureAwait(false);
        var result = decision is AuthorizeDecision.Allow ? null : decision;
        context.Items[key] = result;
        return result;
    }

    // SEC-0004 (§C) — honest capability advertisement: the single-item form of the per-row projection. One
    // open-vocabulary list header naming the verbs THIS principal may perform (a verb is permitted when its gate
    // returns null = allow). Replaces the three Koan-Access-Read/Write/Remove booleans. Reached only on a request
    // whose own action gate passed, so the list always includes at least that verb. Gate() is memoized per
    // request, so the guard + these three calls evaluate each verb at most once. Slice B/C extend the same list
    // with custom verbs and the per-row can:[] sidecar, sharing this per-verb gate-allow computation.
    private async Task AnnotateAccess(EntityRequestContext context)
    {
        if (_authorize is null) return;
        var verbs = new List<string>(3);
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is null) verbs.Add(EntityAuthorizeActions.Read);
        if (await Gate(context, EntityAuthorizeActions.Write).ConfigureAwait(false) is null) verbs.Add(EntityAuthorizeActions.Write);
        if (await Gate(context, EntityAuthorizeActions.Remove).ConfigureAwait(false) is null) verbs.Add(EntityAuthorizeActions.Remove);
        context.Headers["Koan-Access"] = string.Join(", ", verbs);
    }

    private static EntityCollectionResult<TEntity> CollectionDenied(EntityRequestContext context, AuthorizeDecision decision)
        => new(context, Array.Empty<TEntity>(), 0, payload: null, shortCircuit: decision);

    private static EntityModelResult<TEntity> ModelDenied(EntityRequestContext context, AuthorizeDecision decision)
        => new(context, default, payload: null, shortCircuit: decision);

    private static EntityEndpointResult Denied(EntityRequestContext context, AuthorizeDecision decision)
        => new(context, payload: null, shortCircuit: decision);

    private static async Task<IActionResult?> AdmitFields(EntityRequestContext context, Action<FieldAccess> inspect)
    {
        try
        {
            var fields = context.FieldAccess;
            if (fields is null)
            {
                fields = await FieldAccess.Prepare(typeof(TEntity), context.Services, context.User,
                    context.CancellationToken).ConfigureAwait(false);
                context.BindFieldAccess(fields);
            }
            if (!fields.IsCurrent(typeof(TEntity), context.User))
                throw new InvalidOperationException("Field access no longer matches the entity or request principal.");
            inspect(fields);
            return null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException or FilterParseException or InvalidFilterFieldException)
        {
            context.Headers.Clear();
            context.Items.Remove(AccessProjection.ManifestKey);
            return new ObjectResult(new
            {
                code = error is UnauthorizedAccessException ? KoanWebConstants.Codes.FieldAccess.Denied
                    : KoanWebConstants.Codes.FieldAccess.Unsupported,
                error = error.Message
            }) { StatusCode = error is UnauthorizedAccessException ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest };
        }
    }

    private static Task<IActionResult?> AdmitFieldOutput(EntityRequestContext context, object? payload)
        => AdmitFields(context, fields =>
        {
            if (!fields.HasRestrictions) return;
            var value = payload switch
            {
                ObjectResult result => result.Value,
                JsonResult result => result.Value,
                _ => payload
            };
            if (value is string or Newtonsoft.Json.Linq.JToken or byte[]
                || value is IActionResult and not StatusCodeResult)
                throw new NotSupportedException("Conditional entity fields require a typed output. Return the entity or a typed view instead of a raw or pre-serialized replacement.");
        });

    private static void AdmitCallerQuery(FieldAccess fields, string? filterJson, QueryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(filterJson)) fields.DemandFilter(JsonFilterParser.Parse<TEntity>(filterJson));
        foreach (var sort in options.Sort) fields.DemandReadPath(sort.Path.DotPath);
        fields.DemandShape(options.Shape);
    }

    // SEC-0004 (§B): the per-request EntityAccess<TEntity> realization (null = no Constrain → byte-identical to
    // today). Resolved + principal-bound once, memoized on the context. Reads ride the open-generic
    // IRequestOptionsHook; these write/delete paths (which never call BuildOptions) resolve it directly.
    private static EntityAccess<TEntity>? ResolveAccessor(EntityRequestContext context)
    {
        const string key = "Koan.Access.Accessor";
        if (context.Items.TryGetValue(key, out var cached)) return (EntityAccess<TEntity>?)cached;
        var accessor = context.Services.GetService<EntityAccess<TEntity>>();
        accessor?.Bind(context);
        context.Items[key] = accessor;
        return accessor;
    }

    // Run the realization's Constrain for one action and return the populated accumulator (Predicates + pending
    // owner Stamps). The author composes Owner via q.Where(Owner) / q.Stamp(ownerSelector, CurrentUserId).
    private static AccessFilter<TEntity> ConstrainFor(EntityAccess<TEntity> accessor, AccessAction action)
    {
        var filter = new AccessFilter<TEntity>();
        accessor.Constrain(filter, action);
        if (action != AccessAction.Read)
            Filter.RequireRowOnly(filter.Filter, $"{action} access constraint");
        return filter;
    }

    // Read visibility is independent of mutation authority. Inspect actual mutation declarations,
    // not the presence of an access realization, and reuse the request-bound accumulators.
    private (AccessFilter<TEntity>? Create, AccessFilter<TEntity>? Update) MutationConstraints(EntityRequestContext context)
    {
        var accessor = ResolveAccessor(context);
        if (accessor is null) return (null, null);
        var create = ConstrainFor(accessor, AccessAction.Create);
        var update = ConstrainFor(accessor, AccessAction.Update);
        return create.HasStamps || create.Filter is not null || update.HasStamps || update.Filter is not null
            ? (create, update)
            : (null, null);
    }

    // SEC-0004 (§C): does the COARSE seam allow this verb at all (respecting every IAuthorize provider)? The outer
    // guard of the per-row projection — the row-bound gate + Constrain then refine it. Gate() is memoized per verb.
    private async Task<bool> CoarseAllows(EntityRequestContext context, string action)
        => _authorize is null || await Gate(context, action).ConfigureAwait(false) is null;

    // SEC-0004 (§C): assemble the per-row projector ONCE per request — coarse seam decisions, the entity's compiled
    // gate (the SAME gate the floor provider enforces, so the projection never disagrees with enforcement), the
    // principal, the realization's single Owner predicate, and the per-verb Constrain predicates (Update is the
    // row-bound write; Delete the row-bound remove).
    private async Task<RowProjection<TEntity>> CreateProjector(EntityRequestContext context,
        Filter? frozenReadFilter, EndpointReadProof<TEntity, TKey>? proof = null)
    {
        var coarseRead = await CoarseAllows(context, EntityAuthorizeActions.Read).ConfigureAwait(false);
        var coarseWrite = await CoarseAllows(context, EntityAuthorizeActions.Write).ConfigureAwait(false);
        var coarseRemove = await CoarseAllows(context, EntityAuthorizeActions.Remove).ConfigureAwait(false);

        var gate = _gateCache?.GetOrCompile(typeof(TEntity)) ?? AccessGate.Open;
        var accessor = ResolveAccessor(context);
        var authed = context.User.Identity?.IsAuthenticated == true;
        var owner = accessor?.OwnerExpression?.Compile();

        var writeFilter = accessor is null ? null : ConstrainFor(accessor, AccessAction.Update).Filter;
        var removeFilter = accessor is null ? null : ConstrainFor(accessor, AccessAction.Delete).Filter;

        return new RowProjection<TEntity>(gate, context.User, coarseRead, coarseWrite, coarseRemove,
            owner, authed, frozenReadFilter, writeFilter, removeFilter, proof is null ? null : proof.Contains);
    }

    // SEC-0004 (§C): the per-row can:[] manifest for a set of rows (id → { can }). Computed once per request and
    // stored on the context for any surface to render — REST wraps it as the `access` sidecar, the MCP edge
    // attaches it to the tool-result metadata.
    private async Task<Dictionary<string, object>> BuildAccessManifest(EntityRequestContext context,
        IReadOnlyList<TEntity> rows, Filter? frozenReadFilter, EndpointReadProof<TEntity, TKey>? proof)
    {
        var projector = await CreateProjector(context, frozenReadFilter, proof).ConfigureAwait(false);
        var manifest = new Dictionary<string, object>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // The key is the row's id rendered as a string — it must match the `id` field the client reads off the
            // serialized item to correlate. This holds for the canonical IEntity key types (string / Guid /
            // numeric: ToString() is the same text JSON emits). The verb list is lowercase-by-design so no
            // serializer naming policy reshapes the manifest's `can` / `items` / `access` keys.
            var id = GetEntityId(row)?.ToString();
            if (id is null) continue;
            manifest[id] = new { can = projector.Can(row) };
        }
        context.Items[AccessProjection.ManifestKey] = manifest;
        return manifest;
    }

    // SEC-0004 (§C): a request opts into the projection when a surface asks — the MCP edge sets RequestKey by
    // default; REST sets includeAccess from ?access=true. wrapRest distinguishes the REST sidecar (wrap the payload
    // into { items, access }) from the MCP path (manifest read off the context; the bare payload is unchanged).
    private static bool ShouldProject(EntityRequestContext context, bool includeAccess, out bool wrapRest)
    {
        wrapRest = includeAccess;
        return includeAccess || context.Items.ContainsKey(AccessProjection.RequestKey);
    }

    public async Task<EntityCollectionResult<TEntity>> GetCollection(EntityCollectionRequest request)
    {
        var context = request.Context;
        context.Options.Shape = request.Shape ?? context.Options.Shape;
        if (request.With is not null)
            context.Options.IncludeRelationships = request.With.Contains("all", StringComparison.OrdinalIgnoreCase);
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is { } denied) return CollectionDenied(context, denied);
        if (await AdmitFields(context, fields => AdmitCallerQuery(fields, request.FilterJson, context.Options)) is { } fieldDenied)
            return new EntityCollectionResult<TEntity>(context, [], 0, null, fieldDenied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        if (!await _hookPipeline.BuildOptions(hookContext, context.Options))
        {
            return await CollectionShortCircuit(context, hookContext);
        }

        QueryDefinition query;
        try
        {
            query = FreezeReadQuery(BuildQueryDefinition(request, context.Options),
                request.FilterJson, request.IgnoreCase, context.Options);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FilterParseException or InvalidFilterFieldException or NotSupportedException)
        {
            return new EntityCollectionResult<TEntity>(context, [], 0, null, new BadRequestObjectResult(new { error = ex.Message }));
        }
        var frozenReadFilter = context.Options.Filter;
        var shape = context.Options.Shape;
        var includeRelationships = context.Options.IncludeRelationships;
        var proof = EndpointReadProof<TEntity, TKey>.Prepare(context, query);

        if (!await _hookPipeline.BeforeCollection(hookContext, context.Options))
        {
            return await CollectionShortCircuit(context, hookContext, proof is not null);
        }
        if (proof is not null && !proof.IsRequestUnchanged(context))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));

        RepositoryQueryResult queryResult;
        long total;
        try
        {
            queryResult = await QueryCollection(query, context.Options.Q, request.AbsoluteMaxRecords,
                context.CancellationToken);
            total = queryResult.Total;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FilterParseException or InvalidFilterFieldException or NotSupportedException)
        {
            var bad = new BadRequestObjectResult(new { error = ex.Message });
            return new EntityCollectionResult<TEntity>(context, [], 0, null, bad);
        }

        if (queryResult.ExceededSafetyLimit)
        {
            _logger?.LogWarning(
                "EntityEndpointService<{Entity}> blocked unpaged response exceeding safety cap {Cap}. Path: {Path}. ReportedTotal: {Total}.",
                typeof(TEntity).Name,
                request.Policy.AbsoluteMaxRecords,
                request.BasePath ?? context.HttpContext?.Request.Path.ToString() ?? "unknown",
                queryResult.Total);

            var errorPayload = new
            {
                error = "Result too large",
                message = $"This endpoint allows at most {request.Policy.AbsoluteMaxRecords} records without pagination."
            };
            var tooLarge = new ObjectResult(errorPayload) { StatusCode = StatusCodes.Status413PayloadTooLarge };
            return new EntityCollectionResult<TEntity>(context, [], queryResult.Total, null, tooLarge);
        }

        // Sort is now applied by Data<T,K>.QueryWithCount (orchestrator) before the result reaches here.
        // The orchestrator inspects RepositoryQueryResult.SortHandled and falls back to in-memory sort
        // when the adapter cannot push it down — see DATA-0092.
        var list = queryResult.Items.ToList();
        if (proof is not null && !proof.Bind(queryResult.ReadEvidence, list))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));

        var shouldPaginate = request.ApplyPagination;

        if (shouldPaginate && !queryResult.RepositoryHandledPagination)
        {
            (list, total) = ApplyPagination(list, context.Options.Page, context.Options.PageSize, total);
            context.Headers["Koan-InMemory-Paging"] = "true";
        }

        if (shouldPaginate)
        {
            context.Headers["X-Page"] = context.Options.Page.ToString();
            context.Headers["X-Page-Size"] = context.Options.PageSize.ToString();
            var totalPages = context.Options.PageSize > 0 ? (int)Math.Ceiling((double)total / context.Options.PageSize) : 0;
            context.Headers["X-Total-Pages"] = totalPages.ToString();
            if (request.IncludeTotalCount)
            {
                context.Headers["X-Total-Count"] = total.ToString();
            }

            if (!string.IsNullOrWhiteSpace(request.BasePath) && request.QueryParameters.Count > 0 && totalPages > 0)
            {
                var links = BuildLinkHeaders(request.BasePath!, request.QueryParameters, context.Options.Page, context.Options.PageSize, totalPages);
                if (links.Length > 0)
                {
                    context.Headers["Link"] = string.Join(", ", links);
                }
            }
        }
        else if (request.IncludeTotalCount)
        {
            context.Headers["X-Total-Count"] = total.ToString();
        }

        if (!await _hookPipeline.AfterCollection(hookContext, list))
        {
            return await CollectionShortCircuit(context, hookContext, proof is not null);
        }
        if (proof is not null && !proof.IsValid(context, list))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));

        object payload = list;
        var relationshipEvidence = new List<Func<bool>>();
        var projectionSourcesUnchanged = CaptureProjectionSources(context, query, list, proof);
        var emit = await _hookPipeline.EmitCollection(hookContext, list);
        if (hookContext.IsShortCircuited) return await CollectionShortCircuit(context, hookContext, proof is not null);
        if (emit.payload is EmitDecision.DeferredProjection projection)
        {
            if (!ProjectionKeySupported())
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ProjectionKeyRejected());
            if (!IsFlatProjection(context.Options))
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ProjectionShapeRejected());
            if (!projection.Matches(list) || !projectionSourcesUnchanged())
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
            try
            {
                var projected = projection.Map(projectionSourcesUnchanged);
                if (projected is null)
                    return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
                payload = projected;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ProjectionFailed(context, ex));
            }
        }
        else if (emit.replaced)
        {
            if (proof is not null)
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
            payload = emit.payload;
        }
        if (proof is not null && !proof.IsValid(context, list))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));

        // Framework containers are built after custom hooks, so no hook can mutate an unproved
        // map entry or relationship wrapper. A terminal projection has already selected its view.
        if (!emit.replaced && includeRelationships)
        {
            try
            {
                payload = await EnrichRelationships(list, context, request.Set, relationshipEvidence);
            }
            catch (RelationshipQueryRejectedException ex)
            {
                return new EntityCollectionResult<TEntity>(context, list, total, null, RelationshipRejectedResult(ex));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                return new EntityCollectionResult<TEntity>(context, [], 0, null, new BadRequestObjectResult(new { error = ex.Message }));
            }
        }
        else if (!emit.replaced && !string.IsNullOrWhiteSpace(shape))
        {
            if (await AdmitFields(context, fields => fields.DemandShape(shape)) is { } shapeDenied)
                return new EntityCollectionResult<TEntity>(context, [], 0, null, shapeDenied);
            payload = ApplyShape(shape, list);
        }

        if (await AdmitFieldOutput(context, payload) is { } fieldOutputDenied)
            return new EntityCollectionResult<TEntity>(context, [], 0, null, fieldOutputDenied);

        ApplyViewHeader(context, request.Accept);

        CopyHookHeaders(context, hookContext);

        // SEC-0004 (§C): the per-row capability projection. Opt-in (REST ?access=true / MCP default) keeps the bare
        // array the default for existing consumers; REST wraps { items, access }, MCP reads the manifest off the
        // context. Computed after EmitCollection so `items` carries whatever the response would have been.
        if (ShouldProject(context, request.IncludeAccess, out var wrapAccess))
        {
            var manifest = await BuildAccessManifest(context, list, frozenReadFilter, proof).ConfigureAwait(false);
            if (wrapAccess) payload = AccessProjection.Wrap(payload, manifest);
        }

        if ((proof is not null && !proof.IsValid(context, list))
            || (emit.payload is EmitDecision.DeferredProjection && !projectionSourcesUnchanged())
            || relationshipEvidence.Any(check => !check()))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
        return new EntityCollectionResult<TEntity>(context, list, total, payload);
    }

    public async Task<EntityCollectionResult<TEntity>> Query(EntityQueryRequest request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is { } denied) return CollectionDenied(context, denied);
        if (await AdmitFields(context, fields => AdmitCallerQuery(fields, request.FilterJson, context.Options)) is { } fieldDenied)
            return new EntityCollectionResult<TEntity>(context, [], 0, null, fieldDenied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        if (!await _hookPipeline.BuildOptions(hookContext, context.Options))
        {
            return await CollectionShortCircuit(context, hookContext);
        }

        QueryDefinition query;
        try
        {
            var definition = QueryDefinition.All.ForPartition(request.Set).WithSort(context.Options.Sort.ToArray());
            if (context.Options.Page > 0 && context.Options.PageSize > 0)
                definition = definition.WithPagination(context.Options.Page, context.Options.PageSize);
            query = FreezeReadQuery(definition, request.FilterJson, request.IgnoreCase, context.Options);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FilterParseException or InvalidFilterFieldException or NotSupportedException)
        {
            return new EntityCollectionResult<TEntity>(context, [], 0, null, new BadRequestObjectResult(new { error = ex.Message }));
        }
        var frozenReadFilter = context.Options.Filter;
        var proof = EndpointReadProof<TEntity, TKey>.Prepare(context, query);

        if (!await _hookPipeline.BeforeCollection(hookContext, context.Options))
        {
            return await CollectionShortCircuit(context, hookContext, proof is not null);
        }
        if (proof is not null && !proof.IsRequestUnchanged(context))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));

        RepositoryQueryResult queryResult;
        long total;
        try
        {
            queryResult = await QueryCollection(query, context.Options.Q, 0, context.CancellationToken);
            total = queryResult.Total;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FilterParseException or InvalidFilterFieldException or NotSupportedException)
        {
            var bad = new BadRequestObjectResult(new { error = ex.Message });
            return new EntityCollectionResult<TEntity>(context, [], 0, null, bad);
        }

        // Sort + pagination handled by orchestrator inside QueryCollectionFromBody (DATA-0092).
        // The caller only sets headers — never paginates again, or we'd page-of-page (regression).
        var list = queryResult.Items.ToList();
        if (proof is not null && !proof.Bind(queryResult.ReadEvidence, list))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
        if (context.Options.PageSize > 0)
        {
            context.Headers["X-Page"] = context.Options.Page.ToString();
            context.Headers["X-Page-Size"] = context.Options.PageSize.ToString();
            context.Headers["X-Total-Count"] = total.ToString();
        }

        if (!await _hookPipeline.AfterCollection(hookContext, list))
        {
            return await CollectionShortCircuit(context, hookContext, proof is not null);
        }
        if (proof is not null && !proof.IsValid(context, list))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));

        ApplyViewHeader(context, request.Accept);

        var projectionSourcesUnchanged = CaptureProjectionSources(context, query, list, proof);
        var emit = await _hookPipeline.EmitCollection(hookContext, list);
        if (hookContext.IsShortCircuited) return await CollectionShortCircuit(context, hookContext, proof is not null);
        object payload = emit.replaced ? emit.payload : list;
        if (emit.payload is EmitDecision.DeferredProjection projection)
        {
            if (!ProjectionKeySupported())
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ProjectionKeyRejected());
            if (!IsFlatProjection(context.Options))
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ProjectionShapeRejected());
            if (!projection.Matches(list) || !projectionSourcesUnchanged())
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
            try
            {
                var projected = projection.Map(projectionSourcesUnchanged);
                if (projected is null)
                    return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
                payload = projected;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new EntityCollectionResult<TEntity>(context, [], 0, null, ProjectionFailed(context, ex));
            }
        }
        else if (proof is not null && emit.replaced)
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
        CopyHookHeaders(context, hookContext);

        // SEC-0004 (§C): the body-query path has no REST sidecar toggle (the bare body schema stays stable); the MCP
        // edge still opts in by default, so the manifest is computed + stashed for the tool-result metadata.
        if (ShouldProject(context, includeAccess: false, out _))
        {
            await BuildAccessManifest(context, list, frozenReadFilter, proof).ConfigureAwait(false);
        }

        if ((proof is not null && !proof.IsValid(context, list))
            || (emit.payload is EmitDecision.DeferredProjection && !projectionSourcesUnchanged()))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
        if (await AdmitFieldOutput(context, payload) is { } finalFieldOutputDenied)
            return new EntityCollectionResult<TEntity>(context, [], 0, null, finalFieldOutputDenied);
        return new EntityCollectionResult<TEntity>(context, list, total, payload);
    }

    public async Task<EntityModelResult<TEntity>> GetNew(EntityGetNewRequest request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is { } denied) return ModelDenied(context, denied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);
        if (!await _hookPipeline.BuildOptions(hookContext, context.Options))
            return await ModelShortCircuit(context, hookContext);
        if (Filter.HasCounterpart(context.Options.Filter))
            return new EntityModelResult<TEntity>(context, null, null, new BadRequestObjectResult(new
            {
                error = "A new-entity template has no persisted identity to authorize against a counterpart. Query an existing entity instead."
            }));

        var model = Activator.CreateInstance<TEntity>();
        if (!await _hookPipeline.AfterModelFetch(hookContext, model))
            return await ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);

        var emit = await _hookPipeline.EmitModel(hookContext, model!);
        if (hookContext.IsShortCircuited) return await ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : model;
        if (await AdmitFieldOutput(context, payload) is { } fieldOutputDenied)
            return new EntityModelResult<TEntity>(context, null, null, fieldOutputDenied);
        CopyHookHeaders(context, hookContext);

        return new EntityModelResult<TEntity>(context, model, payload);
    }

    public async Task<EntityModelResult<TEntity>> GetById(EntityGetByIdRequest<TKey> request)
    {
        var context = request.Context;
        if (request.With is not null)
            context.Options.IncludeRelationships = request.With.Contains("all", StringComparison.OrdinalIgnoreCase);
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is { } denied) return ModelDenied(context, denied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        // The same normalized read constraint reaches keyed and collection reads.
        if (!await _hookPipeline.BuildOptions(hookContext, context.Options))
        {
            return await ModelShortCircuit(context, hookContext);
        }

        context.Options.Filter = Filter.Snapshot(context.Options.Filter);
        var frozenReadFilter = context.Options.Filter;
        using var partition = EntityContext.With(partition: request.Set);
        var query = QueryDefinition.All.ForPartition(request.Set).WithPagination(1, 1)
            .Where(Filter.And(Filter.Eq(nameof(IEntity<TKey>.Id), request.Id), frozenReadFilter));
        var proof = EndpointReadProof<TEntity, TKey>.Prepare(context, query);

        if (!await _hookPipeline.BeforeModelFetch(hookContext, request.Id?.ToString() ?? ""))
        {
            return await ModelShortCircuit(context, hookContext, proof is not null);
        }
        if (proof is not null && !proof.IsRequestUnchanged(context))
            return new EntityModelResult<TEntity>(context, null, null, ReadEvidenceChanged(context));

        TEntity? model;
        if (proof is null)
        {
            model = await Data<TEntity, TKey>.Get(request.Id!, context.CancellationToken);
        }
        else
        {
            try
            {
                var queryRepository = _dataService.GetRepository<TEntity, TKey>() as IQueryRepository<TEntity, TKey>
                    ?? throw new NotSupportedException("The selected connector does not support governed structured reads.");
                var result = await queryRepository.Query(proof.Query.WithCountStrategy(null), context.CancellationToken);
                if (!proof.Bind(result.ReadEvidence, result.Items))
                    return new EntityModelResult<TEntity>(context, null, null, ReadEvidenceChanged(context));
                model = result.Items.SingleOrDefault();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return new EntityModelResult<TEntity>(context, null, null, new BadRequestObjectResult(new { error = ex.Message }));
            }
        }
        if (!await _hookPipeline.AfterModelFetch(hookContext, model))
            return await ModelShortCircuit(context, hookContext, proof is not null);
        if (proof is not null && !proof.IsValid(context, model is null ? [] : [model]))
            return new EntityModelResult<TEntity>(context, null, null, ReadEvidenceChanged(context));
        if (model is null || (proof is null && !PassesRequestPredicates(model, frozenReadFilter)))
        {
            // A predicate-filtered row returns the same NotFound as a missing row so existence is not
            // revealed to a caller the visibility hook excludes.
            CopyHookHeaders(context, hookContext);
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        // SEC-0004 (§C): refine the single-item Koan-Access header against THIS row — but only when a realization
        // exists, since without one the coarse header from AnnotateAccess is already exact (no Owner/Constrain to
        // bind). This is the single-item form of the per-row projection: a public-read/owner-write row fetched by a
        // non-owner advertises `read`, not `read, write` — honest about what the principal may actually do.
        if (_authorize is not null && ResolveAccessor(context) is not null)
        {
            var projector = await CreateProjector(context, frozenReadFilter, proof).ConfigureAwait(false);
            context.Headers["Koan-Access"] = string.Join(", ", projector.Can(model));
        }

        if (context.Options.IncludeRelationships && model is Entity<TEntity, TKey>)
        {
            // WEB-0068 / AN-leak: relationship expansion must govern every related entity by ITS OWN
            // type's visibility predicates — domain Relatives() is app-authority and would tunnel
            // hidden rows out through a visible parent. Already inside the partition scope above.
            RelationshipGraph<TEntity> enriched;
            try
            {
                enriched = await GovernedRelationshipExpander.ExpandAsync<TEntity, TKey>(model, request.Id!, context);
            }
            catch (RelationshipQueryRejectedException ex)
            {
                return new EntityModelResult<TEntity>(context, model, null, RelationshipRejectedResult(ex));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                return new EntityModelResult<TEntity>(context, null, null, new BadRequestObjectResult(new { error = ex.Message }));
            }
            ApplyViewHeader(context, request.Accept);
            CopyHookHeaders(context, hookContext);
            if (proof is not null && !proof.IsValid(context, [model]))
                return new EntityModelResult<TEntity>(context, null, null, ReadEvidenceChanged(context));
            return new EntityModelResult<TEntity>(context, model, enriched);
        }

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, model);
        if (hookContext.IsShortCircuited) return await ModelShortCircuit(context, hookContext, proof is not null);
        if (proof is not null && emit.replaced)
            return new EntityModelResult<TEntity>(context, null, null, ReadEvidenceChanged(context));
        var payload = emit.replaced ? emit.payload : model;
        if (await AdmitFieldOutput(context, payload) is { } fieldOutputDenied)
            return new EntityModelResult<TEntity>(context, null, null, fieldOutputDenied);
        CopyHookHeaders(context, hookContext);
        if (proof is not null && !proof.IsValid(context, [model]))
            return new EntityModelResult<TEntity>(context, null, null, ReadEvidenceChanged(context));
        return new EntityModelResult<TEntity>(context, model, payload);
    }

    public async Task<EntityModelResult<TEntity>> Upsert(EntityUpsertRequest<TEntity, TKey> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Write).ConfigureAwait(false) is { } denied) return ModelDenied(context, denied);
        if (await AdmitFields(context, fields => fields.DemandReplacement()) is { } fieldDenied)
            return new EntityModelResult<TEntity>(context, null, null, fieldDenied);
        await AnnotateAccess(context).ConfigureAwait(false);
        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        // WEB-0073: route-id authority. A verb that pins the id to the route (PUT) delivers it via
        // RouteId; the model carries it before the create-vs-update split so replace-by-id cannot
        // drift into create. Applied only onto a default id - a disagreeing body id is rejected by
        // the controller before the request is built.
        if (request.RouteId is not null && !EqualityComparer<TKey>.Default.Equals(request.RouteId, default!)
            && EqualityComparer<TKey>.Default.Equals(request.Model.Id, default!))
        {
            if (typeof(TEntity).GetProperty(nameof(IEntity<TKey>.Id), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty) is not { } idProperty || !idProperty.CanWrite)
            {
                throw new InvalidOperationException($"Entity type '{typeof(TEntity).Name}' does not expose a writable id; a route-pinned write (PUT) is not supported for it.");
            }
            idProperty.SetValue(request.Model, request.RouteId);
        }

        // AN11 delta + SEC-0004 Constrain both need the pre-mutation row. Read it ONCE when either asks (MCP and
        // dry-run always want the delta; a constrained entity always needs the create-vs-update split). A plain,
        // unconstrained REST upsert stays a single write with no extra read.
        var mutation = MutationConstraints(context);
        TEntity? before = null;
        if (WantsDelta(context, request.DryRun) || mutation.Create is not null)
        {
            var id = request.Model.Id;
            if (id is not null && !EqualityComparer<TKey>.Default.Equals(id, default!))
            {
                before = await Data<TEntity, TKey>.Get(id, context.CancellationToken);
            }
        }
        if (WantsDelta(context, request.DryRun))
        {
            context.Items[EntityMutationProbe.BeforeKey] = before;
            context.Items[EntityMutationProbe.OperationKey] = before is null ? "create" : "update";
        }
        if (mutation.Create is not null)
        {
            // create (no existing row) → STAMP the owner onto the payload — server-truth that overwrites a forged
            // owner (a Where on create is a silent no-op that lets it through). update → the existing row must be
            // in scope (404 if not, existence-hiding, matching the read rail) then apply the update stamp
            // (freeze ownership by default).
            if (before is null)
            {
                mutation.Create.ApplyStamps(request.Model);
            }
            else
            {
                var constrain = mutation.Update!;
                if (!PassesRequestPredicates(before, constrain.Filter))
                {
                    return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
                }
                constrain.ApplyStamps(request.Model);
            }
        }

        var requiresInsert = mutation.Create is not null && before is null;
        if (requiresInsert && (repo is not IInsertOnlyRepository<TEntity, TKey> ||
            !DataCaps.Describe(repo, repo.GetType().Name).Has(DataCaps.Write.InsertOnly)))
            return new EntityModelResult<TEntity>(context, null, null, InsertUnsupported());

        if (!await _hookPipeline.BeforeSave(hookContext, request.Model))
            return await ModelShortCircuit(context, hookContext);

        if (request.DryRun)
        {
            // Rehearsal: the full hook/validation pipeline ran; the adapter write does NOT, and AfterSave is
            // not raised (no save happened). The "after" face is the would-be model.
            context.Items[EntityMutationProbe.DryRunKey] = true;
            ApplyViewHeader(context, request.Accept);
            CopyHookHeaders(context, hookContext);
            return new EntityModelResult<TEntity>(context, request.Model, request.Model);
        }

        TEntity saved;
        if (requiresInsert)
        {
            var inserted = await ((IInsertOnlyRepository<TEntity, TKey>)repo).Insert(request.Model, context.CancellationToken);
            if (inserted.Outcome == MutationOutcome.Conflict)
                return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
            saved = inserted.Entity!;
        }
        else saved = await request.Model.Upsert<TEntity, TKey>(context.CancellationToken);
        await AuditMutation(context, EntityAuthorizeActions.Write, saved.Id?.ToString() ?? "").ConfigureAwait(false);

        if (!await _hookPipeline.AfterSave(hookContext, saved))
            return await ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, saved);
        if (hookContext.IsShortCircuited) return await ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : saved;
        if (await AdmitFieldOutput(context, payload) is { } fieldOutputDenied)
            return new EntityModelResult<TEntity>(context, null, null, fieldOutputDenied);
        CopyHookHeaders(context, hookContext);
        return new EntityModelResult<TEntity>(context, saved, payload);
    }

    public async Task<EntityEndpointResult> UpsertMany(EntityUpsertManyRequest<TEntity> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Write).ConfigureAwait(false) is { } denied) return Denied(context, denied);
        if (await AdmitFields(context, fields => fields.DemandReplacement()) is { } fieldDenied)
            return new EntityEndpointResult(context, null, fieldDenied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        var list = request.Models.ToList();
        if (list.Count == 0)
        {
            return new EntityEndpointResult(context, null, new BadRequestObjectResult(new { error = "At least one item is required" }));
        }
        if (list.Any(m => m is null))
        {
            return new EntityEndpointResult(context, null, new BadRequestObjectResult(new { error = "Null items are not allowed" }));
        }

        // One partition scope spans the constraint probe, BeforeSave hooks, and the write (mirrors single-item Upsert).
        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);

        var mutation = MutationConstraints(context);
        if (mutation.Create is not null)
        {
            // Preflight the entire request. A null visible read does not prove absence; mixed
            // atomic insertion/update is not yet a provider contract, so such batches fail closed.
            var containsCreate = false;
            foreach (var model in list)
            {
                var id = model.Id;
                var before = id is not null && !EqualityComparer<TKey>.Default.Equals(id, default!)
                    ? await Data<TEntity, TKey>.Get(id, context.CancellationToken)
                    : null;
                if (before is null)
                {
                    containsCreate = true;
                    mutation.Create.ApplyStamps(model);
                }
                else
                {
                    var constrain = mutation.Update!;
                    if (!PassesRequestPredicates(before, constrain.Filter))
                    {
                        return new EntityEndpointResult(context, null, new NotFoundResult());
                    }
                    constrain.ApplyStamps(model);
                }
            }
            if (containsCreate)
                return new EntityEndpointResult(context, null, new ObjectResult(new
                {
                    code = KoanWebConstants.Codes.Mutation.BulkCreateUnsupported,
                    message = "Constrained bulk requests containing new or unavailable identities require atomic mixed writes. Submit creates individually."
                }) { StatusCode = StatusCodes.Status501NotImplemented });
        }

        foreach (var model in list)
        {
            if (!await _hookPipeline.BeforeSave(hookContext, model))
                return await ModelShortCircuit(context, hookContext);
        }

        if (request.DryRun)
        {
            // AN11: batch rehearsal — validation ran, no write. Batch mutations carry a count-level delta
            // (the affected count), not a per-field diff.
            context.Items[EntityMutationProbe.DryRunKey] = true;
            context.Items[EntityMutationProbe.OperationKey] = "upsertMany";
            context.Items[EntityMutationProbe.AffectedCountKey] = list.Count;
            CopyHookHeaders(context, hookContext);
            return new EntityEndpointResult(context, new { wouldUpsert = list.Count });
        }

        var upserted = await Data<TEntity, TKey>.UpsertMany(list, context.CancellationToken);
        await AuditMutation(context, EntityAuthorizeActions.Write, "").ConfigureAwait(false);

        foreach (var model in list)
        {
            if (!await _hookPipeline.AfterSave(hookContext, model))
                return await ModelShortCircuit(context, hookContext);
        }

        context.Headers["Koan-Write-Capabilities"] = WriteCapabilitiesHeader(repo);
        CopyHookHeaders(context, hookContext);
        return new EntityEndpointResult(context, new { upserted });
    }

    public async Task<EntityModelResult<TEntity>> Delete(EntityDeleteRequest<TKey> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Remove).ConfigureAwait(false) is { } denied) return ModelDenied(context, denied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);
        var accessor = ResolveAccessor(context);
        var constraint = accessor is null ? null : ConstrainFor(accessor, AccessAction.Delete);

        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);
        var model = await Data<TEntity, TKey>.Get(request.Id, context.CancellationToken);
        if (model is null)
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        // SEC-0004: a row outside the principal's scope is a 404, never deleted (existence-hiding, matches reads).
        if (constraint is not null && !PassesRequestPredicates(model, constraint.Filter))
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        if (!await _hookPipeline.BeforeDelete(hookContext, model))
            return await ModelShortCircuit(context, hookContext);

        if (WantsDelta(context, request.DryRun))
        {
            context.Items[EntityMutationProbe.BeforeKey] = model;
            context.Items[EntityMutationProbe.OperationKey] = "delete";
        }

        if (request.DryRun)
        {
            // Rehearsal: BeforeDelete ran; the row is NOT removed and AfterDelete is not raised.
            context.Items[EntityMutationProbe.DryRunKey] = true;
            ApplyViewHeader(context, request.Accept);
            CopyHookHeaders(context, hookContext);
            return new EntityModelResult<TEntity>(context, model, model);
        }

        var ok = await Data<TEntity, TKey>.Delete(request.Id, context.CancellationToken);
        if (!ok)
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }
        await AuditMutation(context, EntityAuthorizeActions.Remove, request.Id?.ToString() ?? "").ConfigureAwait(false);
        if (!await _hookPipeline.AfterDelete(hookContext, model))
            return await ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, model);
        if (hookContext.IsShortCircuited) return await ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : model;
        if (await AdmitFieldOutput(context, payload) is { } fieldOutputDenied)
            return new EntityModelResult<TEntity>(context, null, null, fieldOutputDenied);
        CopyHookHeaders(context, hookContext);
        return new EntityModelResult<TEntity>(context, model, payload);
    }

    public async Task<EntityEndpointResult> DeleteMany(EntityDeleteManyRequest<TKey> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Remove).ConfigureAwait(false) is { } denied) return Denied(context, denied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Headers["Koan-Write-Capabilities"] = WriteCapabilitiesHeader(repo);

        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);
        IReadOnlyCollection<TKey> targets = request.Ids ?? [];
        var accessor = ResolveAccessor(context);
        var constrain = accessor is null ? null : ConstrainFor(accessor, AccessAction.Delete);
        if (accessor is not null && targets.Count > 0)
        {
            // SEC-0004: trust no id — a row must be in scope to be deleted (out-of-scope ids are silently skipped,
            // the same hidden-row semantics as a single delete). This bounding runs BEFORE the dry-run report so a
            // rehearsal cannot leak the existence of out-of-scope ids.
            var owned = new List<TKey>(targets.Count);
            foreach (var id in targets)
            {
                var row = await Data<TEntity, TKey>.Get(id, context.CancellationToken);
                if (row is not null && PassesRequestPredicates(row, constrain!.Filter)) owned.Add(id);
            }
            targets = owned;
        }

        if (request.DryRun)
        {
            context.Items[EntityMutationProbe.DryRunKey] = true;
            context.Items[EntityMutationProbe.OperationKey] = "deleteMany";
            context.Items[EntityMutationProbe.AffectedCountKey] = targets.Count;
            return new EntityEndpointResult(context, new { wouldDelete = targets.Count });
        }

        var deleted = await Data<TEntity, TKey>.DeleteMany(targets, context.CancellationToken);
        await AuditMutation(context, EntityAuthorizeActions.Remove, "").ConfigureAwait(false);
        return new EntityEndpointResult(context, new { deleted });
    }

    public async Task<EntityEndpointResult> DeleteByQuery(EntityDeleteByQueryRequest request)
    {
        if (await Gate(request.Context, EntityAuthorizeActions.Remove).ConfigureAwait(false) is { } denied) return Denied(request.Context, denied);
        await AnnotateAccess(request.Context).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new EntityEndpointResult(request.Context, null, new BadRequestResult());
        }

        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);

        // Parse the JSON filter DSL into the unified Filter AST. Works on every adapter (the
        // coordinator pushes what it can and evaluates the rest in-memory) — never a silent match-all.
        Filter filter;
        try
        {
            filter = JsonFilterParser.Parse<TEntity>(request.Query);
        }
        catch (Exception ex) when (ex is FilterParseException or InvalidFilterFieldException)
        {
            return new EntityEndpointResult(request.Context, null, new BadRequestObjectResult(new { error = ex.Message }));
        }

        if (await AdmitFields(request.Context, fields => fields.DemandFilter(filter)) is { } fieldDenied)
            return new EntityEndpointResult(request.Context, null, fieldDenied);

        var accessor = ResolveAccessor(request.Context);
        if (accessor is not null)
        {
            // SEC-0004: a mass delete cannot exceed the principal's rows — AND the Constrain predicate into the
            // user's filter before it ever reaches the adapter (agent-safety).
            filter = Filter.And(filter, ConstrainFor(accessor, AccessAction.Delete).Filter) ?? filter;
        }

        var items = await Data<TEntity, TKey>.All(QueryDefinition.All.Where(filter), request.Context.CancellationToken);
        var ids = items.Select(e => e.Id).ToList();

        if (request.DryRun)
        {
            // AN11: the query already ran, so the rehearsal reports an EXACT affected count for free.
            request.Context.Items[EntityMutationProbe.DryRunKey] = true;
            request.Context.Items[EntityMutationProbe.OperationKey] = "deleteByQuery";
            request.Context.Items[EntityMutationProbe.AffectedCountKey] = ids.Count;
            return new EntityEndpointResult(request.Context, new { wouldDelete = ids.Count });
        }

        if (ids.Count == 0) return new EntityEndpointResult(request.Context, new { deleted = 0 });
        var removedByPredicate = await Data<TEntity, TKey>.DeleteMany(ids, request.Context.CancellationToken);
        await AuditMutation(request.Context, EntityAuthorizeActions.Remove, "").ConfigureAwait(false);
        return new EntityEndpointResult(request.Context, new { deleted = removedByPredicate });
    }

    public async Task<EntityEndpointResult> DeleteAll(EntityDeleteAllRequest request)
    {
        if (await Gate(request.Context, EntityAuthorizeActions.Remove).ConfigureAwait(false) is { } denied) return Denied(request.Context, denied);
        await AnnotateAccess(request.Context).ConfigureAwait(false);
        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);

        var accessor = ResolveAccessor(request.Context);
        if (accessor is not null)
        {
            // SEC-0004: a row-scoped entity must NOT truncate the table — bound by the delete constraint, falling
            // back to the read constraint ("delete all the rows I can see") so an author who scopes reads but
            // forgets Delete cannot accidentally truncate. Only an entity with NEITHER narrowing keeps RemoveAll.
            var bound = ConstrainFor(accessor, AccessAction.Delete).Filter
                     ?? ConstrainFor(accessor, AccessAction.Read).Filter;
            Filter.RequireRowOnly(bound, "DeleteAll access bound");
            if (bound is not null)
            {
                var ids = (await Data<TEntity, TKey>.All(QueryDefinition.All.Where(bound), request.Context.CancellationToken))
                    .Select(e => e.Id).ToList();
                if (request.DryRun)
                {
                    request.Context.Items[EntityMutationProbe.DryRunKey] = true;
                    request.Context.Items[EntityMutationProbe.OperationKey] = "deleteAll";
                    request.Context.Items[EntityMutationProbe.AffectedCountKey] = ids.Count;
                    return new EntityEndpointResult(request.Context, new { wouldDelete = ids.Count });
                }
                var deletedBounded = ids.Count == 0 ? 0 : await Data<TEntity, TKey>.DeleteMany(ids, request.Context.CancellationToken);
                await AuditMutation(request.Context, EntityAuthorizeActions.Remove, "").ConfigureAwait(false);
                return new EntityEndpointResult(request.Context, new { deleted = deletedBounded });
            }
        }

        if (request.DryRun)
        {
            // Unconstrained: name the effect rather than scan the whole set for a count — honest A10 posture.
            request.Context.Items[EntityMutationProbe.DryRunKey] = true;
            request.Context.Items[EntityMutationProbe.OperationKey] = "deleteAll";
            return new EntityEndpointResult(request.Context, new { wouldDeleteAll = true });
        }

        var deleted = await Entity<TEntity, TKey>.RemoveAll(request.Context.CancellationToken);
        await AuditMutation(request.Context, EntityAuthorizeActions.Remove, "").ConfigureAwait(false);
        return new EntityEndpointResult(request.Context, new { deleted });
    }

    public async Task<EntityModelResult<TEntity>> Patch(EntityPatchRequest<TEntity, TKey> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Write).ConfigureAwait(false) is { } denied) return ModelDenied(context, denied);
        if (await AdmitFields(context, fields =>
        {
            if (request.Patch is JsonPatchDocument<TEntity> document)
                fields.DemandPatch(document.Operations.Select(operation => new PatchOp(operation.op, operation.path, operation.from, null)));
            else if (request.Patch is Newtonsoft.Json.Linq.JToken token)
                fields.DemandReplacement();
            else throw new NotSupportedException("Field access requires a supported typed patch document.");
        }) is { } fieldDenied) return new EntityModelResult<TEntity>(context, null, null, fieldDenied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);
        var accessor = ResolveAccessor(context);
        var constrain = accessor is null ? null : ConstrainFor(accessor, AccessAction.Update);

        if (!await _hookPipeline.BeforePatch(hookContext, request.Id?.ToString() ?? "", request.Patch!))
            return await ModelShortCircuit(context, hookContext);

        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);
        var original = await Data<TEntity, TKey>.Get(request.Id!, context.CancellationToken);
        if (original is null)
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        // SEC-0004: the existing row must be in scope (404 if not, existence-hiding); the same filter freezes
        // ownership on the patched copy below.
        if (constrain is not null && !PassesRequestPredicates(original, constrain.Filter))
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        var working = await Data<TEntity, TKey>.Get(request.Id!, context.CancellationToken);
        if (working is null)
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        // Apply generalized patch
        if (request.Patch is Microsoft.AspNetCore.JsonPatch.JsonPatchDocument<TEntity> jp)
        {
            jp.ApplyTo(working);
        }
        else if (request.Patch is Newtonsoft.Json.Linq.JToken jt)
        {
            var opts = context.HttpContext?.RequestServices.GetService(typeof(Microsoft.Extensions.Options.IOptions<Koan.Web.Options.KoanWebOptions>)) as Microsoft.Extensions.Options.IOptions<Koan.Web.Options.KoanWebOptions>;
            var mergePolicy = request.Options?.MergeNulls
                              ?? opts?.Value.MergePatchNullsForNonNullable
                              ?? MergePatchNullPolicy.SetDefault;
            var partialPolicy = request.Options?.PartialNulls
                                ?? opts?.Value.PartialJsonNulls
                                ?? PartialJsonNullPolicy.SetNull;
            if (request.Kind == EntityPatchKind.MergePatch7386)
            {
                // AE-16: the applicator restores a typed working copy (additive collections, private stored
                // state, siblings and family shape survive); refusal, malformed intent, or a non-convertible
                // value throws here, before stamps, BeforeSave and any save (BeforePatch has already run),
                // and becomes a corrective 422.
                try
                {
                    working = new Koan.Data.Core.Patch.MergePatchApplicator<TEntity>(jt, mergePolicy).ApplyToCopy(working);
                }
                catch (Exception ex) when (ex is Newtonsoft.Json.JsonException
                                               or System.IO.InvalidDataException
                                               or InvalidOperationException
                                               or ArgumentException)
                {
                    return new EntityModelResult<TEntity>(context, null, null,
                        new UnprocessableEntityObjectResult(new { error = ex.Message }));
                }
            }
            else
            {
                try
                {
                    working = new Koan.Data.Core.Patch.PartialJsonApplicator<TEntity>(jt, partialPolicy).ApplyToCopy(working);
                }
                catch (Exception ex) when (ex is Newtonsoft.Json.JsonException
                                               or System.IO.InvalidDataException
                                               or InvalidOperationException
                                               or ArgumentException)
                {
                    return new EntityModelResult<TEntity>(context, null, null,
                        new UnprocessableEntityObjectResult(new { error = ex.Message }));
                }
            }
        }
        else
        {
            return new EntityModelResult<TEntity>(context, null, null, new BadRequestObjectResult(new { error = "Unsupported patch payload" }));
        }
        var idProp = typeof(TEntity).GetProperty("Id");
        if (idProp is not null)
        {
            var newId = idProp.GetValue(working);
            if (newId is not null && !Equals(newId, request.Id))
            {
                return new EntityModelResult<TEntity>(context, null, null, new ConflictResult());
            }
        }

        constrain?.ApplyStamps(working!); // freeze ownership (re-stamp owner to principal) before save

        if (!await _hookPipeline.BeforeSave(hookContext, working!))
            return await ModelShortCircuit(context, hookContext);

        if (WantsDelta(context, request.DryRun))
        {
            // The pre-patch row (`original`) is already loaded — the delta costs nothing extra.
            context.Items[EntityMutationProbe.BeforeKey] = original;
            context.Items[EntityMutationProbe.OperationKey] = "update";
        }

        if (request.DryRun)
        {
            // Rehearsal: the patch is applied + validated against the working copy; nothing is saved.
            context.Items[EntityMutationProbe.DryRunKey] = true;
            ApplyViewHeader(context, request.Accept);
            CopyHookHeaders(context, hookContext);
            return new EntityModelResult<TEntity>(context, working, working);
        }

        var saved = await working!.Upsert<TEntity, TKey>(context.CancellationToken);
        await AuditMutation(context, EntityAuthorizeActions.Write, request.Id?.ToString() ?? "").ConfigureAwait(false);
        if (!await _hookPipeline.AfterPatch(hookContext, saved))
            return await ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, saved);
        if (hookContext.IsShortCircuited) return await ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : saved;
        if (await AdmitFieldOutput(context, payload) is { } fieldOutputDenied)
            return new EntityModelResult<TEntity>(context, null, null, fieldOutputDenied);
        CopyHookHeaders(context, hookContext);
        return new EntityModelResult<TEntity>(context, saved, payload);
    }

    private static bool PassesRequestPredicates(TEntity model, Filter? filter)
    {
        Filter.RequireRowOnly(filter, "Row ownership or visibility evaluation");
        return filter is null || InMemoryFilterEvaluator.Compile<TEntity>(filter)(model);
    }

    private static Func<bool> CaptureProjectionSources(EntityRequestContext context, QueryDefinition query,
        IReadOnlyList<TEntity> rows, EndpointReadProof<TEntity, TKey>? proof)
    {
        if (proof is not null) return () => proof.IsValid(context, rows);
        var selected = rows.ToArray();
        var identities = rows.Select(row => row.Id).ToArray();
        var request = EndpointReadProof<TEntity, TKey>.CaptureRequest(context, query);
        return () => request.IsRequestUnchanged(context)
            && rows.Count == selected.Length
            && selected.Select((row, index) => ReferenceEquals(row, rows[index])
                && EqualityComparer<TKey>.Default.Equals(identities[index], row.Id)).All(same => same);
    }

    private static bool IsFlatProjection(QueryOptions options)
        => !options.IncludeRelationships && (string.IsNullOrWhiteSpace(options.Shape)
            || string.Equals(options.Shape, "full", StringComparison.OrdinalIgnoreCase));

    private static BadRequestObjectResult ProjectionShapeRejected() => new(new
    {
        error = "Source-bound projection supports flat collections. Remove map, dict, or relationship shaping from this request."
    });

    private static bool ProjectionKeySupported()
        => typeof(TKey) == typeof(string) || typeof(TKey) == typeof(Guid)
            || typeof(TKey) == typeof(byte) || typeof(TKey) == typeof(sbyte)
            || typeof(TKey) == typeof(short) || typeof(TKey) == typeof(ushort)
            || typeof(TKey) == typeof(int) || typeof(TKey) == typeof(uint)
            || typeof(TKey) == typeof(long) || typeof(TKey) == typeof(ulong);

    private static BadRequestObjectResult ProjectionKeyRejected() => new(new
    {
        error = "Source-bound projection requires an immutable string, Guid, or integral identity. This key type is not supported."
    });

    private ObjectResult ProjectionFailed(EntityRequestContext context, Exception exception)
    {
        context.Items.Remove(AccessProjection.ManifestKey);
        context.Headers.Clear();
        _logger?.LogError(exception, "The collection projection for {Entity} failed before response emission.", typeof(TEntity).Name);
        return new ObjectResult(new
        {
            code = KoanWebConstants.Codes.Read.ProjectionFailed,
            error = "Source-bound projection failed before response emission. No partial view was returned."
        }) { StatusCode = StatusCodes.Status500InternalServerError };
    }

    private static BadRequestObjectResult ReadEvidenceChanged(EntityRequestContext context)
    {
        context.Items.Remove(AccessProjection.ManifestKey);
        context.Headers.Clear();
        return new(new
        {
            code = KoanWebConstants.Codes.Read.EvidenceChanged,
            message = "The returned identities or their authorization scope changed after the query. Preserve queried objects and scope in read hooks, or issue a new governed read."
        });
    }

    private static ObjectResult InsertUnsupported() => new(new
    {
        code = KoanWebConstants.Codes.Mutation.InsertUnsupported,
        message = "This connector or identity shape cannot guarantee atomic insertion. Select a connector and identity shape that support insert-only writes."
    }) { StatusCode = StatusCodes.Status501NotImplemented };

    // AN11: a caller wants a state delta when it explicitly opts in (MCP sets WantsDeltaKey) or whenever the
    // mutation is a dry-run (the rehearsal's whole point is the prospective delta).
    private static bool WantsDelta(EntityRequestContext context, bool dryRun)
        => dryRun || (context.Items.TryGetValue(EntityMutationProbe.WantsDeltaKey, out var v) && v is true);

    // ARCH-0084: negotiate via the unified CapabilitySet (the adapter's IDescribesCapabilities declaration).
    private static CapabilitySet Capabilities(IDataRepository<TEntity, TKey> repo)
        => DataCaps.Describe(repo, repo.GetType().Name);

    // Renders the Koan-Write-Capabilities header as the declared write capability tokens (ARCH-0084).
    private static string WriteCapabilitiesHeader(IDataRepository<TEntity, TKey> repo)
        => string.Join(", ", DataCaps.Describe(repo, repo.GetType().Name).All
            .Where(c => c.Id.StartsWith("write.", StringComparison.Ordinal)).Select(c => c.Id));

    private static void CopyHookHeaders(EntityRequestContext context, HookContext<TEntity> hookContext)
    {
        foreach (var kv in hookContext.ResponseHeaders)
        {
            context.Headers[kv.Key] = kv.Value;
        }
    }

    private static (List<TEntity> Items, long Total) ApplyPagination(List<TEntity> source, int page, int pageSize, long total)
    {
        if (pageSize <= 0)
        {
            return (source, total);
        }

        var skip = Math.Max(page - 1, 0) * pageSize;
        var items = source.Skip(skip).Take(pageSize).ToList();
        return (items, total);
    }
    // In-memory sort moved to Koan.Data.Core.Sorting.InMemorySorter — orchestrator handles fallback.
    // CreateKeySelector replaced by structured MemberPath walking. See DATA-0092.


    private sealed class RepositoryQueryResult
    {
        public RepositoryQueryResult(IReadOnlyList<TEntity> items, long total, bool handled, bool exceededLimit,
            IQueryReadEvidence? readEvidence = null)
        {
            Items = items;
            Total = total;
            RepositoryHandledPagination = handled;
            ExceededSafetyLimit = exceededLimit;
            ReadEvidence = readEvidence;
        }

        public IReadOnlyList<TEntity> Items { get; }
        public long Total { get; }
        public bool RepositoryHandledPagination { get; }
        public bool ExceededSafetyLimit { get; }
        public IQueryReadEvidence? ReadEvidence { get; }
    }

    private static QueryDefinition FreezeReadQuery(QueryDefinition query, string? filterJson,
        bool ignoreCase, QueryOptions options)
    {
        options.Filter = Filter.Snapshot(options.Filter);
        var userFilter = string.IsNullOrWhiteSpace(filterJson) ? null
            : JsonFilterParser.Parse<TEntity>(filterJson, new FilterParseOptions { IgnoreCase = ignoreCase });
        return query with
        {
            Filter = Filter.Snapshot(Filter.And(userFilter, options.Filter)),
            Sort = query.Sort.ToArray()
        };
    }

    private async Task<RepositoryQueryResult> QueryCollection(QueryDefinition query, string? q,
        int absoluteMaxRecords, CancellationToken cancellationToken)
    {
        using var partition = EntityContext.With(partition: query.Partition);
        if (query.Filter is null && !string.IsNullOrWhiteSpace(q))
        {
            var raw = await Data<TEntity, TKey>.QueryRaw(q, null, query, cancellationToken);
            return new RepositoryQueryResult(raw, raw.Count, false, false);
        }
        if (query.Filter is not null && !string.IsNullOrWhiteSpace(q))
            _logger?.LogInformation("EntityEndpointService<{Entity}> dropped free-text Q because the query has a normalized filter.", typeof(TEntity).Name);

        var result = await Data<TEntity, TKey>.QueryWithCount(query, cancellationToken,
            absoluteMaxRecords > 0 ? absoluteMaxRecords : null);
        return new RepositoryQueryResult(result.Items, result.TotalCount, result.RepositoryHandledPagination,
            result.ExceededSafetyLimit, result.ReadEvidence);
    }

    private static QueryDefinition BuildQueryDefinition(EntityCollectionRequest request, QueryOptions options)
    {
        var query = QueryDefinition.All.ForPartition(request.Set).WithSort(options.Sort.ToArray());
        if (request.ApplyPagination && options.Page > 0 && options.PageSize > 0)
            query = query.WithPagination(options.Page, options.PageSize);
        return query;
    }
    // WEB-0068 / AN-leak: collection ?with=all expands each row's relationships through the SAME governed
    // path as the keyed read — every related entity is gated by its own type's visibility predicates.
    // Scope the partition explicitly: the QueryCollection partition scope has already disposed by here.
    private static async Task<object> EnrichRelationships(IReadOnlyList<TEntity> list, EntityRequestContext context, string? set,
        ICollection<Func<bool>> retainedEvidence)
    {
        using var _ = EntityContext.With(partition: set);
        if (list.Count == 0) return Array.Empty<RelationshipGraph<TEntity>>();
        if (list.Any(item => item is not Entity<TEntity, TKey>)) return list;
        var roots = list.Select(item => (item, item.Id)).ToArray();
        var checks = new List<Func<bool>>();
        var enriched = await GovernedRelationshipExpander.ExpandManyAsync<TEntity, TKey>(roots, context, checks);
        foreach (var check in checks)
            retainedEvidence.Add(() =>
            {
                using var partition = EntityContext.With(partition: set);
                return check();
            });
        return enriched;
    }

    private static ObjectResult RelationshipRejectedResult(RelationshipQueryRejectedException exception)
        => new(new
        {
            error = "Relationship expansion rejected",
            reason = exception.ReasonCode,
            relationship = $"{exception.ParentType}->{exception.ChildType}.{exception.ReferenceProperty}",
            provider = exception.Provider,
            correction = exception.Correction,
            limit = exception.Limit
        })
        {
            StatusCode = exception.IsLimitExceeded
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status422UnprocessableEntity
        };

    private static object ApplyShape(string? shape, IReadOnlyList<TEntity> list)
    {
        if (string.Equals(shape, "map", StringComparison.OrdinalIgnoreCase))
        {
            return list.Select(i => new { key = GetEntityId(i), display = GetDisplay(i) }).ToList();
        }
        if (string.Equals(shape, "dict", StringComparison.OrdinalIgnoreCase))
        {
            return list.ToDictionary(i => GetEntityId(i)!, GetDisplay);
        }
        return list;
    }

    private static object? GetEntityId(TEntity entity)
    {
        var t = entity.GetType();
        return t.GetProperty("Id")?.GetValue(entity);
    }

    private static string GetDisplay(TEntity entity)
    {
        var t = entity.GetType();
        return t.GetProperty("Name")?.GetValue(entity) as string
               ?? t.GetProperty("Title")?.GetValue(entity) as string
               ?? t.GetProperty("Label")?.GetValue(entity) as string
               ?? entity.ToString()
               ?? "";
    }

    private static string[] BuildLinkHeaders(string basePath, IReadOnlyDictionary<string, string?> query, int page, int size, int totalPages)
    {
        if (string.IsNullOrWhiteSpace(basePath) || size <= 0 || totalPages <= 0)
        {
            return [];
        }

        var links = new List<string>();
        string Build(int targetPage, string rel)
        {
            var dict = new Dictionary<string, string?>(query, StringComparer.OrdinalIgnoreCase)
            {
                ["page"] = targetPage.ToString(),
                ["size"] = size.ToString()
            };
            var uri = QueryHelpers.AddQueryString(basePath, dict);
            return $"<{uri}>; rel=\"{rel}\"";
        }

        links.Add(Build(1, "first"));
        links.Add(Build(totalPages, "last"));
        if (page > 1)
        {
            links.Add(Build(page - 1, "prev"));
        }
        if (page < totalPages)
        {
            links.Add(Build(page + 1, "next"));
        }
        return links.ToArray();
    }

    private static void ApplyViewHeader(EntityRequestContext context, string? accept)
    {
        string? view = context.Options.View;
        if (!string.IsNullOrWhiteSpace(view))
        {
            context.Headers["Koan-View"] = view;
        }
        else if (!string.IsNullOrWhiteSpace(accept))
        {
            context.Headers["Koan-View"] = ParseViewFromAccept(accept) ?? "full";
        }
        else
        {
            context.Headers["Koan-View"] = "full";
        }
    }

    private static string? ParseViewFromAccept(string accept)
    {
        try
        {
            var parts = accept.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length == 2 && kv[0].Equals("view", StringComparison.OrdinalIgnoreCase))
                {
                    return kv[1].Trim('"');
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static async Task<EntityCollectionResult<TEntity>> CollectionShortCircuit(EntityRequestContext context, HookContext<TEntity> hookContext, bool requiresProof = false)
    {
        CopyHookHeaders(context, hookContext);
        var shortCircuit = hookContext.ShortCircuitPayload;
        if ((requiresProof || Filter.HasCounterpart(context.Options.Filter)) && !IsDenial(shortCircuit))
            return new EntityCollectionResult<TEntity>(context, [], 0, null, ReadEvidenceChanged(context));
        if (requiresProof || Filter.HasCounterpart(context.Options.Filter))
        {
            context.Items.Remove(AccessProjection.ManifestKey);
            context.Headers.Clear();
        }
        if ((requiresProof || Filter.HasCounterpart(context.Options.Filter)) && shortCircuit is ObjectResult { StatusCode: >= 400 } denial)
            shortCircuit = new StatusCodeResult(denial.StatusCode.Value);
        if (await AdmitFieldOutput(context, shortCircuit) is { } fieldOutputDenied)
            shortCircuit = fieldOutputDenied;
        if (shortCircuit is IActionResult action)
        {
            return new EntityCollectionResult<TEntity>(context, [], 0, null, action);
        }
        return new EntityCollectionResult<TEntity>(context, [], 0, shortCircuit, shortCircuit);
    }

    private static async Task<EntityModelResult<TEntity>> ModelShortCircuit(EntityRequestContext context, HookContext<TEntity> hookContext, bool requiresProof = false)
    {
        CopyHookHeaders(context, hookContext);
        var shortCircuit = hookContext.ShortCircuitPayload;
        if ((requiresProof || Filter.HasCounterpart(context.Options.Filter)) && !IsDenial(shortCircuit))
            return new EntityModelResult<TEntity>(context, null, null, ReadEvidenceChanged(context));
        if (requiresProof || Filter.HasCounterpart(context.Options.Filter))
        {
            context.Items.Remove(AccessProjection.ManifestKey);
            context.Headers.Clear();
        }
        if ((requiresProof || Filter.HasCounterpart(context.Options.Filter)) && shortCircuit is ObjectResult { StatusCode: >= 400 } denial)
            shortCircuit = new StatusCodeResult(denial.StatusCode.Value);
        if (await AdmitFieldOutput(context, shortCircuit) is { } fieldOutputDenied)
            shortCircuit = fieldOutputDenied;
        if (shortCircuit is IActionResult action)
        {
            return new EntityModelResult<TEntity>(context, default, null, action);
        }
        return new EntityModelResult<TEntity>(context, default, shortCircuit, shortCircuit);
    }

    private static bool IsDenial(object? result)
        => result is StatusCodeResult { StatusCode: >= 400 }
            or ObjectResult { StatusCode: >= 400 } or ForbidResult or ChallengeResult;

}











