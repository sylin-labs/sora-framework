---
uid: reference.modules.Koan.data.core
title: Koan.Data.Core - Technical Reference
description: Contracts, options, design and operations for the Koan data core.
packages: [Sylin.Koan.Data.Core]
source: src/Koan.Data.Core/
last_updated: 2026-07-27
validation:
  date_last_tested: 2026-07-15
  status: reviewed
  scope: public Data.Core source inventory and current capability contracts; adapter behavior remains suite-owned
---

## Contract

- Inputs/Outputs
  - Core interfaces and helpers for aggregates (IEntity<TKey>, conventions, helpers)
  - Generic facades as second-class helpers: `Data<TEntity, TKey>`
- Options (selected)
  - Paging defaults and limits (via typed Options)
  - Naming and constants policy (see ADR ARCH-0040)
- Error modes
  - Validation and adapter errors surface as exceptions; adapters may wrap provider errors
  - Common host-backed Entity/Data operations use `KoanHostContextException` when no host is active,
    the selected provider is disposed, or a required Data service is absent
- Success criteria
  - Predictable materialized paging plus explicit capability negotiation for provider-bounded streams

## Key types and surfaces

- Primary abstractions: `IEntity<TKey>`, helpers and extensions for data operations
- Extension points: adapter/provider implementations consume these primitives

## Patch operation ownership

- Data accepts one provider-neutral `PatchPayload<TKey>` made of `PatchOp` values.
- Web, MCP, and future protocol projections own request documents, media types, and normalization into that shape.
- Data Core owns patch execution, lifecycle, persistence, and result semantics; adapters do not parse HTTP payloads.
- The contract boundary has no ASP.NET Core dependency and no parallel object-shaped patch request path.

### Typed merge/partial applicators (AE-16)

- `MergePatchApplicator`/`PartialJsonApplicator` expose `ApplyToCopy(target)` only: they serialize the target with
  the Entity document serializer, admit and merge the supplied members into that document, and restore it through
  `EntityJsonSerialization` (the same materialization persistence reads with). The input target is never mutated;
  a refused, malformed, or non-convertible patch throws before any state exists to save.
- Admission is native Newtonsoft contract metadata (writable, non-ignored members with the document's wire names,
  `JsonObjectContract`/`JsonDictionaryContract`/`JsonArrayContract` item shapes) and applies recursively to every
  supplied object, dictionary entry, and array element, including newly created subtrees. Patch admission stays
  distinct from restoration: a private setter the document restores is not permission for a caller to assign it.
- Dictionary keys are exact data: never case-aliased and never defaulted. A merge null removes the
  dictionary entry (RFC 7386 removal is representable per key). Typed members under merge-null take
  an explicit CLR default (non-nullable) or explicit null, so constructor seeds do not resurface.
  `MergePatchNullPolicy.Reject` refuses nulls that target non-nullable members;
  `PartialJsonNullPolicy` keeps SetNull/Ignore/Reject as documented.
- Entity identity (`Id` under any wire alias) and the family discriminator (`__koan_type`) are refused, at any
  family node, by name and by resolved member. A new subtree over an abstract/interface member type refuses:
  the concrete shape comes from stored state, never caller input. `JToken`/`object`-typed members are raw caller
  data and pass through unchanged.

## Context ownership

- `EntityContext` is the Data facade for source, adapter, partition, cache, and transaction routing.
- Its `ContextState` is one typed value in `Koan.Core.Context.KoanContext`, so nested Data scopes share
  Core's logical-flow restoration without making Data the owner of cross-pillar context.
- Tenancy, access subjects, and other modules use their own business-facing facades over
  `KoanContext`; `EntityContext` exposes no generic slice API.
- Durable context carriage is independently registered through `IKoanContextCarrier` and the
  host-owned `KoanContextCarrierRegistry`. The Data-axis DSL describes Data realization only; it does
  not register context carriers.

## Entity lifecycle ownership

- Entity static and receiver `Insert` forward to `Data<TEntity,TKey>.Insert`. Data validates the
  model/cancellation, selects the existing partition scope before repository resolution and awaits
  the existing `IInsertOnlyRepository` terminal inside that scope. Null inherits, empty selects
  default; source, adapter and isolation continue through the normal facade. There is no second
  capability or receipt interpreter. Single-entity Entity.Upsert (and its Save alias) accepts empty
  for default; the other bulk and conditional-change overloads retain their existing contract.
