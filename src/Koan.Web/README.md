# Sylin.Koan.Web

Turn Koan entities into controller-first ASP.NET Core APIs, with shared policy, health, and runtime facts.

- Target framework: net10.0
- License: Apache-2.0

## What it adds

- `EntityController<TEntity, TKey>` and the string-key `EntityController<TEntity>` convenience surface
- attribute-routed ASP.NET Core MVC integration
- health and optional OpenAPI wiring
- static-file middleware only when the host supplies a real web-root provider
- redacted runtime facts at `GET /.well-known/Koan/facts`
- response transformers for deliberate representation shaping
- one ordered request-context contributor lifecycle for validated principal, tenant, and Entity read context

## Install

```powershell
dotnet add package Sylin.Koan.Web
```

## Meaningful result

Define the business model and inherit one controller. `AddKoan()` discovers the capability; the base controller supplies
the CRUD projection through the shared Entity operation boundary:

```csharp
using Koan.Data.Core.Model;      // Entity<T>
using Koan.Web.Controllers;      // EntityController<T>
using Microsoft.AspNetCore.Mvc;  // Route

public sealed class Todo : Entity<Todo>
{
    public string Title { get; set; } = "";
    public bool Done { get; set; }
}

[Route("api/todos")]
public sealed class TodosController : EntityController<Todo>;
```

The result is the `/api/todos` entity API. Lifecycle, authorization, relationship budgets, and other
Entity policies execute at the shared operation boundary rather than being copied into controller actions. Add custom
actions only for business operations that are not entity CRUD.

Use transformers when a representation must differ from the stored model; see WEB-0035 below.

For localized content, keep publication authority in the canonical partition while filtering and sorting
the requested language. An access realization adds that requirement to the ordinary query:

```csharp
public sealed class ArticleAccess : EntityAccess<Article>
{
    public override IAccessFilter<Article> Constrain(IAccessFilter<Article> q, AccessAction action)
    {
        if (action != AccessAction.Read) return q;
        var editor = Principal.IsInRole("editor");
        return q.Where(article => editor || article.Published, partition: "");
    }
}
```

Here `Article` is the application's Entity type with a `Published` property. The same ID must exist in
the default partition and satisfy the predicate before the selected content is counted or paged. Even
an editor requires that counterpart; orphan content stays absent. `null` inherits the current partition,
while `""` explicitly selects the default. REST and MCP use the same declaration without extra registration.
Mongo supports the native cross-partition query. Unsupported connectors or query combinations refuse it.

For request-derived business context, implement `IWebContextContributor` with ordinary scoped DI. Koan invokes it
after authentication and before endpoints. The contributor validates standard `HttpContext` evidence once, then may
contribute a principal, a capability scope, typed Entity predicates, or an existence-hiding rejection. For example, a
share-link contributor can prove `?event=` against a durable grant and call
`context.Where<Photo>(photo => photo.EventId == eventId)`; all downstream Entity, Vector, and Entity-backed Media
reads inherit that predicate automatically. Query values are evidence, never authorization by themselves.

## Inspection and configuration

Runtime facts use the same schema as startup, health, and `koan://facts`. Outside Development, enable
`Koan:Web:ExposeObservabilitySnapshot` deliberately and protect `GET /.well-known/Koan/facts` as an operational
surface. Static-file wiring stays dormant in API-only hosts that have no real web root.

## Boundaries and failures

- A constrained Create branch uses atomic insertion. An identity collision returns existence-hiding
  404, including identities hidden by request predicates. Unsupported providers return 501 with
  `web.mutation.insertUnsupported`. Ordinary unconstrained upsert keeps its existing semantics.
- Mutation constraints mean declared Create/Update predicates or stamps. A read-only realization
  does not constrain writes. Existing coarse authorization, including server grants, remains authoritative.
- Constrained bulk requests with any new or unavailable identity currently return 501 with
  `web.mutation.bulkCreateUnsupported` before persistence. Submit creates individually. An update-only
  batch retains its existing behavior. This is an interim compatibility boundary, not atomic bulk support.
- Dry-run remains a tentative validation preview; it cannot prove identity availability or reserve a key.
  Existing authorized-update races require a separate conditional-write guarantee.

- This package projects capabilities into ASP.NET Core; it is not a standalone server and does not choose a data
  provider. Use `Sylin.Koan.App` for the shortest web entry bundle or compose lower-level packages deliberately.
- `EntityController<T>` exposes direct Entity CRUD. It does not infer workflow endpoints, recursive graph traversal,
  authorization policy, or a public contract versioning strategy.
- Relationship expansion preserves per-type visibility and finite budgets. Unsupported implicit scans return a
  corrective response instead of silently loading a whole source.
- Runtime facts are redacted, not anonymous. Non-Development exposure remains an operator-owned security decision.
- Authentication and richer authorization projections live in optional Web/Auth packages; referencing Web alone does
  not make an API secure.
- Contributed predicates are request-lifetime read visibility. They do not authorize writes, secure raw storage/SQL,
  or travel into durable jobs; those boundaries must establish or re-resolve their own application authority.

