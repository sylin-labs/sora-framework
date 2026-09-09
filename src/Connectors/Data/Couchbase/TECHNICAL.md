---
uid: reference.modules.Koan.data.couchbase
title: Koan.Data.Connector.Couchbase - Technical Reference
description: Greenfield Couchbase adapter for Koan Entity and source integration.
packages: [Sylin.Koan.Data.Connector.Couchbase]
source: src/Connectors/Data/Couchbase/
last_updated: 2026-07-28
---

## Contract

- Provider identity is `couchbase`, alias `cb`, priority 30.
- `AddKoan()` registers options, discovery, the adapter/source-integration factory, one host-owned
  resource pool, and participation-aware health.
- The adapter implements one current repository path. The retired document store, once gate,
  cluster provider, query wrapper, translator, telemetry, and options configurator do not remain as
  compatibility paths.
- Namespace is Couchbase scope; Container is collection; named source selects bucket/connection and
  policy; ambient Container partition selects a scope for managed entities.

## Execution

Known-key Get/Upsert/Delete use the KV service. Bulk methods use bounded concurrency of 16. Managed
whole documents use Koan's polymorphic JSON serializer and managed-field injector. Explicit maps use
the shared immutable `MappingPlan`; mapped replacement is read-CAS-patch so undeclared external
fields survive.

Set operations compile the framework filter AST to parameterized SQL++. Query results report exact
filter, sorting, paging, and count receipts. Queries use `RequestPlus` scan consistency. Unsupported
operators, multi-binding scalar uses, or sorts reject rather than fall back to an unbounded scan.

Conditional replace reads the document, evaluates the logical guard, and replaces with the opaque
CAS token. CAS mismatch and missing documents return `false`. Configured SDK durability is applied to
Upsert, Insert, Replace, Remove, and CAS mutation variants.

The adapter does not claim atomic batches. A non-atomic batch uses the ordinary repository methods;
`RequireAtomic` and idempotency keys reject. This avoids replaying application delegates under the
SDK transaction retry model.

## Mapping and lifecycle

One Couchbase map may bind scalar `.Name(...)`, structured `.Object(...)`, nested `.Path(...)`, and
composite `.Parts(...)` values. Document keys are derived from the application identity and bounded
to Couchbase's 250-byte key limit; oversized keys use a deterministic SHA-256 key. Provider-generated
keys reject at mapping compilation.

An explicit map pins one scope/collection and cannot also accept an ambient container partition.
Explicit maps with framework-managed record fields reject because the external record cannot safely
host an undeclared isolation/discriminator contract.

Managed lifecycle creates a missing scope/collection and a primary query index when a set operation
first needs it. External lifecycle only validates existence and performs no management mutation;
query indexes remain external authority. ReadOnly is enforced by the shared `DataSourcePlan` before
KV mutation. Lifecycle and access are independent.

## Registered operations and inspection

The provider-neutral `SqlOperationBinding` represents SQL and SQL++ native leaves. Couchbase supports
registered record and scalar reads only when the operation declares a configured read lane. The lane
connection is selected by the provider and every statement uses SDK `Readonly(true)`, bounded record
limits, operation timeout, and named parameters. Mutating SQL++ therefore fails at the provider.

Inspection lists scope/collection containers with bounded offset continuations, resolves and
describes addresses, and samples with a bounded `LIMIT take + 1` query. Document collections do not
claim a fixed record shape. Read-only policy removes Write from effective inspection operations.

## Resource and readiness ownership

`CouchbaseResourcePool` is a singleton owned by the Koan host. It is bounded to 128 cluster identities
and keys clusters by connection string plus credentials, allowing multiple buckets and entity
containers to share one SDK cluster. Bucket handles are cached inside the cluster resource.

First selected use waits once for KV and Query, primes the initial bucket, and probes `SELECT RAW 1`
with a bounded provider-readiness retry. Repository operations are not replayed. A failed connection
entry is removed so a later operation can attempt a fresh resource. Successful resources are disposed
once with the host; disposal exceptions are not silently swallowed.

## Configuration

Default options live under `Koan:Data:Couchbase`; `ConnectionStrings:Couchbase` is also recognized.
Named sources use `Koan:Data:Sources:{name}:Adapter`, generic connection/policy keys, and provider
settings under `Koan:Data:Sources:{name}:couchbase:{Bucket,Scope,Username,Password}`. Read lanes use
`Koan:Data:Sources:{name}:ReadLanes:{lane}:ConnectionString`.

`Durability` is parsed once into `DurabilityLevel`; accepted values are `None`, `Majority`,
`MajorityAndPersistToActive`, and `PersistToMajority`. Query/bootstrap timeouts and polling intervals
are typed `TimeSpan` options.

## Claims

The factory declares LINQ, native filter execution, provider-bounded paging, bulk upsert/delete,
conditional replace, and Row/Container/Database isolation. It does not declare atomic batch,
provider-generated identity, fixed document schema, resumable streams, or snapshot traversal.

## Evidence

The greenfield suite passes 25/25 with zero skips against `couchbase:community-8.0.2` and
`CouchbaseNetClient` 3.9.4. The observed complete run took 52 seconds after build. It covers CRUD,
filter convergence, exact receipts, polymorphism, provider-bounded streaming, CAS, all declared
isolation modes, resource sharing, compact flat/object/nested/composite mapping, preservation of
external fields, managed/external/read-only policy, generated-key rejection, named SQL++ reads,
mutation rejection, neutral inspection, identifier safety, health, and participation.

The retired baseline passed 17/21 in 8 minutes 11 seconds. Its four failures were ordinary query,
filter convergence, polymorphism, and streaming receipt failures. Those results are retained only as
negative acceptance tests in the provider suite.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.

Conditional replacement consumes the normalized Filter, qualifies it through CompileConditional, and
retains the observed native CAS fence. Managed, binary and DateTime guard fields refuse before document
reads because the CLR evaluator does not establish the required comparison meaning for those shapes.
