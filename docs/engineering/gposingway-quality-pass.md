# Koan quality pass driven by a brownfield application

The maintainer has authorized a broad quality pass while upgrading Gposingway. Keep both repositories' remote
publication deferred until the fixes and consumer verification are complete. Use existing machine resources.

## Acceptance worklist

| Contract | Evidence required | State |
| --- | --- | --- |
| Enum persistence | Raw storage contains names for scalar, nullable, nested, and collection enums; queries use the same representation | In progress |
| Entity fidelity | Existing identities, custom collections, private setters, dates, nulls, and polymorphic values survive save/read | Custom collection regression passes; broader audit pending |
| HTTP authority | Controller status and authorization precede automatic rendering; GET and HEAD preserve missing-content responses | OpenGraph focused suite: 41 passing |
| Composition | Real AddKoan host, package references, startup facts, health and lockfile agree | Consumer runtime verification pending |
| Durable work | Lifecycle registration, retries, scheduling, and idempotency preserve the consumer's business transitions | Pending |
| Public projections | REST, MCP, SSE and localized content preserve authorization and visibility | Pending |
| Storage and identity | Existing media routes, profiles, keys and OAuth callbacks work on the current package surface | Pending |
| Release | Focused owner tests, consumer journeys and final release checks pass; public packages restore independently | Pending |

Do not infer storage compatibility from a serializer round trip or a query that uses the same faulty codec.
Assert physical types and values independently. Do not mark an unavailable backend as tested.

## Enum scope and ownership

**Task:** Restore enum names as the default persistence contract throughout Koan Data.
**Application intent:** A package upgrade preserves readable domain states and the ability to query existing rows.
**Public expression:** Existing Entity models and Save/Get/Query through AddKoan and the selected connector.
**Guarantee/correction:** Koan-owned enum serialization and comparands preserve names as strings. Do not silently
normalize the regression into a supported numeric format. Explicit external codecs remain deliberate contracts.
Undefined values must not silently produce numeric writes. Any repair of already affected rows is a separate,
reviewable operation; changing a serializer is not a migration.
**Complete intent surface:** No extra options, attributes, serializer registration or application workaround.
**Public concepts:** No new application concept. Prefer an existing scalar conversion owner; add a shared internal
encoding helper only where multiple layers need identical string spelling and validation.
**Docs read:** Mongo README/TECHNICAL own BSON and mapping fidelity. Relational README owns parameter/schema
consistency. CLAUDE owns pillar boundaries and corrective failures. The Mongo identity matrix claims string enums
but did not inspect BSON.
**Code read:** MongoEntityPlan and MongoValues encode writes and filters. EntityJsonSerialization owns shared JSON.
StructuredValuePlan and NeutralDataValue normalize mapped values. CouchbaseDocumentPlan rewrites comparands.
SqlParameters and relational dialects independently choose numeric forms and column types.
**Reusing:** Existing compiled mapping plans, Entity JSON wiring, storage naming, query convergence tests, and live
Mongo fixture with its existing-endpoint override. No new services or databases are required for the application.
**Creating new:** Raw BSON enum cases in the Mongo test suite; shared JSON representation cases in Data.Core;
additional owner tests as each conversion path is characterized.
**Coalescence:** Rebuild default enum conversion at Data's existing scalar/serialization owners. Keep explicit
external codecs. Delete numeric fallback query logic proposed before the maintainer clarified the strict contract.
**Ergonomics:** Model enum names remain visible in stored data with ordinary Entity operations.
**Constraints satisfied:** No new routes, no production writer, no bulk data rewrite, no provider replacement,
no private host identities or data in evidence. Tests use the existing resources.
**Risks:** Relational enum columns currently have numeric DDL in several connectors. Correcting default encoding
must also correct schema and comparands, and report incompatible existing schemas rather than silently altering
them. Custom converter names and flags require consistent spelling. Numeric rows from the regressed encoder need
an explicit inventory and repair plan.

The first complete run passed 495 Data.Core tests and exposed enum ordering divergence in Mongo, SQLite and DuckDB.
String storage must preserve CLR enum ordering through native query expressions. Native flag ordering must reject
correctively until arbitrary named combinations can be translated; flag round trips remain supported.
`EnumStorageEncoding` in Data.Abstractions owns shared spelling/validation and declared ordinal evidence;
`RelationalEnumOrder` owns SQL CASE expressions, with BSON and SQL++ expressions staying in their adapters.

## Existing-resource verification findings

