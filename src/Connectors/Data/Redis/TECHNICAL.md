---
uid: reference.modules.Koan.data.redis
title: Koan.Data.Connector.Redis - Technical Reference
description: Redis keyed Entity persistence with bounded managed sets and read-only Functions.
packages: [Sylin.Koan.Data.Connector.Redis]
source: src/Connectors/Data/Redis/
---

## Executable profile

- Direct Redis string commands for keyed reads and writes; ordered `MGET` preserves positional get-many results.
- Native per-key TTL from a single `[Index(Ttl = true)]` `DateTime`/`DateTimeOffset` property.
- Optimistic conditional replacement through Redis transaction conditions (`WATCH` semantics).
- Bulk upsert/delete with a configured maximum; Entity batches do not claim atomicity or idempotency.
- Row, container, and database isolation. Managed fields live in JSON; partitions compile into distinct logical sets;
  routed sources may select distinct Redis logical databases.
- Full Entity filter correctness over a bounded Koan-owned membership set, reported as `FilterExecutionKind.Scan` with
  bounded candidates. This is not native filtering and does not earn provider-bounded paging.

## Key and registry layout

```text
koan:{route-hash}:record:{identity}
koan:{route-hash}:members
```

Safe short identities remain readable; other identities use URL-safe base64. Record and registry keys share a Redis
Cluster hash slot. Managed writes transact the record and membership together. Expiration can leave stale membership;
bounded reads ignore missing records and remove stale members only on ReadWrite sources. Exceeding `MaxQueryEntries`
rejects before member materialization. External sources never create or consult `members`.

## Configuration

| Setting | Default | Meaning |
|---|---:|---|
| `Koan:Data:Redis:Database` | `0` | Redis logical database |
| `Koan:Data:Redis:MaxQueryEntries` | `10000` | Maximum owned-set cardinality accepted by query/count/clear |
| `Koan:Data:Redis:MaxBulkEntries` | `1000` | Maximum entries accepted by get-many/bulk/batch |
| `Koan:Data:Redis:NamingStyle` | `EntityType` | Unmapped container naming style |
| `Koan:Data:Redis:Separator` | `_` | Unmapped naming separator |

Settings may be overridden under `Koan:Data:Sources:{source}:redis:*`. The host owns at most 128 distinct endpoint
multiplexers and rejects further routes rather than growing an unbounded process-lifetime pool.

## Source Integration

Redis declares registered record/scalar Functions and no portable inspection capability. Functions require a read
lane, dispatch with `FCALL_RO`, pass explicit keys through `KEYS`, pass parameters through `ARGV`, and apply
`RecordSetLimits`. Infrastructure owns `FUNCTION LOAD`, upgrades, and removal.

## Operational boundaries

- Redis transactions serialize queued commands but do not roll back runtime command errors; Koan does not advertise
  atomic Entity batches.
- Redis Cluster does not support logical database selection beyond database zero.
- Eviction and persistence are deployment policies, not adapter durability claims.
- `AllStream` and `QueryStream` reject because owned-set materialization is not provider-bounded paging.
- Health performs `PING` only for participating sources through their resolved shared connection.

## References

- [DATA-0107 provider-bounded Entity streams](../../../../docs/decisions/DATA-0107-provider-bounded-entity-streams.md)
- [DATA-0110 compact data adapter language](../../../../docs/decisions/DATA-0110-compact-data-adapter-language.md)

Conditional replacement consumes a normalized Filter and qualifies it with CompileConditional before
reading the document. Managed, binary and DateTime guards refuse. Immediate-expiry payloads also refuse
before reads; they cannot take an ordinary delete outside the observed-value transaction condition.
Existing bounded provider transaction retries remain internal to one Entity dispatch.
