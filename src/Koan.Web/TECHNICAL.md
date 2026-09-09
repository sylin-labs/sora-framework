---
uid: reference.modules.Koan.web
title: Koan.Web - Technical Reference
description: Contracts, configuration, and architecture for Koan’s ASP.NET Core integration.
packages: [Sylin.Koan.Web]
source: src/Koan.Web/
last_updated: 2026-07-18
---

## What the reference composes

Koan Web is controller-first: attribute-routed MVC controllers and ordinary action results, with
`EntityController<TEntity, TKey>` supplying the governed Entity CRUD and query surface.

`WebModule` registers unconditionally on reference. Health endpoints and the well-known facts route
arrive with the package -- they are not an option to switch on -- and `AutoMapControllers` maps
controllers through the startup filter unless an application disables it. OpenAPI is separate: it
arrives by referencing `Sylin.Koan.Web.OpenApi`, not by toggling a setting here.

Failures stay ASP.NET Core failures. Invalid filters, sorts and paging answer `400`; a relationship
expansion no adapter can serve fails closed with `422`; a response past the safety cap answers `413`.

## Key types and surfaces

- `EntityController<TEntity, TKey>`
- `IWebContextContributor` and `WebContext` for ordered, scoped request-context decisions
- Transformers for payload shaping (see WEB-0035)
- `GET /.well-known/Koan/facts` projects `IKoanRuntimeFacts.Current` through `KoanFactJson`.

## Configuration

- `KoanWebOptions` and `WebPipelineOptions` bind under `Koan:Web`; they shape the pipeline that already composed, rather than switching capabilities on
- Runtime facts are exposed automatically in Development. Set
  `Koan:Web:ExposeObservabilitySnapshot=true` for an intentional non-Development exposure.
- Avoid inline endpoints; keep routing in controllers
- `EnableStaticFiles` retains Koan's conventional static-file wiring, but middleware is skipped when
  ASP.NET exposes `NullFileProvider`; API-only applications therefore need no empty `wwwroot` folder.

## Usage guidance

- Implement controllers with attribute routing only
- For data access inside controllers, prefer first-class model statics:
  - `Item.FirstPage(...)`, `Item.Page(...)`, `Item.QueryStream(...)`

### Request context

- Register scoped `IWebContextContributor` implementations with standard DI. The Web module mounts one internal
  runner after authentication; applications do not add contributor middleware manually.
- Contributors run by `Order`. After each asynchronous contribution returns, the runner synchronously enters its
  pending principal/capability/filter scopes before invoking the next contributor. Later contributors can therefore
  depend on earlier validated identity or tenant context. All entered scopes wrap downstream execution and unwind in
  reverse order.
- `WebContext.Where<TEntity>(expression)` lowers through Data's normalized filter compiler and AND-composes with
  other predicates for that Entity type. One Web-owned `IReadFilterContributor` supplies the immutable current map
  to Data's canonical read fold; Data remains unaware of HTTP vocabulary.
- `Reject()` stops the request before endpoints with an existence-hiding response. A contributor must distinguish an
  inapplicable request from applicable-but-invalid evidence explicitly.
- Global Entity cache reads/writes are bypassed while a dynamic read predicate is active. Static model decoration is
  not required for cache safety.

## Edge cases and limits

- Large responses: prefer paging/streaming; set appropriate cache/timeout policies
- Auth/permissions: integrate with Koan.Web.Auth modules where needed
- Context predicates authorize reads only. Controller policies still own write decisions; raw storage and raw SQL do
  not pass through Entity read filters, and request predicates are not durable job context.

## Observability and security

- Integrate with logging/tracing; expose health endpoints when enabled
- Use transformers to control payload shapes and security concerns
- Runtime facts are redacted but still disclose module/decision names; retain an operational access
  boundary and do not cache the response.

## Design and composition

- Composes with Koan.Data modules via application model statics
- Keeps web concerns in controllers; avoids startup inline endpoints

### Startup ownership

- The generic-host binder owns the process-default `AppHost` binding for the host lifetime.
- `KoanWebStartupFilter` flow-scopes the Web application provider while Koan and downstream startup
  filters construct the pipeline. Startup contributors can therefore use ambient Entity operations
  without replacing a newer attached process owner.
- Exiting pipeline construction restores the prior ambient host. This ownership boundary does not
  change middleware, contributor, endpoint, or startup-filter ordering.

## Deployment and topology

- Library packaged for ASP.NET Core apps; no standalone runtime

## Performance guidance

- Favor pagination and projection to reduce payload size

## Compatibility and migrations

- Target frameworks: net10.0

## References

- [Web API](https://github.com/sylin-org/koan-framework/blob/main/docs/api/web-http-api.md)
- [WEB-0035 — EntityController transformers](https://github.com/sylin-org/koan-framework/blob/main/docs/decisions/WEB-0035-entitycontroller-transformers.md)
- [Engineering guardrails](https://github.com/sylin-org/koan-framework/blob/main/docs/engineering/README.md)
- [Runtime facts](https://github.com/sylin-org/koan-framework/blob/main/docs/engineering/runtime-facts.md)

## Relationship expansion safety

- `?with=all` preserves each related type's request-option/visibility predicates and delegates child
  execution to Data.Core's `IRelationshipQueryExecutor`. Collections issue one child query per edge,
  not one query per root.
- `EntityEndpointOptions.RelationshipMaxResults` defaults to 200 rows per edge across the request.
  `RelationshipFallbackMaxCandidates` defaults to null, so scan-backed adapters fail closed unless an
  application explicitly chooses a finite candidate budget.
- Unsupported or implicit scans return 422. Candidate/result limit overflow returns 413. The response
  carries stable reason, relationship, provider, correction, and limit fields; no entity keys or
  provider configuration are included.
- MCP uses `IEntityEndpointService` and therefore receives the same authorization, limits, errors, and
  runtime facts. These are direct-edge guarantees, not recursive graph-depth or parent-batching claims.

## Ordered response hooks

`IEmitHook<T>` runs in ascending `Order`. `EmitDecision.With(payload)` passes the replacement to
subsequent hooks; `Next()` preserves the current payload. `HookContext.ShortCircuit(...)` stops the
pipeline immediately and takes precedence over a returned replacement. This applies to both collection
and model responses through the shared REST/MCP endpoint pipeline.

Every invoked model hook's `ShortCircuit` result is honored. A pre-save, pre-delete or pre-patch stop
prevents persistence, including dry runs and batch pre-save validation. An after-fetch stop precedes
relationship expansion. Post-write stops control the response without undoing the committed mutation;
the mutation is audited before response hooks run.
## Constrained creation and visibility

EntityEndpointService retains the visible before read for authorization and delta projection. A null
does not prove physical absence: constrained creates call IInsertOnlyRepository on the Data facade
resolved inside the requested set. Native collision returns 404 without success audit or AfterSave.
Create-containing constrained bulk requests reject before any persistence until a native atomic mixed
contract exists. The correction is shared by REST and MCP. No unfiltered prior-row read is introduced.

Mutation qualification uses the existing Create/Update AccessFilter predicates and HasStamps. A
read-only access realization leaves writes unchanged. The insertion selector does not reevaluate gates
or reinterpret the principal; existing coarse authorization, including server grants, remains authoritative.
Owner-gate-only row enforcement is outside this insertion correction. No opt-out or policy registry exists.