**Task:** Make declared DuckDB extension installation effective and make database conformance repeatable.
**Application intent:** Configured capabilities work on a fresh machine and test results do not depend on a fresh server.
**Public expression:** Existing DuckDb Extensions and AutoInstallExtensions options; unchanged AddKoan and Direct calls.
**Guarantee/correction:** With installation enabled, declared extensions are installed before LOAD. With it disabled,
LOAD retains the corrective pre-install message and performs no download. A database conformance run scopes its own
records while proving the same cross-source isolation.
**Complete intent surface:** No new option. The existing explicit runtime-install choice remains authoritative.
**Public concepts:** None.
**Docs read:** DuckDb README's extension policy requires explicit consent; TECHNICAL owns connection configuration.
**Code read:** DuckDbConnections passes the allow-list to each connection but only toggles engine auto-install flags;
the explicit LOAD fails even when AutoInstallExtensions is true. DuckDbCapabilitySpec reproduces this on a fresh
extension cache. AodbConformanceSpecsBase uses shared routed databases and assumes they start empty, so its second
run sees the first run's rows.
**Reusing:** Existing validated extension identifiers, typed options, connection wrapper, and test partition lease.
**Creating new:** A captured installation flag on the existing wrapper; no new type or service.
**Coalescence:** Fix extension realization at the DuckDB connection owner. Fix repeatability in the shared test oracle,
without clearing existing databases or weakening cross-source assertions.
**Ergonomics:** The declared option becomes sufficient; no manual engine command is required of the application.
**Constraints satisfied:** No production access, no new service, no destructive cleanup, no new remote operation.
**Risks:** Extension download errors remain real failures. Synchronous DuckDB driver installation may block Open;
the existing driver offers no asynchronous native execution guarantee here.

Follow-up characterization found two additional missing guarantees. Mongo hydration appends stored list elements
to constructor defaults: a raw document with one audit entry reloads as two. Put replacement hydration in
EntityJsonSerialization.Apply, the shared owner, and prove it through repeated real Mongo save/read cycles.
Additive custom collection factories must clear constructor seeds or fail rather than duplicate them. Existing
CtorSeededCollectionRoundTripSpec had proved the relational path only.

DuckDbOptionsSetup also omitted AutoInstallExtensions and ExtensionDirectory entirely. Bind their existing scalar
and Engine forms using project constants, reject an invalid boolean correctively, and test the resolved options
through AddKoan before the existing native extension journey. No new option or consent is introduced.

Compiler review also found nullable contracts that disagreed with existing behavior. AnalyticsParameterBinder
already accepts an absent value dictionary, so its annotation must say so. Materialized analytics reads require
the state object whose timestamp established freshness. PostgreSQL missing-database provisioning must reject an
absent configured database name before opening the maintenance connection. These belong to their existing owners;
no new surface is required. Analytics README/TECHNICAL establish bounded declared questions and corrective failures;
Npgsql OpenOrCreate is the sole provisioning path. Remove duplicate Analytics imports in the same cleanup.

## Background configuration

**Task:** Honor the existing background-service configuration through AddKoan.
**Application intent:** An operator can disable background activity before starting the host.
**Public expression:** Koan:BackgroundServices:Enabled=false, optionally per-service settings, plus AddKoan.
**Guarantee/correction:** Disabled services do not execute; ordinary configuration binding reports invalid values.
**Complete intent surface:** Existing configuration only, with no application DI workaround.
**Public concepts:** None.
**Docs read:** Core README owns host lifecycle; Core contributor law requires configuration to express real intent.
**Code read:** KoanBackgroundServicesBootstrap resets defaults without binding the section. The orchestrator already
honors resolved Enabled and per-service options. BackgroundServiceConfigurationSpec fails through a real generic
host: Enabled remains true despite configured false.
**Reusing:** Existing option initializers, section constant, standard BindConfiguration and the existing orchestrator.
**Creating new:** One host-level regression spec, using Microsoft.Extensions.Hosting in the existing Core unit suite.
**Coalescence:** Replace redundant default registration with binding at Core's existing bootstrap owner.
**Ergonomics:** The advertised configuration becomes sufficient; application composition remains AddKoan.
**Constraints satisfied:** No extra service, alternate lifecycle, new option, remote operation or production writer.
**Risks:** Settings that were previously ignored become effective. That is the required correction; document it.

After binding was corrected, the host test exposed a second lifecycle defect: the concrete orchestrator singleton
and the IHostedService registration constructed different instances. The resolved singleton had no ExecuteTask
even after the host started. Use the standard hosted-service factory to resolve the existing singleton, and assert
that the hosted and directly resolved objects are identical before checking disabled execution.

## Analytics test ownership and current SDK