- The optional insert-only terminal enters the same source guard, operation horizon, write plan,
  managed write scope, and field transforms as Save. It negotiates support before provisioning
  readiness and uses upsert lifecycle with `Prior = null`, without reading a collision. Field transforms
  cannot change the dispatched identity. Only a verified Inserted/Committed receipt runs completion.
- Deferred coordination cannot supply an immediate atomic insertion receipt and rejects. Cache and
  Entity variants forward insertion through the existing semantic boundary. Generated identifiers
  propagate from detached storage payloads without replacing the plaintext application model.
- `MutationResult<TEntity,TKey>` remains authoritative: Inserted/Committed returns the application
  instance/key; proven identity collision is Conflict/NotCommitted with no entity. Native nonidentity
  constraint failures are not conflicts. Invalid/ambiguous receipts and post-commit completion errors
  propagate as failures, without rollback or facade retry. Source policy, lifecycle and provider
  qualification can refuse before native dispatch; BeforeUpsert effects are not transactional.
- Root and inherited variant statics retain root receipt typing; string-key receiver inference
  retains the exact variant receipt. Runtime variants continue through the existing write plan and
  discriminator serialization. No Insert companion methods are generated; existing root identity
  collision semantics apply across variants.

- `TEntity.Lifecycle` declares persistence behavior inside `AddKoan(() => ...)` or Koan module
  registration. The builder is static syntax; every plan and handler list belongs to one host.
- The outer `RepositoryFacade` is the single execution boundary. Provider/module decorators sit
  inside it, so cache hits and generated REST/MCP operations cannot bypass Lifecycle, isolation,
  transforms, or storage guards.
- Registering an equal delegate instance repeatedly is idempotent. Distinct handlers retain FIFO
  order. A plan freezes on first inspection or execution and rejects late mutation correctively.
- `UpsertMany` preflights every before-handler before the first write, applies the same compiled write plan as a
  single save, then dispatches the prepared set through one native bulk call. Completion callbacks run only after
  the exact affected-count receipt succeeds.
- Query candidates cross an internal two-stage boundary: Data completes residual filter, fallback sort, total,
  page, and projection first; load Lifecycle observes only the final application-visible Entity rows.
- Remove Lifecycle is preserved by `Safe` and `Optimized`; explicit `Fast` is a deliberate bypass.
- `IDataDiagnostics.GetLifecyclePlansSnapshot()` and `koan.data.lifecycle.selected` facts expose the
  composed handler inventory without retaining runtime entities or service scopes.

## Aggregate configuration ownership

- One immutable `DataProviderCatalog` per host owns canonical IDs, aliases, direct-reference evidence,
  collision rejection, and memoized priority metadata. A provider factory carries no second `CanHandle`
  identity authority.
- `DataDefaultProviderPlan` compiles the default choice once. Repository construction, aggregate metadata,
  vector role correlation, diagnostics, startup facts, and the resolved lock consume that same provider ID
  or receipt; they do not independently rank DI registrations.
- Context/source/Entity/default precedence remains Data-owned. A named choice is required and fails closed;
  only record-to-vector correlation is preferred and may continue through Vector's own automatic policy.
- `DataSourceRegistry` compiles the elected adapter, typed `StorageLifecycle`, typed `Access`, a redacted
  connection identity, adapter settings, and route identity into one immutable host-owned `DataSourcePlan`.
  Runtime arguments may narrow that plan but cannot elevate it; the ordinary default is `Managed + ReadWrite`.
- `RepositoryFacade` applies the plan's typed operation-effect gate before cancellation observation,
  segmentation/guard callbacks, readiness, Entity lifecycle, or provider dispatch. A constrained source never
  enters the provisioning-ready seam, which serves `Managed + ReadWrite` alone.
- Known build provenance admits directly referenced connectors plus the deliberate JSON floor. A transitive
  factory cannot become persistence accidentally. Low-level hosts without a generated manifest use a
  deterministic priority fallback explicitly reported as `unknown-provenance-priority`.

- `AggregateConfigs.Get<TEntity,TKey>(services)` memoizes configuration and its lazy repository per
  `IServiceProvider`. Sequential or simultaneous hosts using the same closed Entity type do not share
  adapter factories, guards, read contributors, configurations, or repositories.
- Per-provider caches use weak provider keys, so the cache does not extend a host's lifetime. Values
  may safely close over their owning provider because the entire entry releases with that provider.
- `AggregateConfigs.GetRegisteredTypes()` exposes entity/key facts observed by the active host only. It never exposes
  another host or retains a provider beyond that host's weak-keyed catalog.
