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
using Koan.Web.Filtering;
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

    private static readonly IReadOnlyList<Expression<Func<TEntity, bool>>> NoPredicates =
        Array.Empty<Expression<Func<TEntity, bool>>>();

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
        return create.HasStamps || create.Predicates.Count != 0 || update.HasStamps || update.Predicates.Count != 0
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
    private async Task<RowProjection<TEntity>> CreateProjector(EntityRequestContext context)
    {
        var coarseRead = await CoarseAllows(context, EntityAuthorizeActions.Read).ConfigureAwait(false);
        var coarseWrite = await CoarseAllows(context, EntityAuthorizeActions.Write).ConfigureAwait(false);
        var coarseRemove = await CoarseAllows(context, EntityAuthorizeActions.Remove).ConfigureAwait(false);

        var gate = _gateCache?.GetOrCompile(typeof(TEntity)) ?? AccessGate.Open;
        var accessor = ResolveAccessor(context);
        var authed = context.User.Identity?.IsAuthenticated == true;
        var owner = accessor?.OwnerExpression?.Compile();

        var readPredicates = accessor is null ? NoPredicates : ConstrainFor(accessor, AccessAction.Read).Predicates;
        var writePredicates = accessor is null ? NoPredicates : ConstrainFor(accessor, AccessAction.Update).Predicates;
        var removePredicates = accessor is null ? NoPredicates : ConstrainFor(accessor, AccessAction.Delete).Predicates;

        return new RowProjection<TEntity>(gate, context.User, coarseRead, coarseWrite, coarseRemove,
            owner, authed, readPredicates, writePredicates, removePredicates);
    }

    // SEC-0004 (§C): the per-row can:[] manifest for a set of rows (id → { can }). Computed once per request and
    // stored on the context for any surface to render — REST wraps it as the `access` sidecar, the MCP edge
    // attaches it to the tool-result metadata.
    private async Task<Dictionary<string, object>> BuildAccessManifest(EntityRequestContext context, IReadOnlyList<TEntity> rows)
    {
        var projector = await CreateProjector(context).ConfigureAwait(false);
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
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is { } denied) return CollectionDenied(context, denied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        if (!await _hookPipeline.BuildOptions(hookContext, context.Options))
        {
            return CollectionShortCircuit(context, hookContext);
        }

        if (!await _hookPipeline.BeforeCollection(hookContext, context.Options))
        {
            return CollectionShortCircuit(context, hookContext);
        }

        RepositoryQueryResult queryResult;
        long total;
        try
        {
            queryResult = await QueryCollection(request, context.Options, context.CancellationToken);
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
            return CollectionShortCircuit(context, hookContext);
        }

        object payload = list;

        if (!string.IsNullOrWhiteSpace(request.With) && request.With.Contains("all", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                payload = await EnrichRelationships(list, context, request.Set);
            }
            catch (RelationshipQueryRejectedException ex)
            {
                return new EntityCollectionResult<TEntity>(context, list, total, null, RelationshipRejectedResult(ex));
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Shape))
        {
            payload = ApplyShape(request.Shape, list);
        }

        ApplyViewHeader(context, request.Accept);

        var emit = await _hookPipeline.EmitCollection(hookContext, payload);
        if (hookContext.IsShortCircuited) return CollectionShortCircuit(context, hookContext);
        payload = emit.replaced ? emit.payload : payload;
        CopyHookHeaders(context, hookContext);

        // SEC-0004 (§C): the per-row capability projection. Opt-in (REST ?access=true / MCP default) keeps the bare
        // array the default for existing consumers; REST wraps { items, access }, MCP reads the manifest off the
        // context. Computed after EmitCollection so `items` carries whatever the response would have been.
        if (ShouldProject(context, request.IncludeAccess, out var wrapAccess))
        {
            var manifest = await BuildAccessManifest(context, list).ConfigureAwait(false);
            if (wrapAccess) payload = new { items = payload, access = manifest };
        }

        return new EntityCollectionResult<TEntity>(context, list, total, payload);
    }

    public async Task<EntityCollectionResult<TEntity>> Query(EntityQueryRequest request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is { } denied) return CollectionDenied(context, denied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        if (!await _hookPipeline.BuildOptions(hookContext, context.Options))
        {
            return CollectionShortCircuit(context, hookContext);
        }

        if (!await _hookPipeline.BeforeCollection(hookContext, context.Options))
        {
            return CollectionShortCircuit(context, hookContext);
        }

        IReadOnlyList<TEntity> repositoryItems;
        long total;
        try
        {
            (repositoryItems, total) = await QueryCollectionFromBody(repo, request, context.Options, context.CancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FilterParseException or InvalidFilterFieldException or NotSupportedException)
        {
            var bad = new BadRequestObjectResult(new { error = ex.Message });
            return new EntityCollectionResult<TEntity>(context, [], 0, null, bad);
        }

        // Sort + pagination handled by orchestrator inside QueryCollectionFromBody (DATA-0092).
        // The caller only sets headers — never paginates again, or we'd page-of-page (regression).
        var list = repositoryItems.ToList();
        if (context.Options.PageSize > 0)
        {
            context.Headers["X-Page"] = context.Options.Page.ToString();
            context.Headers["X-Page-Size"] = context.Options.PageSize.ToString();
            context.Headers["X-Total-Count"] = total.ToString();
        }

        if (!await _hookPipeline.AfterCollection(hookContext, list))
        {
            return CollectionShortCircuit(context, hookContext);
        }

        ApplyViewHeader(context, request.Accept);

        var emit = await _hookPipeline.EmitCollection(hookContext, list);
        if (hookContext.IsShortCircuited) return CollectionShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : list;
        CopyHookHeaders(context, hookContext);

        // SEC-0004 (§C): the body-query path has no REST sidecar toggle (the bare body schema stays stable); the MCP
        // edge still opts in by default, so the manifest is computed + stashed for the tool-result metadata.
        if (ShouldProject(context, includeAccess: false, out _))
        {
            await BuildAccessManifest(context, list).ConfigureAwait(false);
        }

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

        var model = Activator.CreateInstance<TEntity>();
        if (!await _hookPipeline.AfterModelFetch(hookContext, model))
            return ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);

        var emit = await _hookPipeline.EmitModel(hookContext, model!);
        if (hookContext.IsShortCircuited) return ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : model;
        CopyHookHeaders(context, hookContext);

        return new EntityModelResult<TEntity>(context, model, payload);
    }

    public async Task<EntityModelResult<TEntity>> GetById(EntityGetByIdRequest<TKey> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Read).ConfigureAwait(false) is { } denied) return ModelDenied(context, denied);
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        // WEB-0068: IRequestOptionsHook predicates are read-visibility filters. The collection/query
        // paths AND-compose them into the adapter query; the keyed read must apply the same predicates
        // against the fetched row, or a row hidden from every listing stays reachable by id — a
        // row-level visibility bypass. BuildOptions runs the hooks; PassesRequestPredicates enforces them.
        if (!await _hookPipeline.BuildOptions(hookContext, context.Options))
        {
            return ModelShortCircuit(context, hookContext);
        }

        if (!await _hookPipeline.BeforeModelFetch(hookContext, request.Id?.ToString() ?? ""))
        {
            return ModelShortCircuit(context, hookContext);
        }

        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);
        var model = await Data<TEntity, TKey>.Get(request.Id!, context.CancellationToken);
        if (!await _hookPipeline.AfterModelFetch(hookContext, model))
            return ModelShortCircuit(context, hookContext);
        if (model is null || !PassesRequestPredicates(model, context.Options.Predicates))
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
            var projector = await CreateProjector(context).ConfigureAwait(false);
            context.Headers["Koan-Access"] = string.Join(", ", projector.Can(model));
        }

        if (!string.IsNullOrWhiteSpace(request.With) && request.With.Contains("all", StringComparison.OrdinalIgnoreCase) && model is Entity<TEntity, TKey>)
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
            ApplyViewHeader(context, request.Accept);
            CopyHookHeaders(context, hookContext);
            return new EntityModelResult<TEntity>(context, model, enriched);
        }

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, model);
        if (hookContext.IsShortCircuited) return ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : model;
        CopyHookHeaders(context, hookContext);
        return new EntityModelResult<TEntity>(context, model, payload);
    }

    public async Task<EntityModelResult<TEntity>> Upsert(EntityUpsertRequest<TEntity, TKey> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Write).ConfigureAwait(false) is { } denied) return ModelDenied(context, denied);
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
                if (!PassesRequestPredicates(before, constrain.Predicates))
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
            return ModelShortCircuit(context, hookContext);

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
            return ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, saved);
        if (hookContext.IsShortCircuited) return ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : saved;
        CopyHookHeaders(context, hookContext);
        return new EntityModelResult<TEntity>(context, saved, payload);
    }

    public async Task<EntityEndpointResult> UpsertMany(EntityUpsertManyRequest<TEntity> request)
    {
        var context = request.Context;
        if (await Gate(context, EntityAuthorizeActions.Write).ConfigureAwait(false) is { } denied) return Denied(context, denied);
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
                    if (!PassesRequestPredicates(before, constrain.Predicates))
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
                return ModelShortCircuit(context, hookContext);
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
                return ModelShortCircuit(context, hookContext);
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

        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);
        var model = await Data<TEntity, TKey>.Get(request.Id, context.CancellationToken);
        if (model is null)
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        // SEC-0004: a row outside the principal's scope is a 404, never deleted (existence-hiding, matches reads).
        var accessor = ResolveAccessor(context);
        if (accessor is not null && !PassesRequestPredicates(model, ConstrainFor(accessor, AccessAction.Delete).Predicates))
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        if (!await _hookPipeline.BeforeDelete(hookContext, model))
            return ModelShortCircuit(context, hookContext);

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
            return ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, model);
        if (hookContext.IsShortCircuited) return ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : model;
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
        if (accessor is not null && targets.Count > 0)
        {
            // SEC-0004: trust no id — a row must be in scope to be deleted (out-of-scope ids are silently skipped,
            // the same hidden-row semantics as a single delete). This bounding runs BEFORE the dry-run report so a
            // rehearsal cannot leak the existence of out-of-scope ids.
            var constrain = ConstrainFor(accessor, AccessAction.Delete);
            var owned = new List<TKey>(targets.Count);
            foreach (var id in targets)
            {
                var row = await Data<TEntity, TKey>.Get(id, context.CancellationToken);
                if (row is not null && PassesRequestPredicates(row, constrain.Predicates)) owned.Add(id);
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

        var accessor = ResolveAccessor(request.Context);
        if (accessor is not null)
        {
            // SEC-0004: a mass delete cannot exceed the principal's rows — AND the Constrain predicate into the
            // user's filter before it ever reaches the adapter (agent-safety).
            filter = QueryFilterComposer.AndAll<TEntity>(filter, ConstrainFor(accessor, AccessAction.Delete).Predicates) ?? filter;
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
            var bound = QueryFilterComposer.AndAll<TEntity>(null, ConstrainFor(accessor, AccessAction.Delete).Predicates)
                     ?? QueryFilterComposer.AndAll<TEntity>(null, ConstrainFor(accessor, AccessAction.Read).Predicates);
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
        await AnnotateAccess(context).ConfigureAwait(false);
        var repo = _dataService.GetRepository<TEntity, TKey>();
        context.Capabilities = Capabilities(repo);

        var hookContext = _hookPipeline.CreateContext(context);

        if (!await _hookPipeline.BeforePatch(hookContext, request.Id?.ToString() ?? "", request.Patch!))
            return ModelShortCircuit(context, hookContext);

        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);
        var original = await Data<TEntity, TKey>.Get(request.Id!, context.CancellationToken);
        if (original is null)
        {
            return new EntityModelResult<TEntity>(context, null, null, new NotFoundResult());
        }

        // SEC-0004: the existing row must be in scope (404 if not, existence-hiding); the same filter freezes
        // ownership on the patched copy below.
        var accessor = ResolveAccessor(context);
        var constrain = accessor is null ? null : ConstrainFor(accessor, AccessAction.Update);
        if (constrain is not null && !PassesRequestPredicates(original, constrain.Predicates))
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
                new Koan.Data.Core.Patch.MergePatchApplicator<TEntity>(jt, mergePolicy).Apply(working);
            }
            else
            {
                new Koan.Data.Core.Patch.PartialJsonApplicator<TEntity>(jt, partialPolicy).Apply(working);
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
            return ModelShortCircuit(context, hookContext);

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
            return ModelShortCircuit(context, hookContext);

        ApplyViewHeader(context, request.Accept);
        var emit = await _hookPipeline.EmitModel(hookContext, saved);
        if (hookContext.IsShortCircuited) return ModelShortCircuit(context, hookContext);
        var payload = emit.replaced ? emit.payload : saved;
        CopyHookHeaders(context, hookContext);
        return new EntityModelResult<TEntity>(context, saved, payload);
    }

    // WEB-0068: evaluate hook-contributed read-visibility predicates against a single fetched model.
    // Each predicate is the Expression<Func<TEntity, bool>> the developer wrote, compiled and invoked
    // here — the ground truth of intent for a security gate. Mirrors QueryFilterComposer's type guard
    // so a mistyped predicate fails the same way on the keyed-read path as on the collection path.
    private static bool PassesRequestPredicates(TEntity model, IReadOnlyList<LambdaExpression> predicates)
    {
        if (predicates.Count == 0) return true;
        foreach (var predicate in predicates)
        {
            var typed = predicate as Expression<Func<TEntity, bool>>
                ?? throw new InvalidOperationException(
                    $"QueryOptions.Predicates entry was {predicate?.GetType().FullName ?? "null"}, expected " +
                    $"Expression<Func<{typeof(TEntity).FullName}, bool>>. Use QueryOptions.AddPredicate<TEntity>(...).");
            if (!typed.Compile().Invoke(model)) return false;
        }
        return true;
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
        public RepositoryQueryResult(IReadOnlyList<TEntity> items, long total, bool handled, bool exceededLimit)
        {
            Items = items;
            Total = total;
            RepositoryHandledPagination = handled;
            ExceededSafetyLimit = exceededLimit;
        }

        public IReadOnlyList<TEntity> Items { get; }
        public long Total { get; }
        public bool RepositoryHandledPagination { get; }
        public bool ExceededSafetyLimit { get; }
    }

    private async Task<RepositoryQueryResult> QueryCollection(
        EntityCollectionRequest request,
        QueryOptions options,
        CancellationToken cancellationToken)
    {
        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);

        Filter? userFilter = null;
        if (!string.IsNullOrWhiteSpace(request.FilterJson))
        {
            userFilter = JsonFilterParser.Parse<TEntity>(request.FilterJson!, new FilterParseOptions { IgnoreCase = request.IgnoreCase });
        }

        // WEB-0068: hook-contributed predicates AND-compose with the user's filter (all lowered to one
        // Filter AST) so the adapter counts and pages against the already-filtered set. When any filter
        // exists, free-text Q is dropped — Q reaches the raw provider surface only.
        var composed = QueryFilterComposer.AndAll<TEntity>(userFilter, options.Predicates);
        var queryDef = BuildQueryDefinition(request, options);
        var absoluteMax = request.AbsoluteMaxRecords > 0 ? request.AbsoluteMaxRecords : (int?)null;

        if (composed is null && !string.IsNullOrWhiteSpace(options.Q))
        {
            var raw = await Data<TEntity, TKey>.QueryRaw(options.Q!, null, queryDef, cancellationToken);
            return new RepositoryQueryResult(raw, raw.Count, false, false);
        }
        if (composed is not null && !string.IsNullOrWhiteSpace(options.Q))
        {
            _logger?.LogInformation(
                "EntityEndpointService<{Entity}> dropped free-text Q because IRequestOptionsHook(s) contributed {Count} predicate(s). See WEB-0068.",
                typeof(TEntity).Name,
                options.Predicates.Count);
        }

        var result = await Data<TEntity, TKey>.QueryWithCount(queryDef.Where(composed), cancellationToken, absoluteMax);

        return new RepositoryQueryResult(result.Items, result.TotalCount, result.RepositoryHandledPagination, result.ExceededSafetyLimit);
    }

    private static QueryDefinition BuildQueryDefinition(EntityCollectionRequest request, QueryOptions options)
    {
        var def = QueryDefinition.All;
        if (request.ApplyPagination && options.Page > 0 && options.PageSize > 0)
            def = def.WithPagination(options.Page, options.PageSize);
        if (!string.IsNullOrWhiteSpace(request.Set))
            def = def.ForPartition(request.Set);
        if (options.Sort.Count > 0)
            def = def.WithSort(options.Sort);
        return def;
    }

    private async Task<(IReadOnlyList<TEntity> Items, long Total)> QueryCollectionFromBody(
        IDataRepository<TEntity, TKey> repo,
        EntityQueryRequest request,
        QueryOptions options,
        CancellationToken cancellationToken)
    {
        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(request.Set) ? null : request.Set);

        // Route body queries through the orchestrator (Data<T,K>.QueryWithCount) so the unified
        // QueryDefinition contract handles split/residual/sort/paginate-after centrally.
        var queryDef = QueryDefinition.All;
        if (options.PageSize > 0 && options.Page > 0)
            queryDef = queryDef.WithPagination(options.Page, options.PageSize);
        if (!string.IsNullOrWhiteSpace(request.Set))
            queryDef = queryDef.ForPartition(request.Set);
        if (options.Sort.Count > 0)
            queryDef = queryDef.WithSort(options.Sort);

        Filter? userFilter = null;
        if (!string.IsNullOrWhiteSpace(request.FilterJson))
        {
            userFilter = JsonFilterParser.Parse<TEntity>(request.FilterJson!, new FilterParseOptions { IgnoreCase = request.IgnoreCase });
        }

        // WEB-0068: same composition rule as the GET path — hook predicates AND with the user's
        // filter (one Filter AST), free-text Q is dropped when any filter contributes.
        var composed = QueryFilterComposer.AndAll<TEntity>(userFilter, options.Predicates);

        if (composed is null && !string.IsNullOrWhiteSpace(options.Q))
        {
            var raw = await Data<TEntity, TKey>.QueryRaw(options.Q!, null, queryDef, cancellationToken);
            return (raw, raw.Count);
        }
        if (composed is not null && !string.IsNullOrWhiteSpace(options.Q))
        {
            _logger?.LogInformation(
                "EntityEndpointService<{Entity}> dropped free-text Q (body-query) because IRequestOptionsHook(s) contributed {Count} predicate(s). See WEB-0068.",
                typeof(TEntity).Name,
                options.Predicates.Count);
        }

        var result = await Koan.Data.Core.Data<TEntity, TKey>.QueryWithCount(queryDef.Where(composed), cancellationToken);
        return (result.Items, result.TotalCount);
    }

    // WEB-0068 / AN-leak: collection ?with=all expands each row's relationships through the SAME governed
    // path as the keyed read — every related entity is gated by its own type's visibility predicates.
    // Scope the partition explicitly: the QueryCollection partition scope has already disposed by here.
    private static async Task<IReadOnlyList<object>> EnrichRelationships(IReadOnlyList<TEntity> list, EntityRequestContext context, string? set)
    {
        using var _ = EntityContext.With(partition: string.IsNullOrWhiteSpace(set) ? null : set);
        if (list.Count == 0) return Array.Empty<object>();
        if (list.Any(item => item is not Entity<TEntity, TKey>)) return list.Cast<object>().ToArray();
        var roots = list.Select(item => (item, item.Id)).ToArray();
        var enriched = await GovernedRelationshipExpander.ExpandManyAsync<TEntity, TKey>(roots, context);
        return enriched.Cast<object>().ToArray();
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

    private static EntityCollectionResult<TEntity> CollectionShortCircuit(EntityRequestContext context, HookContext<TEntity> hookContext)
    {
        CopyHookHeaders(context, hookContext);
        var shortCircuit = hookContext.ShortCircuitPayload;
        if (shortCircuit is IActionResult action)
        {
            return new EntityCollectionResult<TEntity>(context, [], 0, null, action);
        }
        return new EntityCollectionResult<TEntity>(context, [], 0, shortCircuit, shortCircuit);
    }

    private static EntityModelResult<TEntity> ModelShortCircuit(EntityRequestContext context, HookContext<TEntity> hookContext)
    {
        CopyHookHeaders(context, hookContext);
        var shortCircuit = hookContext.ShortCircuitPayload;
        if (shortCircuit is IActionResult action)
        {
            return new EntityModelResult<TEntity>(context, default, null, action);
        }
        return new EntityModelResult<TEntity>(context, default, shortCircuit, shortCircuit);
    }

}











