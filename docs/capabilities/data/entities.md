---
type: REFERENCE
domain: data
title: "Entity capability hooks"
audience: [ai-agents, developers]
status: current
last_updated: 2026-08-29
framework_version: v1.0.0
validation:
  date_last_tested: 2026-08-27
  status: passed
  scope: cold-executed by an external agent on the SQLite path against published packages (App + Data.Connector.Sqlite) via store-and-expose — create/query/get over HTTP with persistence across restart, plus the fail-fast correction when a configured adapter is unavailable
---

# Entity capability hooks

Start here when a request names a business thing. `Entity<T>` is not merely a database row: it is the
shared application vocabulary that persistence, policy, HTTP, jobs, events, AI, storage, and agent
surfaces reuse.

```csharp
public sealed class Todo : Entity<Todo>
{
    public string Title { get; set; } = "";
    public bool Done { get; set; }
}

var todo = await new Todo { Title = "Ship it" }.Save(ct);
var open = await Todo.Query(item => !item.Done, ct);
```

## You need

| Piece | Package | Note |
|---|---|---|
| Koan application and Entity semantics | the application or foundation bundle | call `AddKoan()` once |
| One Entity store | [choose by topology](entity-stores.md) | ordinary Entity verbs need one elected route |
| Only the hooks the outcome needs | follow the routes below for exact package identifiers | a hook is not active because its name appears here |

## Route by what the Entity must do

