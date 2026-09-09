# Koan Cache technical notes

## Ownership

The repository decorator forwards `IInsertOnlyRepository.Insert` only with native capability support.
It invalidates a verified committed key and never seeds a value from the raw insert receipt. The outer
Data facade retains source policy, lifecycle, identity validation and transformation ownership.

Cache owns policy materialization, Entity cache identity, L1/L2 topology, serialization,
singleflight, tag operations, and peer-invalidation meaning. Data invokes an ordered repository
decorator but does not know Cache policy or transport. Communication owns carriage, election, wire,
lifecycle, ingress, health, and facts; it never decides what an invalidation means.

The Entity surface has two deliberate planes:

- `entity.Cache.Evict()` and its finite/stream forms address concrete entries;
- `EntityType.Cache.Explain/Flush/Count/Any` inspect policy or operate on policy-derived tags.

MCP operational flush remains a separately governed and audited type/global control plane. Entry
eviction is not automatically projected as an agent mutation.

## Canonical Entity cache plan

`EntityCachePlan` is the single host-level decision seam consumed by `CacheRepositoryDecorator` and
`EntityCacheEvictionCoordinator`. It:

1. selects the first active Entity-scoped policy;
2. excludes entities whose restored storage representation or non-equality read scope cannot be
   safely represented by an id-keyed cache;
3. parses the selected key template;
4. supplies canonical Entity type identity and partition/source template values; and
5. produces the business/base key only; `CacheIdentityPlan` applies Core hard-segmentation dimensions
   once at the `CacheClient` boundary for keys, tags, singleflight, eviction, and coherence.

Repository reads, writes, deletes, batch mutations, and explicit eviction therefore cannot select a
different policy or independently reconstruct a key. A custom Entity key template must remain
derivable for every operation that uses it; templates needed by id-based reads normally use
`{TypeName}`, `{Partition}`, `{Source}`, `{Id}`, and `{Key}`.

Types with storage field transforms, non-equality read predicates, or Data-local managed fields that
have not joined Core hard segmentation are not decorated. Caching their provider representation or
omitting a Data-only scope from Cache identity would be unsafe. Explicit entry eviction rejects such a
type before enumerating because no entry can validly exist.

## Eviction execution

The scalar, finite, and asynchronous Entity facets normalize to one lazy source through Data.Core's
`EntityCardinality`. `EntityCacheEvictionCoordinator` resolves the plan and captures
`EntityContext.ContextState` plus every registered `KoanContextCarrierRegistry` axis before the first
await. It restores both around deferred enumeration and each writer call.

Execution is sequential and constant-memory. Source order and multiplicity are preserved; there is no
read-ahead while one removal is pending. Default identifiers are skipped. Every non-default Entity is
formatted through the resolved plan and sent to `ICacheWriter.Remove`, which removes from the selected
topology and requests peer invalidation even when the key was locally absent.

`EntityCacheEviction` retains counters only. On source/removal failure or cancellation, typed errors
retain the confirmed prefix. Store-tier removal and peer carriage are separate awaits, not one atomic
commit; the current item may be partly removed when a failure is reported.

## Topology and coherence

The built-in Memory store is the minimum-priority local floor. Standard .NET DI supplies `ICacheStore`
candidates; the singleton `CacheTopology` compiles Core's immutable provider catalog and independently
elects Local and Remote routes once. Core owns exact lookup, duplicate rejection, memoized priority, and
the stable tie. Cache owns placement, host-wide pins, tier availability, and operation negotiation. Each
candidate and selected route retains one capability set and one safe election receipt for facts.

`LayeredCache` executes `CacheTier`: LocalOnly and RemoteOnly touch exactly the requested route and fail
when it does not exist; Layered uses both when available and degrades to the available route. Reads use
Local then Remote and backfill Local after a Remote hit. Broad removal intentionally invalidates every
selected route.

Successful removals publish one Cache-owned key invalidation through Communication's every-node
framework route. Peers filter their own origin and evict L1 only; they never mutate shared L2 or
rebroadcast. The local Communication floor is automatic. Redis contributes a layered broadcast
candidate only while Redis owns the remote tier; a direct RabbitMQ reference is ordinary provider
intent. L1 TTL bounds staleness when a best-effort invalidation is lost.

## Inspection and unsupported scenarios

The policy lifecycle owner materializes the registry before composition facts are collected, even though
the normal hosted-service start occurs later. Composition facts and startup logs expose topology, policy
count, each selected Entity entry plan or safety exclusion, coherence mode, effective broadcast
provider/assurance, active state, node identity, and L1-only receipt behavior. Health exposes the live
topology/coherence posture. Runtime-agent facts reuse those decisions; no surface infers topology from
package names.

Unsupported claims include collection atomicity, distributed transaction coupling, durable
invalidation replay/catch-up, automatic removal retry, remote settlement, and global flush as a wire
primitive. Tag flush enumerates matching entries and emits normal per-key invalidations.
Counterpart query/count definitions pass through the existing query decorator. The optional
target-binding contract forwards under Data's guarded target context; no counterpart permission
or joined result is stored in the entity cache.

## Conditional replacement

The repository decorator forwards the existing conditional-write primitive only when the inner
interface and capability agree. It captures the canonical entity cache key before awaiting and
invalidates that key only after a confirmed true result. Conflict/missing/refusal does not seed or
invalidate a value. A removal failure or cancellation after native success propagates and may leave
stale cache state after a committed write; no retry or transactional cache guarantee is introduced.
