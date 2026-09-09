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
- Standard Newtonsoft input uses the operation resolver even for unrestricted typed bodies, sharing
  `KoanJsonContractResolver` array construction with Data. Custom unrestricted resolvers or formatter
  subclasses keep their original reader. Member gates remain in `FieldAccessContractResolver`; no persistence setter or
  Entity discriminator policy enters request binding. PUT prepares the same field admission/settings
  before materializing its route-authoritative body and then enters ordinary endpoint authorization.
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
  runtime facts. Parent IDs are batched per edge through a structured ID-set query. These are direct-edge
  guarantees, not recursive graph-depth guarantees.

## Normalized read constraints and counterpart authority

`IAccessFilter<T>`, `AccessFilter<T>` and `QueryOptions` carry one `Filter? Filter`. The former `Predicates`
collections are removed, which is a source and binary API change. Use `q.Where(expression)` and
`options.AddPredicate(expression)` for ordinary lambda ergonomics, or compose the normalized `Filter`
directly. Supported captured values and collections are copied when contributed; the endpoint freezes
the complete query after `BuildOptions`. An opaque CLR-only predicate retains ordinary fallback semantics
and cannot be combined with a counterpart requirement.

`q.Where(expression, partition: "")` lowers immediately to `Filter.SameIdIn<T>`: the same entity and ID
must satisfy the predicate in the explicit default partition. `null` inherits the current partition.
Data binds both sides through its existing source, tenant and segmentation rules. The application owns
the business predicate; Web does not open raw collections or collect an unbounded allow-list.

Collection and body queries carry the complete filter before provider count, order and paging. Keyed
reads query the ID together with the same filter before materialization. Parent expansion uses the
bounded ID set and full related filter; child expansion uses `IRelationshipQueryExecutor`. Unsupported
providers or relational Boolean/residual combinations fail before returning candidates. Count and page
may be separate commands; this is not a cross-command snapshot or a promise against later moderation.

The endpoint binds Data's execution evidence to the request principal, frozen options and exact returned
object references and typed IDs. Access manifests use this read evidence rather than rerunning Read
`Constrain` or evaluating the replica's CLR fields. Related evidence remains valid through the final graph
boundary. Changed identities, principal, query or scope reject the response with `web.read.evidenceChanged`.
No proof is a request-wide permission or a caller-managed token.

Counterpart predicates are read-only. Create, Update, Delete and mass-mutation bounds refuse them before
mutation hooks or writes. A read-only declaration leaves ordinary admin writes unchanged. A `/new`
template has no persisted identity and refuses a counterpart realization before its template callback.

## Ordered response hooks

`IEmitHook<T>` runs in ascending `Order`. `EmitDecision.With(payload)` passes the replacement to
subsequent hooks; `Next()` preserves the current payload. `HookContext.ShortCircuit(...)` stops the
pipeline immediately and takes precedence over a returned replacement. This applies to both collection
and model responses through the shared REST/MCP endpoint pipeline.

For ordinary row-only reads and mutations, an invoked model hook's `ShortCircuit` result is honored. A pre-save, pre-delete or pre-patch stop
prevents persistence, including dry runs and batch pre-save validation. An after-fetch stop precedes
relationship expansion. Post-write stops control the response without undoing the committed mutation;
the mutation is audited before response hooks run.

`EmitDecision.Project(rows, mapper)` is a terminal deferred collection decision. Its factory owns the
typed source reference/order array, then the endpoint checks that it exactly matches the selected page.
Mapping runs into a private array with one full evidence check before and one after the batch. Successful
mapping invokes the delegate once per source row; exceptions discard the array. Foreign, reordered,
subset or replaced sources are rejected, as are source ID, principal or query-scope changes. The framework
does not infer source authority from a DTO implementing `IProjectionOf`, and does not inspect arbitrary
object graphs or try to prove application field calculations.
Supported source identities are string, Guid, byte, sbyte, short, ushort, int, uint, long and ulong.
Other key types refuse before mapping rather than aliasing mutable identity values into a proof.
Mapper exceptions are logged through the endpoint's existing logger without entity or request payloads.
The client receives a safe 500 with `web.read.projectionFailed`; source-evidence rejection remains a
separate 400 response, as do unsupported key or shape combinations. Neither response contains a partial view.

Collection and Query support terminal projection. Model and mutation emit reject that decision before
mapping. `QueryOptions.Shape` and `IncludeRelationships` carry the normalized response choice before
`BuildOptions`; read evidence freezes them together with `View`. Flat/full projection cannot be combined
with map, dict or relationship shaping. For `Next`, framework containers are constructed after custom
emit hooks and related proofs are checked before return. Ordinary row-only `With` chains retain their
ordered replacement behavior. A counterpart read refuses arbitrary `With` and success short-circuits;
error object bodies from custom denials are stripped while their status remains intact. The Access floor
still runs exactly once after user options even when an earlier options hook short-circuits.
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

## Property access admission

`AccessGateCache` compiles property read/write/all using the existing parser. `remove` and row
`owner` are rejected. The Entity floor and `FieldAccess` share the same asynchronous token-first,
resource-grant fallback in `EntityFloorAuthorizationProvider`. Scoped `AgentGrantStore` retains its
existing request memoization; no additional authority provider or grant cache is introduced.

