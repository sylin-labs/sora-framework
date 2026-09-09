---
uid: reference.modules.Koan.data.sqlserver
title: Koan.Data.Connector.SqlServer - Technical Reference
description: SQL Server adapter for Koan data.
packages: [Sylin.Koan.Data.Connector.SqlServer]
source: src/Connectors/Data/SqlServer/
---

## Contract

- The adapter declares `DataCaps.Query.ProviderBoundedPaging` and applies numbered pages in SQL Server
  before candidate rows are materialized into application memory.
- Relational translation and schema helpers remain in Koan.Data.Relational.
- One `SqlServerRepository<TEntity,TKey>` and compiled `SqlServerEntityPlan<TEntity,TKey>` serve managed Id+object
  storage and explicit flat/object/nested/composite/generated maps.
- External lifecycle performs no DDL; read-only source policy rejects Entity mutation before provider I/O.
- `Inspect()` provides neutral table/view discovery, description, and bounded sampling. Registered SQL reads/scalars
  require a source-owned read lane and execute inside a rollback-only transaction; database grants remain the security
  boundary.

## Configuration

- Connection string, schema governance, naming, discovery, and readiness use
  the existing `SqlServerOptions` and adapter-readiness contracts.

### Options (typical keys)

- `ConnectionStrings:Default` (first-win)
- `ConnectionStrings:SqlServer`
- `Koan:Data:Sources:<name>:ConnectionString`
- `Koan:Data:Sources:<name>:ReadLanes:<lane>:ConnectionString`
- `Koan:Data:SqlServer:ConnectionString`
- `Koan:Data:SqlServer:Schema`
- `Koan:Data:SqlServer:NamingStyle` and `Separator`
- `Koan:Data:SqlServer:DdlPolicy`
- `Koan:Data:SqlServer:SchemaMatchingMode`
- `Koan:Data:SqlServer:AllowProductionDdl`
- standard readiness options under `Koan:Data:SqlServer:Readiness`

`ConnectionString=auto` attempts orchestration discovery before using the localhost development
fallback. The connector does not currently expose provider-specific retry or command-timeout options.

## LINQ and pushdowns

- Supported expressions follow `Koan.Data.Relational` translator. See: `xref:reference.modules.Koan.data.relational#supported-linq-subset`.
- Paging uses `OFFSET`/`FETCH`. Every caller-requested provider-bounded stream sort component must be a
  portable scalar -- an enum, or an integral, decimal, floating, or temporal type -- for its comparison
  to hold on every qualified adapter. Any other key still streams here; Data.Core records a fact naming
  the keys this store, rather than Koan, defines the order of. The one rejection is an Entity-identifier
  sort whose key type is not the provider-stable `string`. Data.Core appends that identifier as an
  opaque tie-breaker, which is not a CLR or cross-provider collation promise.

## Provider-bounded streaming

- `AllStream` and `QueryStream` are coordinated as lazy numbered pages by Data.Core.
- `batchSize` is the maximum Koan-visible candidate page, not a promise about opaque SQL client
  buffers.
- Deep or collection ordering rejects correctively before provider I/O rather than falling back to a
  complete-result sort.
- Offset paging is not snapshot isolation and does not provide mutation-safe traversal, resume tokens,
  or a public cursor.

## Error modes

- Provider errors (`SqlException`) surface to the caller; the connector does not add a retry policy.
- Unsupported predicates surface explicitly; simplify the predicate or materialize a known-small page.
  Provider-bounded streams may apply supported pointwise residuals but never hide a full-source fallback.
- An unreachable selected SQL Server reports unhealthy readiness and never falls back to a different
  data provider.

## Operations

- Health: connection open/round-trip.
- Configuration/discovery logs avoid introducing a second provider-selection path.

## Verification

- Connector suite: 33/33 against the pinned SQL Server 2025 container on 2026-07-28.
- Coverage includes CRUD/query/count/paging/batch, managed fields, compact external mappings, nested JSON preservation,
  composite/generated identities, policy enforcement, named SQL, neutral inspection, and bounded sampling.

## References

- [DATA-0107 provider-bounded Entity streams](../../../../docs/decisions/DATA-0107-provider-bounded-entity-streams.md)
- [Entity access and streaming](../../../../docs/guides/data/entity-access-and-streaming.md)

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
