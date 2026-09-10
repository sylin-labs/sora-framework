# Sylin.Koan.Data.Connector.DuckDb — technical notes

Companion to [README.md](README.md). Decision authority: [DATA-0123](../../../docs/decisions/DATA-0123-embedded-analytics-and-duckdb-connector.md).
Evidence: [ANL-0 spike](../../../docs/assessment/evidence/duckdb-connector-spike.md).

## Package split

`Sylin.Koan.Data.Connector.DuckDb` references **`DuckDB.NET.Data`** (managed ADO.NET only,
~235 KB). `Sylin.Koan.Data.Connector.DuckDb.Native` references **`DuckDB.NET.Bindings.Full`**
(per-RID libduckdb, ~330 MB uncompressed across five RIDs; floor RIDs linux-x64 / linux-arm64 /
win-x64 all covered). The split keeps hosts in control of their native footprint. Both versions
move in lockstep with the engine (1.5.5 at adoption).

## Instance identity

DuckDB engines are shared per (path + config) within a process: two connections on byte-identical
connection strings join one engine. `DuckDbConnections.Normalize` therefore (a) anchors relative
paths to the content root, (b) folds `DuckDbOptions` engine settings into every string
(`memory_limit`, `threads`, `extension_directory`, `autoinstall_known_extensions=false` by
default), and (c) strips SQLite-only keys (`Mode`, `Cache`, `Pooling` — with `Mode=Memory` selecting
the per-source scratch store). Splitting is quote-aware: a `;` inside a quoted value (a secret)
never fragments the string.

In-memory sources are **connection-private** in DuckDB (each open is an empty database), so they
are materialized as ephemeral scratch files under `.koan/tmp/duckdb/` with a keeper connection and
deleted on host dispose — same observable lifetime as SQLite's `:memory:`, stated plainly.

Non-creating reads do not rely on open modes: a file-backed source that does not exist refuses in
`Create` (`FileNotFoundException`) before any engine call, preserving the SQLite contract that a
look never becomes a write.

## Dialect notes (all probe-verified, ANL-0 R2–R6)

- **Parameters**: SQL spells `$p0`-style names; parameters are added with the bare name. The provider
  does **not** rewrite `@name`. Lane/raw SQL must spell `$name` — `RelationalSourceIntegration` and
  `DirectSession` bind by logical name (prefix stripped), which every ADO.NET provider matches.
- **Upsert**: `ON CONFLICT (keys) DO UPDATE SET col = excluded.col` (requires a PK/unique — the
  schema always declares one). Nested document merges use **`json_merge_patch`** with a patch built
  from the written paths; DuckDB has no `json_set`.
- **Generated identity**: `CREATE SEQUENCE` + `INTEGER PRIMARY KEY DEFAULT nextval(...)` + `RETURNING`.
- **Bulk**: multi-row `VALUES (...), (...)` merged per dispatch (single write shape per dispatch);
  outcome-requiring rows (generated identity, managed-scope guards) execute per-row with `RETURNING`.
- **Ordering**: DuckDB sorts NULL last ascending — the reverse of SQLite and of the framework sorter —
  so every ordered term states `NULLS FIRST`/`NULLS LAST` explicitly.
- **JSON reads**: nested numeric/enum/TimeSpan reads `CAST(col -> '$.path' AS DOUBLE)`; text reads use
  `->>`; Object-shaped reads (array aggregation sources) stay JSON for `json_each`/`json_type`.
  Collection containment compares `json_each(...).value = to_json($p)` — JSON-to-JSON, since a raw
  VARCHAR comparison would attempt to parse the parameter as JSON.
- **Catalog**: `information_schema.columns` + `duckdb_constraints()` replace `PRAGMA table_info`
  (the pragma's rewritten table-function form parses its argument as a qualified name and rejects
  CLR-qualified/partition-suffixed storage names). Index inspection via `duckdb_indexes()`.
- **Types**: document columns are `JSON`; BLOBs hydrate from the provider's `UnmanagedMemoryStream`;
  `TimeSpan` stores as ticks (`BIGINT`) per DATA-0100; enum values use their declared names in both
  parameters and stored documents.

## Failure classification

- Second-writer / file-lock open failures (`Cannot open file ... used by another process`) classify
  as ownership conflicts — non-retryable, health-degrading.
- Optimistic-concurrency `Transaction conflict` errors classify as retryable.
- Extension-not-installed failures are corrective: the message names `INSTALL` + the connector's
  preload options.

## Capability envelope (declared)

Everything `SqliteFeatures` declares, plus `Write.MutationOutcomes` (`RETURNING`). Deliberately
undeclared: `query.fastCount`/`query.optimizedCount` (conservative v1), vector/FTS (upstream VSS
remains experimental), and `SupportsRewriteFreeExpressionIndexes = false` — document-expression
mapped indexes are declined until planner matching is demonstrated.

## Conformance

`tests/Suites/Data/Connector.DuckDb/` — 49 specs mirroring the SQLite suite: AODB conformance (all
three isolation modes; Database mode = one file per routed tenant), filter/sort convergence with the
in-memory oracle, DATA-0100 comparable-encoding convergence, provider-bounded paging, source
integration and read lanes, cold restart, connection lifecycle (including per-source memory
isolation), boot provenance, health contributor, and the connection-string redaction guard.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Rows or columns that hold numeric enum values are not rewritten automatically; they require an explicit
migration to the string representation.

AutoInstallExtensions and ExtensionDirectory bind from the provider section or its Engine subsection.
With AutoInstallExtensions enabled, each declared extension is installed before LOAD; otherwise LOAD uses only
preinstalled binaries and fails correctively when they are missing.