Boot validation inspects declarations in Web and assemblies that reference its Access attribute.
Base declarations are validated at their declaring type. Unrelated runtime property metadata is
outside this scan; inherited runtime policy evaluation is unchanged.

The editor-hidden `FieldAccess.Prepare` follows neutral Newtonsoft object, collection and dictionary
contracts from one root type. It never visits object values. Only structural metadata is shared;
principal decisions and serializer callbacks are operation-local. Optional pure protocol exclusions
intersect those decisions. `RequireUnconditional` lets the existing context-free MCP resolver refuse
a declared conditional type closure without inventing ambient authority.

`FieldAccessResultFilter` runs last in ordinary MVC result-filter execution. It prepares typed
ObjectResult/JsonResult and supplies native buffered Newtonsoft output. It preserves standard
resolver settings, including null naming strategy and original ShouldSerialize behavior. A declared
object/interface gap requires guarded serialization too: a custom formatter, resolver or converter
cannot use an incomplete closure as proof of unrestricted output. Governed or unresolved contracts
refuse unbuffered output. Fully known ordinary typed contracts retain their existing options.
Unprepared governed runtime members refuse inside the operation resolver. Custom raw MVC results
remain application dataflow; shared Entity emit and short-circuit know their entity type and refuse
raw/pre-serialized replacements when conditional fields are declared.

The native Newtonsoft input wrapper prepares the declared model type and demands whole-value write
admission before binding, including omitted and constructor-bound members. Explicit Access on a JSON-ignored property remains
in write-admission metadata while ordinary JSON ignoring stays intact. Entity replacement and
bulk paths repeat preflight for protocol-neutral calls. JSON Patch remains partial: test reads path;
copy reads from and writes path; move also writes from; add/replace/remove write path. Ancestors and
declared descendants are checked through the existing Newtonsoft member/wire contract. Caller
filter/sort and DeleteQuery admission precede trusted server Filter composition. Existing parser
limitations still apply, including unsupported field aliases in a parser that requires CLR paths.
Built-in map/dict checks ID and display source fields before discarding their member identity.

Known member denial uses `web.fieldAccess.denied`/403. Unsupported caller intent uses
`web.fieldAccess.unsupported`/400; native MVC body refusal is a model-state 400. A serialization
configuration failure uses the host's exception response and emits no partial buffered value.
These mechanisms do not guard direct persistence, arbitrary custom query code, copied unannotated
values, custom serialization outside MVC, or a directly executed IActionResult that bypasses MVC
filters. Static OpenAPI is a conditional superset, never a principal-dependent cached contract.

A counterpart after-fetch denial preserves its status while discarding selected rows, count, page,
access headers and manifest. This includes status-only 409 and prevents stale read metadata from
surviving a rejected page. Count and page still do not imply a cross-command snapshot.

Property decisions bind the actual containing contract type, including inherited declarations. The
protocol prepares after EntityRequestContextBuilder establishes trusted origin, then binds its policy
once to the context. Reuse validates root type and the captured principal identities/claims. MVC input
and result preparation use that same builder for origin semantics. Final shape admission runs after
BuildOptions, using the adapter's stronger exclusions when present.

Partial JSON and JSON Merge Patch require whole-value write admission. JSON Patch is the supported
selective field-edit protocol. The existing normalizer's empty-object omission and unescaped alias path
behavior are separate Data findings, not repaired by this slice. No extra patch walker is introduced.

## Typed merge/partial application (AE-16)

`EntityEndpointService.Patch` applies JToken patches through the Data.Core typed applicators and
assigns the returned working copy; the stored row is never edited in place. Applicator refusals
(identity or family-discriminator edits, ambiguous duplicate-cased members, malformed payloads,
policy-rejected nulls, and non-convertible values) surface as a corrective 422 with the applicator's
message after `BeforePatch` and before stamps, `BeforeSave` and any save. Dry-run rehearsals project
the restored copy and persist nothing. Field admission, row constraints, hooks, stamps and audit are
unchanged; JSON Patch keeps its existing typed path.

AccessProjection's internal generic envelope retains the actual payload CLR type. Collection relationship
responses retain RelationshipGraph<T> arrays. For that existing graph family alone, preparation obtains
parent and child types from IRelationshipMetadata. It never examines graph values or maintains another
registry. Existing arbitrary unannotated custom projection semantics remain application-owned.

Guard qualification conservatively includes nonsealed object contracts even when the output root is
known by value.GetType(), plus object/interface and ISerializable slots. This can reject a custom
formatter/resolver on a plain nonsealed DTO. The explicit boundary avoids another preparation mode or
unsafe inference about retained polymorphic values. Exact sealed, fully known unrestricted contracts
keep their native formatter path. Native ProblemDetails converters remain usable because their nested
values return through the guarded serializer; retained unprepared governed extension values still refuse.

EntityController.DefaultCollectionView belongs only to collection initialization when the caller omits
a nonempty view. EntitySummaryController supplies summary before shared hooks. Explicit view=full,
keyed/model responses and their headers retain the existing full behavior.
