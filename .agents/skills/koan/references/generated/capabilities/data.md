---
type: REFERENCE
domain: data
title: "Data"
audience: [ai-agents, developers]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  date_last_tested: 2026-08-23
  status: passed
  scope: docs/capabilities/data.md - route table verified against leaf targets
---

# Data

Entities are the application's vocabulary: persistence is the floor, and relationships, lifecycle,
contexts, HTTP, trust, work, AI, files, and agent projections attach to the same noun. The code
keeps saying `Recipe` while everything underneath changes.

## Route by need

| The request says | Fetch |
|---|---|
| "where should data live?" - first store, or a change | [entity stores](data/entity-stores.md) |
| "orders belong to customers" - things owned by things | [relationships](data/relationships.md) |
| "publish approved records to another audience" | [named sources](data/named-sources.md) |
| "we outgrew SQLite" - move without rewriting verbs | [store cutover](data/store-cutover.md) |
| "users delete things by accident" - undo | [recoverable deletion](data/recoverable-deletion.md) |
| "emails/keys/regions keep arriving dirty" - trim and casing | [field hygiene](data/field-hygiene.md) |
| "backups / disaster recovery" | [backups](data/backups.md) |
| "search by meaning" | [semantic search](ai/semantic-search.md) |
| "what can an `Entity<T>` become?" - the full hook map | [Entity capability hooks](data/entities.md) |
| "create only if absent, or replace only the revision I read" | [Data write contracts](../../src/Koan.Data.Core/README.md) |
| "change a few fields and preserve the rest" | [PATCH an Entity](../guides/patch-capabilities-howto.md) |

## Standing constraints

- The same Entity verbs on every store. Adding a store never moves data - moving is cutover's
  explicit job.
- Same syntax is not the same guarantees: read the store node's boundary section before
  promising behavior.

## Do not, at this level

- Do not pre-create databases, schemas, or wiring - provisioning is on demand.
- Do not add a second store "for flexibility" without a named outcome.

For the one-screen maturity view, see [Data in the capability map](../reference/capability-map.md#data).
