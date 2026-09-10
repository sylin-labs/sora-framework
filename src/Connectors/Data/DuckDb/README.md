# Sylin.Koan.Data.Connector.DuckDb

DuckDB is Koan's embedded analytical reference adapter: an in-process OLAP engine for aggregation,
bulk load, and lakehouse file queries, with the ordinary Entity experience on top. It complements —
never replaces — the transactional store: SQLite (or any elected provider) remains the system of
record while DuckDB answers the aggregation-shaped questions.

## Install

Reference the connector **and** the native rider (the connector carries only managed bindings; the
engine is a per-RID payload you choose explicitly):

```powershell
dotnet add package Sylin.Koan.Data.Connector.DuckDb
dotnet add package Sylin.Koan.Data.Connector.DuckDb.Native
```

For an application-owned store, reference the packages and use the normal bootstrap:

```csharp
builder.Services.AddKoan();

var todo = await new Todo { Title = "Ship" }.Save();
var open = await Todo.Query(item => !item.Done, ct);
```

The zero-configuration target is `.koan/data/Koan.duckdb`. DuckDB creates it on first elected use;
merely loading the connector does not touch disk. In-memory sources are served from an ephemeral
scratch store under `.koan/tmp/duckdb/` that dies with the host.

## One writer per file — by composition

DuckDB allows exactly one process to hold a database read-write; on Windows even read-only opens are
excluded while a writer holds the file. The adapter treats that as composition, not a caveat: paths
anchor to the content root (unrelated applications never share a store), a second writer is
classified as an ownership conflict (non-retryable) by the failure classifier and surfaced by the
health probe, and multi-tenant isolation is expressed as Database-mode routing — **one file per
routed tenant**, which is the supported shared-nothing posture.

## Analytics on the store you already have

Declare the `sqlite_scanner` extension to attach an existing SQLite database and aggregate over it.
Pre-install the extension, or explicitly enable `AutoInstallExtensions` to install declared extensions before LOAD:

```csharp
// via raw instruction on any DuckDB-routed source
await Data.Source("analytics").Execute(
    "ATTACH '.koan/data/Koan.sqlite' AS app (TYPE sqlite)");
var rows = await Data.Source("analytics").Query<long>(
    "SELECT COUNT(*) FROM app.todos");
```

Declared analytics — named, materialized, agent-callable questions — live one layer up in
`Sylin.Koan.Data.Analytics` (DATA-0123).

## Inspect and name useful reads

The provider-neutral source vocabulary works here exactly as on SQLite:

```csharp
var source = Data.Source("Legacy");
var page = await source.Inspect().Containers(100, ct: ct);
var shape = await source.Inspect().Describe(customer, ct);
```

Named reads are enforced by the engine: a read lane opens `BEGIN TRANSACTION READ ONLY`, so a lane
cannot write. Lane SQL uses DuckDB's parameter spelling — `$name`, not `@name`:

```csharp
builder.Services.AddKoan(koan =>
{
    koan.Data.Source("Legacy").Query("customers.active", query => query
        .Lane("Reports")
        .Sql("SELECT customer_no AS Id, display_nm AS Name FROM customer WHERE active = $active")
        .Parameter<bool>("active"));
});
```

## Engine settings

| Option | Meaning |
|---|---|
| `MemoryLimit` | DuckDB `memory_limit` (e.g. `"2GB"`). The engine defaults to 80% of system RAM — embedded hosts should set this. |
| `Threads` | Engine threads; unset uses all cores. |
| `AutoInstallExtensions` | **`false` by default.** Extension binaries are never downloaded from DuckDB's CDN at runtime unless explicitly enabled. |
| `ExtensionDirectory` | Preloaded extension directory for air-gapped installs (`INSTALL` once, then `LOAD` offline). |

## Honest envelope

- **Single writer per file** across processes; Database-mode routing (one file per tenant) is the
  scale-out posture.
- **Bulk writes ride the Appender-shaped multi-row path**; per-entity `Save()` is correct but not the
  engine's fast path (~1M rows/s bulk vs ~10k rows/s row-at-a-time).
- **Schema policy** is create + validate with repair: most `ALTER` statements work, constraint
  changes do not, and document-expression mapped indexes are **declined** while planner matching is
  unproven (`SupportsRewriteFreeExpressionIndexes = false`).
- **Generated identity** is a sequence-backed column (`DEFAULT nextval(...)` + `RETURNING`).
- Storage format is backward-compatible, not forward-compatible; the native version is pinned by the
  rider package, and `STORAGE_VERSION` pinning is available in options via the connection string.

## Limits

Configuration decides participation; unsupported requests reject before provider work with a named capability and a correction. Provider-specific limits live in the package's TECHNICAL.md.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Rows or columns that hold numeric enum values are not rewritten automatically; they require an explicit
migration to the string representation.
