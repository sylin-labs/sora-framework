# Sylin.Koan.Data.Relational.Npgsql

Shared Npgsql repository mechanics for Koan's PostgreSQL-wire Data providers.

## Install

Application developers should install a concrete provider instead:

```powershell
dotnet add package Sylin.Koan.Data.Connector.Postgres
```

Provider authors implementing another compatible PostgreSQL-wire connector can reference the mechanism directly:

```powershell
dotnet add package Sylin.Koan.Data.Relational.Npgsql
```

## Meaningful result

A concrete provider maps its resolved endpoint, identity, schema policy, and naming convention to
`NpgsqlRepositoryOptions`, then returns `NpgsqlRepository<TEntity,TKey>` from its normal `IDataAdapterFactory`. The
repository supplies the shared Entity CRUD, query, paging, batch, isolation, and schema behavior without borrowing or
activating another provider connector.
The constructing provider supplies its naming capability directly, so an inactive target repository never derives its
table name from whichever provider currently owns the application's default route.

Managed Id+object storage and explicit physical maps both compile into `NpgsqlEntityPlan<TEntity,TKey>` and execute
through the same repository. Scalar names, object roots, nested `jsonb` paths, composite keys, and generated keys are
plan differences—not alternate repositories. Nested writes preserve values outside the declared map.

## Guarantees and limits

- This package contains no `KoanModule`, discovery adapter, provider election, health contributor, or startup report.
- Referencing it alone activates no Data provider and opens no connection.
- Concrete providers retain ownership of configuration, source routing, identity, discovery, and operations reporting.
- The immutable repository options carry the compiled `DataSourcePlan`; External DDL and read-only writes therefore
  remain denied below provider composition as well as at the framework facade.
- The mechanism assumes compatible Npgsql/PostgreSQL-wire SQL behavior. Provider-specific differences must be explicit
  options or remain in the concrete connector; compatibility is not inferred from protocol alone.
- `NpgsqlStableOrder` makes the one current ordering delta explicit: PostgreSQL may use its physical tuple identifier,
  while providers without that system column order by the compiled identity roots.
- Warm entity plans are compiled once per bounded storage route; query translation and native parameter binding remain
  on the provider hot path without reflection-driven repository selection.

See [TECHNICAL.md](TECHNICAL.md) for the ownership boundary.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
