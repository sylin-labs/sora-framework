---
uid: reference.modules.Koan.data.inmemory
title: Koan.Data.Connector.InMemory - Technical Reference
description: Process-local Entity data adapter and conformance oracle.
packages: [Sylin.Koan.Data.Connector.InMemory]
source: src/Connectors/Data/InMemory/
---

## Contract

`InMemoryAdapterFactory` registers direct provider `inmemory` with priority `-100`. `memory` is a compatible
provider alias. It does not declare itself an automatic floor. Selection follows Data.Core's normal context, Entity
attribute, configured source, direct-reference, automatic-floor, and priority rules; the connector adds no separate
registration API.

`Runtime/InMemoryRepository<TEntity,TKey>` is a thin backend over the shared key-value store contract.
`Runtime/InMemoryState` owns one finite host-local registry, partitioned by routed source, Entity root, and ambient
partition. Values are serialized with Data Core's root-aware Entity codec and materialized anew on every read. The
store is internal implementation state; process exit discards every dictionary.

## Capabilities

`DataCaps.Write.InsertOnly` is implemented with `ConcurrentDictionary.TryAdd` on the physical identity
map. Managed-field snapshots and Entity family serialization are retained; no preliminary read is used
to prove absence. The existing conditional-replace RowGate is not part of this insertion guarantee.
Default identities reject before native dispatch; the ordinary Entity write plan generates string/Guid
keys, while numeric keys require an explicit non-default value.

The repository declares:

- `FilterExecutionProfile(InMemory, SupportsBoundedCandidates: true)`;
- bulk upsert;
- bulk delete; and
- ordered batch execution without an atomic guarantee.

It does not declare `DataCaps.Query.ProviderBoundedPaging`. Its query path starts from the resident
full-source dictionary, so slicing a numbered page is not evidence of provider-bounded traversal.

The common key-value family supplies managed-field guards, isolation modes, instructions, and the
provider-neutral Entity repository contract. This connector does not infer remote durability,
distributed atomicity, or production recovery from those shared semantics.

The factory publishes the same executable claims and rejects two inapplicable source decisions rather than silently
weakening them: physical `Map<T>` declarations and `StorageLifecycle.External`. `RequireAtomic=true` rejects before
dispatch because the KeyValue batch has no single all-or-nothing native boundary.

## Streaming boundary

- `AllStream` and `QueryStream` fail correctively with `QueryStreamRejectedException` before yielding;
  there is no complete-result materializing fallback.
- Use `All`/`Query` only for known-small test sets. Use `FirstPage`/`Page` to limit the result returned to
  test code, without inferring an unbounded-data performance guarantee.
- A later resident-incremental implementation must earn a separate capability claim through shared
  conformance before these Entity streams become available.

## Concurrency and isolation

Each physical store is a `ConcurrentDictionary`. Warm lookup is lock-free; only creation misses enter the reservation
gate. The host owns at most 4096 source/root/partition stores; exceeding the bound rejects before another store is
published. Writes serialize detached snapshots and reads deserialize fresh values, so POCO references are never the
persistence boundary. Individual key operations and the connector's batch contract are process-local. There is no
cross-process coordination, durable journal,
replication, backup, or restart recovery.

`EntityContext.Partition` changes the physical store. Routed data sources also remain distinct. Test
hosts must still own their ambient `AppHost`; `Sylin.Koan.Testing` enters a flow-scoped host around
every inherited Entity battery.

## Evidence

`Koan.Data.Connector.InMemory.Tests` covers CRUD, filtering/capabilities, sorting, batch behavior,
instructions, isolation modes, partitions, host ownership, and managed-field no-leak behavior. The
current suite passes 56/56, including detached nested-object/collection mutation, root/variant round trips, and the
finite host-state boundary.

## Unsupported

- persistence after process exit;
- multiple processes or nodes;
- production backup/recovery;
- unbounded-data performance claims; and
- parity with a provider capability the connector does not declare.

## References

- [DATA-0107 provider-bounded Entity streams](../../../../docs/decisions/DATA-0107-provider-bounded-entity-streams.md)
- [Entity access and streaming](../../../../docs/guides/data/entity-access-and-streaming.md)

Conditional replacement compares the observed immutable stored record in ConcurrentDictionary.TryUpdate.
An ordinary Save/Delete between observation and exchange therefore prevents a stale replacement. It
never inserts a missing row. The old conditional-only RowGate has been removed. Guards use the existing
normalized evaluator's CompileConditional admission, which rejects managed, binary and DateTime fields;
ordinary query evaluation is unchanged. This is finite host-local storage, not durable coordination.