| Need | Hook on or around the Entity | Continue at |
|---|---|---|
| Save, get, query, page, stream, or remove | `Save`, `Get`, `Query`, `Page`, `AllStream`, `Remove` | [choose an Entity store](entity-stores.md) |
| Normalize string fields before persistence (trim, casing) | `[Trim]`, `[Lowercase]`, `[Uppercase]` | [field hygiene](field-hygiene.md) |
| Use a non-string identifier | `Entity<T, TKey>` | [Data reference](../../reference/data/index.md#the-application-vocabulary) |
| Keep several runtime shapes in one searchable set | one `Entity<TRoot>` plus generated root variants | [polymorphic Entity family](../../reference/data/index.md#keep-a-polymorphic-family-in-one-set) |
| Relate a child to a parent | `[Parent(typeof(ParentType))]`, `GetParent`, `GetChildren` | [Entity relationships](relationships.md) |
| Enforce a rule around persistence | `T.Lifecycle.BeforeUpsert(...)` and the other load/upsert/remove phases | [Entity lifecycle](../../reference/data/entity-lifecycle.md) |
| Select an exceptional source, adapter, partition, or transaction | disposable `EntityContext` scopes | [context hooks](#context-hooks) |
| Isolate every operation to one customer | `using (Tenant.Use(id))` | [tenant isolation](../trust/tenant-isolation.md) |
| Cache ordinary reads | `[Cacheable(...)]` plus ordinary Entity verbs | [Entity cache](../state/cache.md) |
| Make removal recoverable | opt the Entity into soft deletion; keep ordinary `Remove()` | [recoverable deletion](recoverable-deletion.md) |
| Expose governed CRUD over HTTP | `EntityController<T>` | [Entity HTTP API](../web/entity-api.md) |
| Declare who may read or change it | `[Access(...)]` and row constraints | [access rules](../trust/access-rules.md) |
| Index and retrieve it by meaning | `[Embedding(...)]`, then `Client.Embed` + `Vector<T>.Search` | [semantic search](../ai/semantic-search.md) |
| Expose it to an outside agent | `[McpEntity(...)]` | [Entity MCP surface](../agents/entity-mcp.md) |
| Let an in-application model use it as a tool | `Agent.Create().WithEntities<T>()` | [in-application agent](../agents/in-app-agent.md) |
| Announce a business occurrence | `entity.Events.Raise<TEvent>()` | [Events and Transport](../work/events-and-transport.md) |
| Send a snapshot of current state | `entity.Transport.Send()` | [Events and Transport](../work/events-and-transport.md) |
| Own retryable or scheduled execution | `Entity<T>, IKoanJob<T>` with static `Execute` | [background Jobs](../work/background-jobs.md) |
| Own bytes | derive from `StorageEntity<T>` and add `[StorageBinding]` | [Entity-owned files](../state/entity-files.md) |
| Own an original plus reproducible derivatives | derive from `MediaEntity<T>` and declare recipes | [media derivatives](../state/media-derivatives.md) |
| Reconcile imperfect arrivals into one trusted result | derive from `CanonEntity<T>` | [Canon reconciliation](../records/canon.md) |

## The query filter vocabulary

`Todo.Query(predicate)` and the HTTP `?filter=` surface accept the same closed operator vocabulary.
A filter names a field and one operator; the framework pushes what the selected store declared and
finishes the rest in memory, so answers converge across stores.

| Ask | DSL (`?filter=`) | LINQ |
|---|---|---|
| Equals / differs | `{"Done": false}`, `{"Level": {"$ne": 3}}` | `t => !t.Done`, `t => t.Level != 3` |
| Compare | `{"Level": {"$gte": 2}}` (also `$gt`, `$lt`, `$lte`, `$between`) | `t => t.Level >= 2` |
| In a set | `{"Tier": {"$in": ["Free","Pro"]}}` (`$nin` inverse) | `t => tiers.Contains(t.Tier)` |
| String starts/ends/substring | `{"Name": "ab*"}`, `{"Name": "*ab"}`, `{"Name": "*ab*"}` or `{"Name": {"$contains": "ab"}}` | `t => t.Name.StartsWith("ab")` / `EndsWith` / `Contains` |
| Element in a collection | `{"Tags": "urgent"}`, `{"Tags": {"$in": ["a","b"]}}`, `{"Tags": {"$all": ["a","b"]}}` | `t => t.Tags.Contains("urgent")` |
| **Substring inside a collection element** | `{"Ingredients": {"$like": "salt"}}` | `t => t.Ingredients.Any(i => i.Contains("salt"))` |
| Collection shape | `{"Tags": {"$size": 2}}`, `{"Tags": {"$nin": ["x"]}}` | — |
| Presence | `{"Score": {"$exists": true}}` | — |
| Combine | `{"$and": [...]}`, `{"$or": [...]}`, `{"$not": {...}}`, `{"$nor": [...]}` | `&&`, `\|\|`, `!` |

`$like` is valid only on a collection field (an element contains the substring); on a scalar string
the parser refuses it and names `$contains` as the correction. `$contains` keeps two meanings it
already had: substring on a scalar string, exact element match on a collection. String matching is
case-sensitive unless the filter carries `"$options": {"ignoreCase": true}`; stores that cannot
answer case-insensitively finish that part in memory. A store that cannot lower an operator still
answers correctly — the framework completes the filter after the store's part — and every adapter's
exact pushdown set is readable at runtime facts (`DataCaps.Query.Filter`).

## Context hooks

Contexts are disposable, nestable exceptions around ordinary Entity calls. Keep the scope at the
business operation so unusual routing stays visible and restores automatically.

| Scope | Meaning | It does not mean |
|---|---|---|
| `EntityContext.Source("Published")` | use one configured named source | move the default or copy existing data |
| `EntityContext.Adapter("mongo")` | deliberately override the elected adapter | infer a package or guarantee provider parity |
| `EntityContext.Partition("north")` | route to one physical/logical partition | authorize a tenant |
| `EntityContext.Transaction("publish")` | defer and sequentially coordinate supported work | provide native, cross-Entity, or cross-provider atomicity |
| `Tenant.Use("acme")` | apply the ambient tenant boundary across participating pillars | create or authenticate the tenant |
| `EntityContext.NoCache()` / `RefreshCache()` | bypass or refresh the Entity cache for this scope | change durable truth |

For the complete context and transaction grammar, use the
[Entity capabilities guide](../../guides/entity-capabilities-howto.md#7-context-routing-partitions-sources-and-adapters).

## The constraint box

> **The constraint:** Capabilities compose at the common Entity boundary, but they do not erase one
> another's rules. Lifecycle is not an Event bus; a partition is not tenancy; MCP exposure is not
> authorization; a Job is not exactly-once execution; one Entity syntax does not promise provider
> parity. Follow every selected hook and inherit its constraint before calling the solution complete.

## Leaves

- **Pasteable build:** [store and expose](../../recipes/store-and-expose.md)
- **Tested contract:** [Data reference](../../reference/data/index.md)
- **Runnable composition:** [FirstUse](https://github.com/sylin-org/koan-framework/blob/main/samples/FirstUse/README.md)
- **Compound receipts:** [solution compositions](../solutions.md)
