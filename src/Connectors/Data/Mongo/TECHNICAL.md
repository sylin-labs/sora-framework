---
uid: reference.modules.Koan.data.mongo
title: Koan.Data.Connector.Mongo - Technical Reference
description: MongoDB gold-reference adapter for Koan Data.
packages: [Sylin.Koan.Data.Connector.Mongo]
source: src/Connectors/Data/Mongo/
last_updated: 2026-07-29
---

## Contract

- Provider `mongo` (alias `mongodb`, priority 20) is registered by `AddKoan()`.
- `MongoAdapterFactory` is the sole route and repository authority. It also supplies source inspection and registered
  pipeline execution without manufacturing an Entity repository.
- `MongoClientManager` owns one bounded, reusable `MongoClient` per configured connection/database route and resolves
  discovery intent once before publishing that route's database handle.
- One `MongoRepository<TEntity,TKey>` implements managed and explicit-mapping behavior through one compiled
  `MongoEntityPlan`; there is no compatibility repository or alternate execution stack.

## Storage representation

Managed entities are serialized by an adapter-owned Json.NET configuration, converted recursively to native BSON, and
stored with `_id` as the physical identity. Camel-case member names and Koan's polymorphic discriminator are stable
adapter decisions. `DateTime` and `DateTimeOffset` are normalized to UTC BSON dates; `TimeSpan`, `DateOnly`, and
`TimeOnly` use comparable deterministic encodings.
Typed Json.NET tokens flow directly into BSON without reparsing JSON text, retaining CLR integer widths,
decimal precision and binary token identity. Legacy numeric widths remain readable when values fit the model.
The read path likewise hydrates from typed tokens. Nested JSON documents retain native dates, binary and
representable decimal values through replacement writes. URI values preserve their original escaped strings.

An explicit `MappingPlan` replaces naming conventions with compiled physical bindings. Reads hydrate through those
bindings. Writes use `$set` for each declared path and `$setOnInsert` for a mapped `_id`, preserving unbound fields and
unbound siblings inside a structured document. Managed documents use the cheaper whole-document replace path.

Mapped collections cannot also accept an ambient container partition. Explicit maps reject framework-managed row
fields because that combination cannot preserve both external ownership and hidden scope values without another
declared physical binding.

## Query and mutation execution

`IInsertOnlyRepository.Insert` supports ordinary managed `_id` storage. Its native `UpdateOne` matches only `_id`
and uses only `$setOnInsert` with upsert enabled. A match performs no modification and returns a conflict without
reading the existing document. An acknowledged upsert receipt with the submitted BSON identity proves insertion.
Unacknowledged writes reject before entity persistence; uncertain driver failures remain failures. Unrelated
unique-key violations are not converted to identity conflicts. Concurrent duplicate-key failures, if returned by
the server instead of a match receipt, remain failures rather than guessed commit outcomes.

Explicit mappings do not advertise this capability and reject insert-only execution, since their identity fields
do not necessarily have a native unique index. Ordinary `Save` and mapped upsert behavior remain unchanged.
Constrained bulk requests containing create-classified items reject before persistence. This contract does not
close the separate race between authorization of an existing row and its subsequent ordinary update.

`MongoQueryCompiler` lowers the declared filter floor, nested canonical paths, exact sort prefixes, explicit pages,
and counts to driver definitions over physical BSON names. Unsupported CLR residuals do not enter the repository.
Identity batches are bounded; bulk writes are ordered and require acknowledged driver receipts.

Guarded upserts include Koan's managed-field write scope. A duplicate `_id` produced by an attempted cross-scope upsert
is surfaced as a corrective cross-scope failure. Conditional writes combine identity, caller predicate, and managed
scope in one native operation.

Mongo batches use one ordered `BulkWrite`. They intentionally advertise no atomic execution capability; atomic or
idempotency requirements reject before mutation. The connector does not infer topology support or replay an ambiguous
commit.

## Collection and index realization