- `IDataDiagnostics.GetEntityConfigsSnapshot()` is host-owned and contains only configurations
  observed by that host. Aggregate resolution records into it directly; diagnostics do not reflect
  over private cache implementation details.
- `IDataDiagnostics.GetAdapterParticipationsSnapshot()` is a separate host-owned execution fact. Merely
  describing an Entity or its route does not activate an adapter; successful repository construction or
  a Direct connection request records the canonical provider and logical source idempotently.
- `AggregateConfigs.Reset()` remains available for test-matrix discovery cleanup, but repeated-host
  correctness does not depend on calling it.

## Data-adapter health participation

- Generic readiness state, initialization order, and monitoring ship in `Sylin.Koan.Core` under the
  `Koan.Core.Adapters` namespace. Data Core adds source semantics: provider option binding, default paging,
  and distinct host-owned single-flight stages for non-creating reachability, declared-shape validation,
  and explicitly authorized provisioning.
- `AdapterOptionsConfigurator<TOptions>` and `IAdapterOptions` live in
  `Koan.Data.Adapters.Configuration`; AI and other concerns do not implement a Data paging contract.
- `AdapterBootReporting` lives in `Koan.Data.Adapters.Reporting` and reports Data settings through
  Core provenance. Endpoint resolution delegates to Core's `ServiceDiscoveryReporting` chokepoint.

- `DataAdapterHealthContributorBase` distinguishes connector availability from application
  dependency. A provider participates when it wins default election or is selected by a runtime
  repository or Direct request in that host. A configured named source describes an available route;
  it remains non-gating until an operation actually selects it.
- An available but inactive provider returns `Unknown`, remains non-critical, and must not open a
  connection or mutate backing infrastructure.
- An active provider probes every participating source and is critical. Provider implementations
  retain ownership of the physical probe. A probe must be non-creating; provisioning is a separate
  `Managed + ReadWrite` action and is never inferred from a business-operation failure.
- Selection uses `AdapterResolver`, `DataSourceRegistry`, and `IDataDiagnostics`; connector health
  implementations must not introduce a parallel configuration hierarchy or fallback election.

## Direct physical routing

- Source- and adapter-routed Direct sessions ask the selected provider factory to resolve the
  physical connection, so normal provider configuration and autonomous discovery remain provider-owned.
- `WithConnectionString(...)` is the explicit escape hatch: its value is used literally and never
  reinterpreted as a source or replaced by a configured default. Blank values and `auto` reject before
  provider I/O; use source or adapter routing when Koan should resolve intent.
- A literal connection override retains the named/default source ceiling. Query and scalar carry proven `Read`;
  opaque execute and transaction creation carry `Unknown` and therefore reject on every constrained source.
  Data never infers safety from a SQL prefix or result shape.

## Source Integration ownership

- `Data.Source(name)` resolves one immutable host-owned handle for an explicitly configured source and a matching
  `IDataSourceIntegrationFactory`. This catalog is separate from Entity repositories and has no automatic provider
  fallback for named external sources.
- `Describe()` and `Explain(operation)` are pure projections of the same frozen source, mapping, operation, descriptor,
  and claim plans used by execution. They never construct a provider integration or perform I/O.
- `Doctor(ct)` activates only a descriptor that explicitly supports Doctor, applies a Framework timeout distinct from
  caller cancellation, and accepts only documented non-mutating checks. Source integrations activate lazily and are
  disposed with their owning host.
- `Inspect()` applies the source read gate, capability checks, source/reference validation, bounded pages, signed
  host-scoped continuation envelopes, descriptor-shape validation, and access-policy projection before native work
  escapes.
- Registered `Query`/`Scalar<T>` plans are declared during `AddKoan(koan => ...)`, reject duplicate source/operation
  declarations, and freeze binding kind, parameter types, lane name, bounds, timeout, effect, result, and delivery.
- Runtime validation resolves the lane against the immutable source plan once per handle operation. An opaque binding
  requires `EnforcesReadLane`; an effective-read provider binding may execute without one. Active segmentation rejects
  because this application surface has no host/control-plane override.
- `RecordSetMaterializer` owns the one buffered pass, `MaterializedValueV1` accounting, first-limit completion,
  cancellation, provider partial truth, and reader disposal. Inspection samples, registered reads, and typed Direct
  ADO queries converge here.
- DTO projection is result-lifetime bounded and compiled by ordinal. The warm row path performs no member discovery,
  dictionary creation, JSON serialization, or provider-type conversion.