The full analytics run exposed an invalid startup oracle: it starts the refresh loop before seeding
records, so the first materialization may correctly contain zero rows for its six-hour interval.
Build the test host, seed the record store, then start the host and wait for actual refresh state.
The suite also loses its IntegrationHost wrappers, leaving SQLite pools and background loops alive.
Use the existing async-disposal contract in every owning test. No provider cleanup workaround is needed.
Testing.Hosting README/TECHNICAL specify caller ownership after successful startup; the suite violates it.
The public analytics behavior remains unchanged by these test corrections.

The existing machine has .NET 10 SDK 10.0.401, while global.json requires exactly 10.0.302. Retain the
minimum SDK version and permit latestFeature within .NET 10, matching the app's existing policy. This
uses the installed supported SDK for normal repository tooling without installing a parallel runtime.
No package version, target framework, or release mechanism changes.

## Verified checkpoint before consumer dogfood

- Core unit: 120 passed, including disabled background execution and hosted singleton identity.
- Data.Core: 496 passed, including enum names, aliases, flags and replacement collection hydration.
- Relational: 27 passed; Mongo: 42 passed against the existing server, including raw BSON assertions.
- SQLite: 49 passed; DuckDB: 56 passed, including declared extension installation and option binding.
- Analytics: 36 passed after repairing test host ownership and seeding before scheduled startup.
- OpenGraph: 41 passed at the preceding controller-authority commit.
- Couchbase, MySQL, SQL Server and Firebird compile without warnings. Native backend execution for
  those four and PostgreSQL is not yet verified on this machine; this checkpoint makes no such claim.

Local dogfood packages are temporary inputs for the existing Gposingway checkout. Remote release is
explicitly authorized but deferred until the application pass is complete. Public-package restoration
and the normal dependency-stamping/release boundary remain required before final acceptance.

## Ordered response enrichment

**Task:** Preserve all ordered response hooks when an earlier hook replaces the payload.
**Application intent:** A catalog page carries likes, collection membership and claim markers together.
**Public expression:** Existing ordered IEmitHook<T> implementations returning EmitDecision.With.
**Guarantee/correction:** Each replacement becomes the next hook's input. Explicit context ShortCircuit
stops processing and wins over a simultaneous replacement. A plain short-circuit payload remains intact.
**Complete intent surface/public concepts:** Existing hooks, order and decisions only; no new option or type.
**Docs/code read:** Web README/TECHNICAL own response shaping across governed endpoints. HookRunner currently
returns immediately for Replace, contrary to the app's three ordered enrichers and EmitDecision's pipeline
contract. DefaultEntityHookPipeline shares that runner for REST and MCP. HookContext already owns stops.
**Reuse/coalescence:** Fix the two emit loops at their existing Web owner. No application orchestration helper.
**Verification:** Characterize chained replacement, Continue and explicit stops for collection and model emits
in the existing Web.WellKnown friend suite, then exercise Gposingway's real endpoint enrichment.
**Risk:** Later registered hooks previously skipped will now execute as declared. Exceptions remain visible.

Web hook characterization failed all six cases before the fix. The corrected runner passes the complete
10-test Web.WellKnown suite, including replacement propagation and explicit-stop precedence.

## Endpoint hook authority

**Task/application intent:** A read hook can hide stale translated content, and mutation hooks can reject a
write before storage changes. **Public expression:** Existing IModelHook<T>, ICollectionHook<T> and
HookContext.ShortCircuit; unchanged Entity controllers and MCP tools. **Guarantee:** Every invoked hook's
stop result is honored before later enrichment or mutation. A post-write stop changes the response only;
it does not roll back a completed write, and the mutation audit must still be recorded.
**Docs/code evidence:** Web's shared endpoint contract owns REST/MCP parity. The app's real endpoint test
proves AfterModelFetch returns false but GetById ignores it. Source review finds the same discarded result
at BeforeSave, BeforeDelete, BeforePatch and post-write hooks. Collection fetch already handles it correctly.
**Reuse/coalescence:** Existing ModelShortCircuit/CollectionShortCircuit helpers and the same endpoint owner;
no new application workaround or public concept. BeforeSave on a batch validates every row before any write.
**Verification:** Native endpoint calls in the existing InMemory adapter host prove read/new/expanded read,
save/batch/patch/delete stops and unchanged persisted state for pre-write rejections, including dry runs.
**Boundary:** This fixes invoked hooks. Bulk delete has its existing separate access/command contract.
No production mutation, new service, remote operation, or package version override is introduced.

Endpoint authority characterization failed all 11 pre-operation cases before the fix. The complete
InMemory Web adapter suite then passed 92 tests. Three additional post-write cases also pass, for
14 focused hook-authority cases; committed mutations remain recorded when response processing stops.
