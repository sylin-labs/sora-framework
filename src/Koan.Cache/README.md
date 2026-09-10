# Sylin.Koan.Cache

Business-shaped caching for Koan Entities, with a process-local provider that works immediately and deterministic
provider composition when adapters are referenced.

## Install

```powershell
dotnet add package Sylin.Koan.Cache
```

Keep the normal Koan boot and declare the policy where the business type lives:

```csharp
using Koan.Core;                          // AddKoan()
using Koan.Cache.Abstractions.Policies;   // Cacheable
using Koan.Data.Core.Model;               // Entity<T>, Get
using Koan.Data.Core;                     // Save

builder.Services.AddKoan();

[Cacheable(300)]
public sealed class Todo : Entity<Todo>
{
    public string Title { get; set; } = "";
}
```

That is the complete shortest path. `Todo.Get`, `Save`, and `Delete` retain their normal meaning; Cache applies
read-through and invalidation behind the Entity repository. No cache-specific registration belongs in
`Program.cs`.

Cache is not the source of truth — the cached Entity still needs a durable store. Reference one Data
connector and configure it (without one, Entity verbs fail with `Koan Data has no provider candidates.
Reference a Data connector and call AddKoan().`):

```powershell
dotnet add package Sylin.Koan.Data.Connector.Sqlite
```

```json
"Koan": {
  "Data": {
    "Sources": {
      "Default": {
        "Adapter": "sqlite",
        "ConnectionString": "Data Source=app.db"
      }
    }
  }
}
```

## Meaningful result

```csharp
var todo = await Todo.Get(id, ct);     // cache-aware read
todo.Title = "done";
await todo.Save(ct);                   // business write; cache state follows

var policy = Todo.Cache.Explain();     // inspect the effective Entity policy
var removed = await todo.Cache.Evict(ct); // after an out-of-band write
var flushed = await Todo.Cache.Flush(ct); // type/tag control plane
```

Scalar, finite, and async-stream `Cache.Evict()` forms preserve source order and apply sequential backpressure.
Ordinary Entity writes already maintain cache state; explicit eviction is for writes that intentionally bypassed
the repository.

## Direct cache entries

Use `ICacheClient` or the `Cache` facade when the cached value is not an Entity repository result:

```csharp
var report = await Cache.WithJson<UsageReport>($"usage:{tenantId}")
    .WithAbsoluteTtl(TimeSpan.FromMinutes(10))
    .WithTags("usage")
    .GetOrAdd(ct => BuildReport(tenantId, ct), ct);
```

Fresh-or-miss is the default. `AllowStaleFor` opts a read into a bounded stale-serving window; it does not promise
background revalidation. `WithTier(CacheTier.LocalOnly)` and `WithTier(CacheTier.RemoteOnly)` express an explicit
operation requirement and fail clearly when that tier is unavailable. `Layered` is the default and uses every
available selected tier.

Removal and `GetOrAdd` share one process-local gate per physical key: a completed `Remove` orders after any
in-flight local fill, so an already-running factory cannot republish a removed value. A layered fill holds the
gate until every tier write it started has settled, so a removal cannot slip between one tier write's failure
and another's still-pending publication. A removal that cannot acquire the gate fails with `TimeoutException`
or `OperationCanceledException` and evicts nothing. The ordering is process-local; direct setters and peer
processes are not ordered.

## Composition by reference

| Direct reference | Effect |
|---|---|
| `Sylin.Koan.Cache` | Built-in process-memory Local floor |
| `Sylin.Koan.Cache.Adapter.Sqlite` | Persistent Local candidate; wins automatic Local election |
| `Sylin.Koan.Cache.Adapter.Redis` | Remote Redis candidate plus layered peer-broadcast capability |

Koan compiles Local and Remote election once from standard .NET DI candidates. An optional host-wide
`Cache:LocalProvider` or `Cache:RemoteProvider` pin overrides priority and fails closed when unavailable. Per-policy
provider pins do not exist; a policy chooses semantic tier, while the host owns infrastructure selection.

Each store declares guarantees through `CacheCaps`. Operations negotiate the capabilities they require—tags,
sliding expiry, bounded stale serving, or binary payloads—before invoking the provider.

## Startup and operations

Boot reporting and composition facts expose:

- every available store, placement, priority, and declared capabilities;
- selected Local/Remote receipts and effective topology;
- default tier, TTL, L1 TTL, and invalidation posture;
- peer-broadcast provider and assurance; and
- materialized Entity policies, resolved entry plans, and safety exclusions.

`CacheHealthCheck` probes each selected tier and reports coherence posture. Facts can be projected through Koan's
operator and agent surfaces when those host modules are present.

## Honest limits

Cache does not claim cross-store transactions, batch atomicity, durable invalidation replay, remote handler
settlement, or automatic stale-value revalidation. Peer invalidation is a bounded best-effort hint; L1 TTL remains
part of the correctness posture.

See the [Cache reference](../../docs/reference/data/cache.md) and [technical notes](TECHNICAL.md).
Atomic insertion is forwarded through the existing Entity repository decorator when the selected
provider supports `DataCaps.Write.InsertOnly`. Proven committed insertion invalidates its cache key;
conflict does not cache or expose the rejected payload or an existing row.

Counterpart predicates use the underlying provider's structured query path and capability.
A cached entity is never evidence that its counterpart satisfies a current read constraint;
unsupported providers reject the query even when an identity is already cached.