- `Koan.Data.SourceIntegration` activities contain only source, stable operation, provider, effect, result kind,
  attempts, duration, and result count. Parameter values and native payload text are excluded.
- Runtime facts, health, source diagnostics, TestKit applicability, and certification packets consume the exact
  deterministic references emitted by the adapter's inert `DataClaimSet`. The selected source's pure descriptor
  contributes its registered-read, record-result, and inspection profiles once; no diagnostic surface re-infers them.

## Mapping compilation ownership

- `DataSourceBuilder.Map<TEntity>` records one source/entity declaration in the active Koan composition. Duplicate
  declarations reject before host construction.
- `IDataMappingPlans` is host-owned and bounded by `Koan:Data:Mapping:PlanEntries`. It compiles reflection, conflict,
  constructor, member-access, structured-value, identity, codec, and index decisions once, then returns the same
  immutable `MappingPlan` on the warm path.
- Whole-object bindings compile read-only virtual sub-bindings for logical leaves. The root remains the sole write
  authority; filters/projections/indexes resolve exact structured physical paths without a second resolver.
- `MappingPlan.Use` warm-reuses one binding/use receipt per logical path and consumer. `Write`, `WriteIdentity`,
  `Hydrate`, selective `Read`, filter/order/patch/conditional/index use all reference those same bindings.
- `MappingConvention` supplies an explicit managed-store Id+Object default when no application map exists. Explicit
  source maps always win.
- Recursive object graphs reject with a correction; relationships and units of work do not leak into aggregate
  mapping. Mark relationship properties out of storage or map an intentional value subtree through a codec.

## Configuration

- Prefer typed Options for tunables; avoid magic values
- Centralize constants per project (see ARCH-0040)
- Structured query planning follows DATA-0096; bounded Entity streaming follows DATA-0107

## Query and mutation receipt ownership

- `FilterPushdownCoordinator` owns the final semantic order. `QueryReceiptValidator` rejects false or impossible
  filter, sort, pagination, projection, total, and count facts before Lifecycle or public return.
- Sparse Entity projection is the final Data-owned query stage and uses bounded, compiled member plans. Nested sparse
  object projection rejects correctively; neutral `RecordSet` projection is the intended complex-shape surface.
- `GetMany` normalizes one ordered nullable slot per requested identity and rejects unrequested identities.
- Exact mutation outcomes require `DataCaps.Write.MutationOutcomes` plus the native outcome seam. A returned receipt
  must carry the same key, a valid outcome, and a committed result.
- A batch is one-shot. Atomicity is prequalified through both the adapter capability and the created native batch;
  successful results must report committed execution and matching realized atomicity. Complete item results are
  validated in native positions, then remapped to logical builder order.
- The shared transaction coordinator is sequential deferred coordination only. Its failure reports completed
  operation count, commit outcome, retry disposition, and replay disposition; it does not claim native atomicity.
- Deferred entity and vector operations restore their captured source, adapter and partition exactly during
  route resolution and execution. A captured default clears the commit caller's corresponding routing axis.
  Executing a tracked operation suppresses the transaction to prevent recursive enlistment.

## Synchronous console-host ownership

- `IServiceCollection.StartKoan()` is the short synchronous facade over the standard .NET Generic
  Host. It composes Koan when needed, starts every hosted capability, and returns a provider facade
  without blocking for shutdown.
- The returned provider is caller-owned and implements `IDisposable` and `IAsyncDisposable`.
  Disposing it releases its process-default `AppHost` lease before owned services are torn down.
- If discovery or startup throws, `StartKoan()` releases the host lease and disposes the provider
  before rethrowing. A failed start does not leave a selectable ambient host.
- Overlapping providers are safe at the binding boundary: disposing an older provider cannot clear a
  newer owner. Use `AppHost.PushScope(provider)` when concurrent flows must select different providers.
- `StartKoan()` supplies the same `IHostEnvironment`, `IHostApplicationLifetime`, configuration,
  validation, and `IHostedService` lifecycle as other Generic Host applications. Web apps and workers
  use their native builders with `AddKoan()` rather than nesting a second host.

## Usage guidance

- In application models, expose first-class statics:
  - `Item.All(ct)`, `Item.Query(...)`, `Item.FirstPage(...)`, `Item.Page(...)`, `Item.QueryStream(...)`
- Reserve `Data<TEntity, TKey>` for cases where no first-class static exists
- Establish the runtime with a Koan generic host or `StartKoan()` before calling static Entity/Data
  operations; use the typed host-context failure to diagnose lifecycle versus composition errors
