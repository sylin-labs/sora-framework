# Firebird adapter — provider contract and operations

Technical reference for maintainers. Status: **supported** (`firebird-data-provider`); conformance proven against
`firebirdsql/firebird:5.0.4` with `FirebirdSql.Data.FirebirdClient` 10.3.4, .NET 10, Windows host,
2026-08-29. The adapter rides `Koan.Data.Relational` (mapping compilation, filter translation,
schema orchestration, readiness coordination, `AdoCommands`/`SqlParameters`) and owns only Firebird
translation and execution.

## Verified provider facts (probe log)

| Fact | Value |
|---|---|
| Wire auth | Srp plugin required on the client; server default is Srp256-only → set `AuthServer="Srp256, Srp"` |
| Wire encryption | FirebirdClient cannot do `Required` → `WireCrypt=Enabled` |
| Admin password env | `FIREBIRD_ROOT_PASSWORD` (image ignores `ISC_PASSWORD`) |
| Config env | `FIREBIRD_CONF_<Key>` with the conf file's exact casing |
| Identifiers | double-quoted, case-exact, 63-byte cap; dots legal inside quotes |
| Upsert | `UPDATE OR INSERT INTO … VALUES … MATCHING (id)` + optional `RETURNING col` (singleton) |
| Paging | `OFFSET n ROWS FETCH NEXT m ROWS ONLY` (requires the ORDER BY every query already carries) |
| NULLS ordering | `NULLS FIRST/LAST` supported (4.0+); placement always stated explicitly |
| JSON functions | none — drives the shadow-column design below |
| Native binds | Guid↔`CHAR(16) OCTETS`, `bool`, `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`; **no** `DateTimeOffset` (encoded text) |
| Missing database | connect fails `FbException` code `335544344` (isc_io_error), SQLSTATE 08001 → `CreateDatabase` under managed consent |
| Duplicate key | code `335544665` (isc_no_dup), SQLSTATE 23000 → cross-scope write classification |
| Read-only lane | `FbTransactionBehavior.Concurrency \| Read` — engine-enforced |
| TRUNCATE | absent — `RemoveStrategy.Fast` lowers to `DELETE FROM` |
| Oversize strings | "string right truncation" corrective error, never clipped |
| DDL concurrency | transactional DDL; concurrent `CREATE TABLE`s deadlock → executor serializes behind one gate |

## Runtime map

| Concern | Owner |
|---|---|
| Route resolution, read lanes, naming capability | `FirebirdAdapterFactory` (+ `AdapterConnectionResolver`) |
| Dialect (quote, parameters, LIKE escape, path reads) | `FirebirdDialect` |
| Plan per entity (SELECT, hydration, shadow columns, encoded scalars) | `FirebirdEntityPlan` : `RelationalEntityPlan` |
| Capability declaration | `FirebirdFeatures` (scalar-only `FilterSupport`) |
| Store features for the schema owner | `FirebirdStoreFeatures` (all optional features false) |
| Schema DDL, catalog describe, database creation | `FirebirdDdlExecutor` (RDB$ system tables; DdlGate) |
| Repository surface | `FirebirdRepository` (`AdoCommands`/`SqlParameters`) |
| Inspection (list/resolve/describe/sample) | `FirebirdInspector` |
| Source Integration | `RelationalSourceIntegration` over `FirebirdConnectionFactory` |
| Health, discovery, options | `FirebirdHealthContributor`, `FirebirdDiscoveryAdapter`, `FirebirdOptions(Configurator)` |

## Shadow columns (the design decision)

The shared mapping stores each entity as `(Id, Json)`. With no JSON functions, nothing inside the
document is reachable by SQL, so `FirebirdEntityPlan.ShadowColumns` enumerates every top-level
scalar document path plus the registered managed discriminators; `FirebirdDdlExecutor` creates them;
`Insert`/`UpdateSet` write them from `plan.ShadowValues(entity)` (the same bindings, encoded by the
same `EncodeScalar` the comparands use); `FirebirdDialect.Read` answers a single-segment nested path
with the quoted column. Consequences:

- scalar filters, sorts, declared indexes and managed-isolation predicates are store-enforced;
- deeper paths refuse (`NestedPaths=false` declared; the coordinator routes them to the floor);
- collection-shaped bindings (e.g. `List<string>`) stay document-only; their operators are absent
  from `FilterSupport.CollectionOperators`, and running them records a `koan.data.query.fallback`
  fact — the residual is declared, visible, and convergent;
- tables created before a managed field was registered lack its column; the first scoped write then
  fails naming the missing column (corrective). This adapter is new, so no upgrade population exists.

## Evidence

- `Koan.Data.Connector.Firebird.Tests` — 14 specs, all green (two consecutive runs): AODB record-plane
  conformance (isolation declarations + row/container/database realization, streaming fail-closed,
  polymorphic roots), full filter-convergence corpus, scalar pushdown guard, residual-fact honesty,
  scalar ordering convergence, paged windows, no-fallback scalar pages, capability truth, boot
  provenance.
- Reasoned non-hosted cells: `FilterConvergence.AssertPushesDownAsync` and
  `SortPushdownConvergence.AssertConvergesAsync/AssertStreamsAsync/AssertNothingFallsBackAsync`
  (collection lowering and streaming are declared limits — the hosted specs prove the declaration and
  the fail-closed path instead).
- Packaging: `dotnet pack` with the release-train version; package id `Sylin.Koan.Data.Connector.Firebird`.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
