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

Every invoked model hook's `ShortCircuit` result is honored. A pre-save, pre-delete or pre-patch stop
prevents persistence, including dry runs and batch pre-save validation. An after-fetch stop precedes
relationship expansion. Post-write stops control the response without undoing the committed mutation;
the mutation is audited before response hooks run.