- For large sets, prefer explicit pages or a capability-qualified stream; avoid unbounded `All()`

## Explicit numbered paging

- `FirstPage(size)` and `Page(pageNumber, pageSize)` return one materialized page.
- Iterate page numbers until a returned page contains fewer than `pageSize` items.
- Supply an explicit stable sort when repeated or concurrent writes could otherwise change page
  membership. No provider-agnostic cursor/resume-token API exists today.
- Numbered paging limits the result returned to the caller. Some adapters may still perform
  in-memory fallback work; Koan does not yet promise universally bounded provider execution.

## Streaming semantics

- `AllStream/QueryStream` use one Data.Core coordinator to request numbered candidate pages lazily.
  They never invoke `QueryWithCount`; totals are not requested.
- A repository must advertise `DataCaps.Query.ProviderBoundedPaging`, enforce the requested page and
  complete total order, and report both honestly. Otherwise enumeration throws a corrective
  `QueryStreamRejectedException` before yielding instead of materializing the complete source.
- Qualification is determined from the selected adapter's executable capability and receipts, never from its name
  or storage family.
- `batchSize` bounds the Koan-visible candidate page, not opaque driver buffers. Consumer pace controls
  later page requests; cancellation and early disposal stop later work.
- A sort key that `TypeClassification.IsPortableStreamSortScalar` admits -- an enum, or `bool`, `byte`,
  `sbyte`, `short`, `ushort`, `int`, `long`, `decimal`, `double`, `DateTimeOffset`, `DateOnly`,
  `TimeOnly`, `TimeSpan` -- compares identically on every qualified adapter. Any other key still
  streams; `RecordProviderDefinedOrder` emits a fact naming the keys whose comparison the store defines
  rather than Koan, so the difference is discoverable instead of fatal.
- The one stream-sort rejection is an Entity-identifier sort whose key type is not the provider-stable
  `string`: that throws `QueryStreamRejectedException` before I/O, because the tie-break it would
  replace has no proven shape.
- After validating caller ordering, Koan appends the actual Entity identifier. The usual string key is
  an opaque provider-stable tie-break, not a CLR/cross-provider collation promise. A
  different business member named `id` does not suppress it, but models declaring both `Id` and `id`
  are not portable persistence models and are outside the qualified-adapter contract.
- The current `Int32` provider-offset contract rejects a page before I/O when
  `(pageNumber - 1) * pageSize` exceeds `Int32.MaxValue`. Numbered paging is not snapshot-consistent,
  mutation-safe, resumable, or cursor-based.

## Edge cases and limits

- Large result sets require explicit paging or a stream on a provider that earns
  `DataCaps.Query.ProviderBoundedPaging`
- Concurrency and batches → follow transactional batch semantics (see DATA-0007)
- Adapter capabilities vary. `DataCaps.Query.Filter` describes operator semantics;
  `DataCaps.Query.FilterExecution` carries `FilterExecutionProfile` (`Native`, `InMemory`, `Scan`, or
  `Unknown`). Do not infer backend pushdown from `DataCaps.Query.Linq` or `FilterSupport.Full`.

## Relationship negotiation

- `RelationshipGraphLoader` is the single scalar/set/stream graph-loading owner. It consumes
  `EntityCardinality`, preserves source order and multiplicity, batches parent keys through `GetMany`,
  and sends each child edge to `IRelationshipQueryExecutor` once per bounded source batch.
- `IRelationshipQueryExecutor` remains the child-edge execution owner used by Entity, graph, Web,
  and MCP paths. A batch of roots becomes one `Filter.In` query per edge.
- `RelationshipQueryPolicy.Strict` accepts native or already-resident InMemory execution and rejects
  scans or residual fallback. It is the default for existing Entity method shapes.
- `RelationshipQueryPolicy.Bounded(maxCandidates, maxResults)` explicitly accepts finite scan or
  residual work. Providers implementing `IBoundedQueryRepository` refuse before returning partial
  data when the candidate limit is exceeded.
- `RepositoryFacade` applies storage guards and managed/read-scope filters to bounded reads exactly as
  it does ordinary queries; field transforms are reversed before results leave the facade.
- `RelationshipQueryRejectedException` carries safe relationship/provider/reason/correction fields.
  The latest selected or rejected mode is recorded as `koan.data.relationship.execution` in the
  shared runtime-fact snapshot.