## Technical reference

- [Koan.Web technical reference](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Web/TECHNICAL.md)
- [Web API conventions](https://github.com/sylin-org/koan-framework/blob/main/docs/api/web-http-api.md)
- [WEB-0035 — EntityController transformers](https://github.com/sylin-org/koan-framework/blob/main/docs/decisions/WEB-0035-entitycontroller-transformers.md)
- [Engineering guardrails](https://github.com/sylin-org/koan-framework/blob/main/docs/engineering/README.md)

## Ordered response hooks

`IEmitHook<T>` runs in ascending `Order`. `EmitDecision.With(payload)` passes the replacement to
subsequent hooks; `Next()` preserves the current payload. `HookContext.ShortCircuit(...)` stops the
pipeline immediately and takes precedence over a returned replacement. This applies to both collection
and model responses through the shared REST/MCP endpoint pipeline.

For a governed collection summary, use the selected rows as the projection source:

```csharp
return EmitDecision.Project(articles, article => ArticleSummary.From(article));
```

The hook may load bounded personalization data first and capture it in this mapper. Koan verifies the
exact selected source sequence, maps each row once, and checks identity and scope again before emitting
any view. `Project` is terminal: later emit hooks cannot replace its sources or receive its DTOs. Ordinary
`IProjectionOf<TEntity, TView>.From` remains application mapping code, not an authority claim.

`Project` supports flat collection and body-query responses, including `shape=full`. To keep map, dict,
or relationship responses, a hook can return `Next()` when `ctx.Options.Shape` is `map` or `dict`, or
`ctx.Options.IncludeRelationships` is true. Those options are normalized at the shared endpoint before
hooks. Framework shapes are built after emit hooks, so hooks receive the selected entity rows rather
than mutable response wrappers. Combining `Project` with those shapes refuses before mapping.

Counterpart reads reject arbitrary `With` payloads and successful custom short-circuits. Hook denial
statuses remain usable, but arbitrary denial object bodies are removed. A failed mapper returns no
partial view; its side effects, if any, are not undone. Model and mutation emit do not support `Project`.
Mapper exceptions are logged server-side and return a safe 500 response with `web.read.projectionFailed`.
Source-bound projection initially supports string, Guid, and the eight built-in integer key types.
Other keys, including mutable byte arrays, refuse before mapping; ordinary `With` is unchanged.

Every invoked model hook's `ShortCircuit` result is honored. A pre-save, pre-delete or pre-patch stop
prevents persistence, including dry runs and batch pre-save validation. An after-fetch stop precedes
relationship expansion. Post-write stops control the response without undoing the committed mutation;
the mutation is audited before response hooks run.

## Conditional properties

Use the existing access declaration on a property when operators may see or write a field that
ordinary callers cannot:

```csharp
[Access(read: "is:admin", write: "is:admin")]
public List<string> ClaimedByUserIds { get; set; } = [];
```

The normal `AddKoan()` MVC pipeline applies the property gate to typed Entity, custom ObjectResult
and JsonResult responses, including typed lists and dictionaries. Entity authorization still runs
first. AgentGrants retain resource scope. MCP uses the same decisions and keeps its stronger
`McpIgnore` exclusions. Storage and trusted server predicates remain unchanged.

Caller filters, sorts, delete predicates and patch paths cannot read a denied field. Full typed
replacement, including a custom MVC body, refuses when an omitted field could erase a protected
value. Use JSON Patch for an ordinary editable field. Partial JSON and JSON Merge Patch documents
require permission to write every protected member, including when a document only changes a public
field. Property `remove` and `owner`
have no supported meaning and fail with a corrective explanation.

Known denied caller fields return 403; unknown or unsupported field intent returns 400. MVC's
native invalid-model response returns 400 when complete typed input binding is refused. Unsupported
typed serializer customization fails before buffered output. Static schemas remain a conditional
superset. Explicit standard Newtonsoft casing and ordinary ShouldSerialize rules are preserved.

This protects retained typed member contracts, not secrets copied by custom code into an unannotated
DTO, JToken or string. Such application projections must own their privacy. Direct execution of an
IActionResult outside the MVC invocation pipeline is also outside the result-filter boundary.

For example, `PATCH /works/id` with `Content-Type: application/json-patch+json` and
`[{"op":"replace","path":"/name","value":"Updated"}]` can edit an admitted field. The equivalent
`application/json` or `application/merge-patch+json` document requires whole-value write permission.
This initial boundary avoids ambiguous empty-object and aliased-path effects in legacy patch normalization.

Typed access sidecars and relationship responses preserve member policy through their known framework
wrappers. Inherited property gates use the actual containing resource type. An explicitly Access-declared
JSON-ignored property still restricts replacement; JSON ignoring remains intact on output.

Object/interface slots, legacy ISerializable contracts and nonsealed object contracts conservatively
require guarded serialization, including a nonsealed root supplied by its actual runtime type. Custom
formatters/resolvers/converters or suppressed buffering on those contracts refuse. Sealed, fully known
unrestricted contracts retain their original native formatter path. No application override bypasses
this boundary.
