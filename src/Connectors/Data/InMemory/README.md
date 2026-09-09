# Sylin.Koan.Data.Connector.InMemory

Use this connector for fast, process-local Entity persistence in tests, conformance suites, and
explicitly ephemeral development workflows.

## Choose it when

- losing all data at process exit is acceptable;
- a test needs a real `AddKoan()` provider without files, containers, or remote services; or
- provider-neutral behavior needs a deterministic in-memory oracle.

Do not choose it for durable application state, cross-process sharing, or production recovery.

## Install

```powershell
dotnet add package Sylin.Koan.Data.Connector.InMemory
```

The direct package reference expresses intent to use the ephemeral provider. Keep the application's ordinary Koan
bootstrap; there is no provider-specific registration:

```csharp
builder.Services.AddKoan();
```

## Meaningful result

Define an Entity and use its normal persistence verbs:

```csharp
public sealed class Todo : Entity<Todo>
{
    public string Title { get; set; } = "";
}

var saved = await new Todo { Title = "Prove the rule" }.Save();
var same = await Todo.Get(saved.Id);
```

The saved value is available to every repository in the same Koan host and disappears when that host exits. InMemory
stores a serialized snapshot, so mutating the original or a loaded value does not silently mutate persistence; call
`Save()` again to publish a change. No files, container, or remote service are created.

## Selection

The provider identity is `inmemory` (`memory` is also recognized). It is a direct provider with priority `-100`, not
an automatic fallback. A direct InMemory reference wins over a merely bundle-provided automatic floor. If several Data
connectors are referenced directly, Koan applies the normal deterministic selection rules; pin the intended provider
when application durability must not change as references grow:

```json
{
  "Koan": {
    "Data": {
      "Sources": {
        "Default": { "Adapter": "inmemory" }
      }
    }
  }
}
```

## Current capability boundary

- process-local concurrent storage keyed by source, Entity root, and partition;
- detached, polymorphism-safe Entity snapshots on every read and write;
- in-memory filter execution;
- bulk upsert and bulk delete;
- ordered, non-atomic batch behavior with atomic requirements rejected before mutation; and
- shared/container/database isolation modes through the common key-value family.

The connector does not advertise `DataCaps.Query.ProviderBoundedPaging`. `AllStream` and
`QueryStream` reject correctively with `QueryStreamRejectedException` before yielding because paging
an already resident full-source dictionary is not a provider-bounded stream.

Use `All`/`Query` for deliberately small test sets, or `FirstPage`/`Page` when a bounded result returned
to test code is sufficient. Numbered pages do not create an unbounded-data performance guarantee.

Physical compatibility maps and `StorageLifecycle: External` reject correctively: an object-graph store has no legacy
physical names or pre-existing provider-owned storage to open. These are connector-specific claims, not a promise that
remote providers behave identically. The current connector suite passes 56/56.

For application conformance, prefer `Sylin.Koan.Testing`; it owns host isolation, partitions, and the
capability-aware battery. See [`TECHNICAL.md`](TECHNICAL.md) for the exact storage and negotiation
contract.

## References

- [DATA-0107 provider-bounded Entity streams](../../../../docs/decisions/DATA-0107-provider-bounded-entity-streams.md)
- [Entity access and streaming](../../../../docs/guides/data/entity-access-and-streaming.md)

## Atomic insertion

Atomic insertion uses the existing host-owned, source/root/partition store. `ConcurrentDictionary.TryAdd`
provides the insert-only decision over detached snapshots; an identity collision never replaces or
returns the existing row. This is a single-process guarantee, consistent with InMemory storage.
Default numeric keys reject before persistence; assign an identity when the Entity cannot generate one.