- This contract covers direct edges. Recursive graph planning, depth budgets, cross-key-type edges,
  index verification, and fleet-wide performance certification are not included.

## Stored-field transform compilation

- `StorageFieldTransformPlan` is one host-owned singleton. It snapshots DI contributors in stable order, rejects
  duplicate ids, and memoizes one immutable plan per Entity type.
- The write chokepoint shallow-clones the Entity once, then applies applicable transforms in order before repository
  I/O. Materialization applies transforms in reverse order before any supported Entity result leaves the facade.
- Data Core knows only the contracts in Data Abstractions. Classification is one consumer; future round-trip concerns
  must use the same seam rather than add process-static registries or adapter-specific hooks.
- `IFieldTransformInspector` exposes applicability and contributor ids for cache safety and Data-axis explanation. It
  does not expose transformed values or module-specific state.

## Observability and security

- Integrate with platform logging/tracing; propagate operation context
- Data exposure is adapter-specific; enforce auth/permission at the Web or service layer

## Design and composition

- Data.Core provides shared building blocks used by relational/document/vector adapters
- Composes with Web controllers via application models exposing first-class statics

## Deployment and topology

- No standalone runtime; shipped as a library used by apps and adapters

## Performance guidance

- Prefer Entity streams for consumer-paced large-set work on a qualified adapter; use explicit
  materialization or numbered pages when the selected adapter does not provide bounded streaming
- Batch operations according to adapter guarantees

## Compatibility and migrations

- Target frameworks: net10.0
- Adapter compatibility is capability-specific. Use the adapter matrix and runtime facts instead of
  treating package presence as proof of every Data.Core feature.

## References

- ADR ARCH-0040 - config and constants naming: `/docs/decisions/ARCH-0040-config-and-constants-naming.md`
- ADR DATA-0096 - unified filter pipeline: `/docs/decisions/DATA-0096-unified-filter-pipeline.md`
- ADR DATA-0107 - provider-bounded Entity streams: `/docs/decisions/DATA-0107-provider-bounded-entity-streams.md`
- ADR ARCH-0084 - unified capability model: `/docs/decisions/ARCH-0084-unified-capability-model.md`
- Engineering guardrails: `/docs/engineering/README.md`

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.

Entity JSON hydration replaces constructor-seeded members with stored collections. Additive domain collections
with public parameterless constructors and Add(T) round-trip as arrays; a nonempty constructor also needs public
Clear() so hydration cannot duplicate its defaults. Unsupported shapes fail with a corrective error.
The array constructor is owned by Core.Json.KoanJsonContractResolver and reused by typed transport
binding. Data retains the separate private-setter and family-discriminator overrides.
## Counterpart query execution

RepositoryFacade binds QueryDefinition.Partition before read scope and dispatch. For counterpart
queries it captures segmentation and legacy managed equality values once per operand, compares
isolation and route bindings, folds target read contributors, and captures the provider target
inside that target context. Target contexts end before native outer dispatch. Native execution
and reverse-order lease disposal remain inside the same async binding lifetime; operation leases
are not reactivated across async contexts. Nested counterparts and complete-filter residuals reject.

The existing facade IQueryRepository.Query entry uses the shared planner and receipt finalizer,
including no-count keyed reads. Data's raw candidate boundary avoids duplicate planning. Native
counterpart receipts carry execution-local evidence. Load lifecycle, relationship materialization,
and streams verify returned row references/identities and Data scope before release. Evidence does
not establish a shared count/page snapshot or permission for rows added by later Web hooks.

## Conditional replacement

Entity ReplaceIf lowers its lambda synchronously, enters the requested partition, resolves the
existing conditional repository and awaits it through completion. RepositoryFacade strictly snapshots
the Filter before its first await and requires a complete native row guard and conditional capability.
It rejects default/unstable keys, variants, classified/managed guard fields, residual/CLR/counterpart
nodes, active managed/read scopes and deferred coordination. Source policy/operation leases still apply.
Provider-specific mapping/value qualification may occur after advisory BeforeUpsert, before mutation.

The captured key and route must survive lifecycle and write preparation. One native dispatch returns
false for missing/conflict and true for an acknowledged match, including an identical replacement.
Only true invokes AfterUpsert. Before/Prior is advisory, not the atomic preimage. Exceptions after
native dispatch are preserved and can represent an unknown or confirmed commit; there is no retry,
rollback promise or conversion to false. Mutable in-flight caller models are not a concurrency API.
EntityVariantRepository masks conditional support until exact native membership is qualified.