`MongoSchema` is a retryable, host-bounded gate per physical collection. `Managed` creates a missing collection and
realizes declared indexes. `External` verifies existence and performs no DDL. Managed conventions and explicit mapping
plans both lower their index paths through the same physical decisions used by reads and writes. TTL declarations use
MongoDB's native zero-second expiry index.

For unnamed declarations, an existing index satisfies the declaration when ordered keys, uniqueness, TTL,
collation (including collection defaults), coverage and visibility agree. Its historical name is retained.
Explicit names must match. Incompatible definitions fail with guidance to perform an explicit migration;
Koan never drops or renames them automatically. Only missing indexes are created. Resolution and names are
cached in the existing repository/physical-collection readiness gate, shared by concurrent callers and retried
after failure. Reuse is reported at Debug level once during initialization, without further index I/O on reads.

Duplicate business-key violations retain the Mongo driver error. A duplicate is described as a cross-scope
write only when a scoped identity lookup proves the existing identity lies outside the current write guard.

## Inspection and registered operations

Inspection exposes database-relative `StorageAddress` values, collection/view traits, effective read/write operations,
bounded pagination, and bounded samples. MongoDB collections have no promised fixed shape, so `Describe` reports no
synthetic schema. A sample builds a neutral union of observed top-level fields; each record preserves missing separately
from null, retains duplicate-name occurrences by ordinal, and converts binary, temporal, and nested BSON to the neutral
value algebra. Inspection reads raw BSON so the driver's ordinary document deserializer cannot discard or reject legal
duplicate elements; raw buffers are disposed immediately after the bounded neutral copy is complete.

The native status facet issues a non-creating `ping` through the route-owned client and returns only stable, redacted
readiness codes. Cutover combines that proof with complete collection inventory; a provider-limited inventory is never
treated as complete.

`MongoPipelineBinding` stores validated JSON stages and the target collection. It rejects `$out` and `$merge` during
composition and therefore carries `ValidatedRead` proof. Execution parses immutable stages, structurally substitutes
declared `{{parameter}}` values as BSON, appends a provider bound, and returns neutral records or an exact one-record,
one-field scalar receipt.

## Discovery, readiness, and resources

Options materialization normalizes configuration without provider I/O. On first use, default `auto` connection
resolution asynchronously uses `IServiceDiscoveryCoordinator`; explicit `zen-garden://` intent is required and
fail-closed. Concurrent callers share one resolution, while cancellation abandons only the caller's wait. Concrete
native endpoints bypass discovery. Health probes use the same route and client owner as data operations. Referencing
the package alone does not create a MongoDB client or start discovery.

Route clients and per-repository collection gates are bounded. Driver clients are reused for the host lifetime and
disposed with the host. Collection enumeration, identity batches, pipeline results, and inspection samples all have
explicit bounds.

## Capability truth

The adapter declares native LINQ/filter execution, provider-bounded paging, bulk upsert/delete, conditional replace,
and insert-only availability qualified by the repository's identity shape,
TTL indexes, and row/container/database isolation. It does not declare `AtomicBatch` or `FastRemove`. Query receipts
report only work completed by MongoDB.

## Verification

The focused `MongoInsertOnlySpec` proves native BSON preservation after identity conflict, one committed insertion
under eight concurrent attempts, unrelated unique-index failures, and mapped-identity rejection before persistence.
It also proves that default numeric keys reject before inserting an `_id: 0` document.

The connector project and test project build with zero warnings. The real MongoDB 8.3 suite passes 40/40, covering
managed CRUD, filtering convergence, comparable values, identity types, partitions, routing, discovery, health,
instructions, batching, capability truth, managed-field isolation, explicit legacy mapping, read-only/external policy,
lossless bounded inspection, registered pipelines, pure options, single-flight first use, and cancellation-safe
discovery. Canonical Forge also executes 11 exact MongoDB rows, including provider-specific D-01 through D-05.

- [Data adapter development primer](../../../../docs/architecture/data-adapter-development-primer.md)
- [Adapter responsibility map](../../../../docs/architecture/data-adapter-responsibility-map.md)

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
