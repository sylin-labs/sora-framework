# Sylin.Koan.Data.Relational

Shared relational execution for Koan Data providers: compiled mapping consumption, symbolic command planning,
definition-level schema governance, SQL translation, parameter encoding, and ADO helpers.

## Application use

Applications normally reference a concrete connector, not this package directly:

```powershell
dotnet add package Sylin.Koan.Data.Connector.Sqlite
```

Provider authors who need the functional relational owner can reference it explicitly:

```powershell
dotnet add package Sylin.Koan.Data.Relational
```

The application remains Entity-first:

```csharp
builder.Services.AddKoan();

public sealed class Todo : Entity<Todo>;
```

The selected connector supplies its endpoint, schema, and provider-specific SQL. Koan applies the connector's
route-local DDL and matching policy automatically on first meaningful use.

## What this package owns

- `IRelationalSchemaOrchestrator`, the single owner of every relational schema decision - which columns a
  mapping implies, which indexes are worth building, whether the environment consents to automatic DDL, and
  whether a difference is fatal - working from the same `MappingPlan` the connector's own commands use;
- `RelationalCommandPlanner`, which turns a `MappingPlan` into complete get/query/insert/update/delete/patch/CAS plans;
- mapped filter translation that uses the same physical path and codec as writes and indexes;
- restricted filter-to-SQL translation shared by relational connectors;
- comparable scalar JSON encoding and AOT-clean ADO command helpers.

It does not elect a Data provider, open a connection, activate PostgreSQL/SQL Server/SQLite/CockroachDB, or expose an
application-facing relational registration call. Cross-module contracts live in
`Sylin.Koan.Data.Relational.Abstractions`; PostgreSQL-wire repository mechanics live in the module-free
`Sylin.Koan.Data.Relational.Npgsql` package.

## Guarantees and limits

- Schema policy and resolved storage identity are supplied per selected provider/source route; connectors do not share
  a mutable global schema decision.
- `StorageLifecycle.External` is an absolute DDL ceiling even when a relational `AutoCreate` policy was supplied.
- Selective reads, mapped/expression indexes, rewrite-free behavior, and TTL remain unproved until the provider's
  executable feature seam says otherwise.
- Schema creation is additive. This package is not a destructive migration engine.
- Unsupported query expressions reject rather than silently scanning an unbounded source.
- A concrete connector and reachable database are required for persistence; this package alone provides no backend.

See [TECHNICAL.md](TECHNICAL.md) for provider-author contracts and supported translation boundaries.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
