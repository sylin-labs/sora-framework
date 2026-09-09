---
uid: reference.modules.Koan.data.sqlite
title: Koan.Data.Connector.Sqlite - Technical Reference
description: SQLite gold-reference adapter for managed and externally mapped data.
packages: [Sylin.Koan.Data.Connector.Sqlite]
source: src/Connectors/Data/Sqlite/
last_updated: 2026-07-28
---

## Execution model

SQLite has one repository and one compiled mapping path. A managed Entity map is simply the framework-generated
`Id + Json` mapping; an explicit `Map<TEntity>` supplies another immutable mapping plan to the same executor. CRUD,
hydration, identity predicates, filters, sorts, counts, bulk work, conditional writes, and schema checks therefore
cannot drift between managed and legacy shapes.

There is no compatibility bridge, mapped-repository variant, adapter-local object mapper, or query compiler. The
host bounds compiled entity plans, memory-source keepers, batch items, and native parameters. Mapping accessors and
mapping-use decisions are compiled outside warm operations; mapped member access performs no reflection.

## Source integration

The adapter exposes registered SQL record/scalar operations and provider-neutral inspection of tables and views.
Container continuations are bounded, opaque after Data wraps them, and resumable. Sampling reads at most `take + 1`
native rows so it reports `Complete` versus `ProviderLimit` truthfully.
Repository plans derive container names from the constructing SQLite factory, including when SQLite is an inactive
cutover target behind a different active provider.

`SqlOperationBinding` is opaque. A registered SQL operation therefore selects a configured read lane. SQLite opens
that lane's connection and enables native `query_only` before executing the command; an effective write fails at
the provider boundary. Connection strings remain outside public plans and diagnostics.

## Mapping

An explicit map supports:

- one scalar or composite identity;
- provider-generated single-column identity;
- scalar physical names;
- structured object values stored as SQLite JSON text;
- scalar logical values at nested JSON paths; and
- symmetric codecs compiled by Data's mapping plan.

The same physical binding is used for hydration, parameters, filters, order expressions, conditional replace, and
writes. Sibling nested-path writes use `json_set` against the existing value, preserving unrelated legacy fields.
Whole-object bindings replace the whole declared object by design.

SQLite accepts an empty mapping namespace or `main`. Explicit mappings reject framework-managed row fields and
ambient partitions until those concerns have explicit physical bindings; this is a fail-closed boundary, not a
fallback to a shared container.

## Query guarantees

Supported Filter AST operations are lowered through `Koan.Data.Relational`. Filter, fully handled sort, numbered
page, and exact-count receipts reflect only provider-executed work. Provider-bounded candidate reads request one
extra row to prove whether the bound was exceeded. Collection operations use SQLite JSON functions.

Raw Entity queries are an explicit escape hatch. Registered named SQL is preferred for stable application intent
and is independently bounded by Source Integration limits.

## Schema and policy

Managed sources with `AutoCreate` create the required table on first use. External sources perform validation only
and never create, alter, or drop a physical object. `Access: ReadOnly` is enforced by Data before repository
construction or provider I/O. The two decisions are independent.

## Bulk and batch

Constrained single-item endpoint creates use `IInsertOnlyRepository.Insert`. SQLite executes `INSERT OR ABORT`,
overriding any external table replacement policy. Only a native primary-key violation naming the validated table
and identity columns produces a conflict receipt; other constraints remain failures. Zero affected rows, including
an external trigger's `RAISE(IGNORE)`, do not prove collision or a safe commit outcome. Schema validation requires
the declared identity to match the native primary key before dispatch. Provider-generated identities
use `RETURNING` and are propagated to the submitted entity and receipt. Ordinary `Save` remains an upsert.

The initial constrained bulk surface rejects any create-classified item before persistence; this insert contract
does not claim atomic mixed insert/update execution. Existing authorized-update races remain outside its guarantee.

`SqliteInsertOnlySpec` verifies physical row preservation, concurrent single-winner insertion, generated keys,
unrelated unique constraints, missing native primary keys, trigger ignore, and other-table trigger key failures.

`UpsertMany` emits one bounded multi-statement SQLite command inside one transaction and consumes each `RETURNING`
result in input order, including provider-generated identities. Oversized batches or parameter sets reject before
dispatch. `DeleteMany` uses one predicate command. The Entity batch surface uses one transaction and reports a
complete ordered outcome for every queued operation; mutate-by-id necessarily reads the target inside that same
transaction.

## Connection ownership

File connections use Microsoft.Data.Sqlite pooling. The host records only the exact pool groups it uses and clears
them on disposal. Private and named memory targets receive source-isolated shared-memory identities plus one
host-owned keeper connection, so per-operation connections observe the same database for that host lifetime.
Connection and provenance output are redacted.

## Limits

- Atomic insertion requires an assigned non-default identity or a mapped Generated identity; a default
  non-generated key rejects before dispatch.
- Repository operations are buffered; Entity streaming is coordinated as bounded numbered pages.
- Offset paging is not snapshot isolation or a resumable cursor.
- Explicit mapped containers do not combine with ambient partitions or managed row fields.
- SQLite attached databases are not inferred from mapping namespaces.
- Provider exceptions surface directly; commands are not replayed after failure.
- Native bulk accepts at most 4,096 items and 30,000 parameters per dispatch.
- A host accepts at most 256 distinct in-memory source targets and 512 compiled entity plans per repository.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
