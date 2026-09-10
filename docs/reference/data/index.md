---
type: REFERENCE
domain: data
title: "Persist and query business state"
audience: [developers, architects, ai-agents]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  date_last_tested: 2026-08-06
  status: verified
  scope: Entity grammar, provider choice, query cost, relationships, lifecycle, and correction paths
---

# Persist and query business state

Use Koan Data when an application needs to store, find, relate, page, or process business objects.
Application code works with its own Entity types; the referenced connector owns the physical store,
its schema, and its own registration. The ordinary application path carries no persistence plumbing
at all.

## Smallest useful result

Add the application bundle and one durable provider:

```powershell
dotnet add package Sylin.Koan.App
dotnet add package Sylin.Koan.Data.Connector.Sqlite
```

Keep the normal Koan bootstrap:

```csharp
using Koan.Core;   // AddKoan()

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoan();

var app = builder.Build();
await app.RunAsync();
```

Then model and use business state directly:

```csharp
public sealed class Todo : Entity<Todo>
{
    public string Title { get; set; } = "";
    public bool Done { get; set; }
}

var todo = await new Todo { Title = "Ship one useful thing" }.Save(ct);
var same = await Todo.Get(todo.Id, ct);
var open = await Todo.Query(item => !item.Done, ct);
await todo.Remove(ct);
```

Referencing SQLite makes the connector available. `AddKoan()` discovers it, elects it, creates the
default local schema on first use, and exposes the same Entity operations to application code,
generated REST controllers, and generated MCP tools.

## The application vocabulary

| Need | Use | Cost signal |
|---|---|---|
| One known Entity | `Todo.Get(id, ct)` | Keyed lookup |
| A filtered, bounded result | `Todo.Query(predicate, ct)` | Materialized result; provider execution varies |
| One UI or API page | `Todo.FirstPage(size, ct)` or `Todo.Page(number, size, ct)` | Caller supplies the bound |
| A large sequential workload | `Todo.AllStream(...)` or `Todo.QueryStream(...)` | Only on a provider that proves bounded paging |
| Create or update | `entity.Save(ct)` | One logical write |
| Create without replacing an existing identity | `entity.Insert(ct: ct)` | Qualified native insertion; conflict leaves the stored row unchanged |
| Replace the revision the caller observed | `entity.ReplaceIf(stored => stored.Revision == expected, ct: ct)` | Qualified native conditional replacement; never creates a missing row |
| Delete | `entity.Remove(ct)` | One logical removal |
| A finite batch | `Todo.UpsertMany(items, ct)` | Atomicity depends on the provider capability |

Use `Entity<T, TKey>` when the identifier is not the default string key. The lower-level
`Data<TEntity, TKey>` facade, direct provider instructions, and raw access are expert escape hatches,
not an application architecture requirement.

## Preserve representation and write intent

Managed Entity documents preserve enum names as strings, including declared aliases and supported
flags combinations. Queries use the same representation; unsupported native ordering refuses rather
than sorting the names as if they were numeric values. Typed restoration preserves collection
contents and private stored state without granting an HTTP caller permission to write those members.

Use `Insert` for create-only intent and `ReplaceIf` for a captured destination revision. Unsupported
provider or scope combinations refuse instead of falling back to a read followed by an unguarded
save. These operations protect the destination identity; they do not make a separate source read
atomic with its write. See the [Data contract](../../../src/Koan.Data.Core/README.md) for receipts,
supported providers, deferred route restoration, and completion-failure limits, and the
[PATCH guide](../../guides/patch-capabilities-howto.md) for partial HTTP updates.

## Keep a polymorphic family in one set

Declare one Entity root when several runtime shapes must share one searchable table or collection.
Put shared searchable and policy-bearing fields on the root, then close each variant through Koan's
generated same-name companion:

```csharp
public class Media : Entity<Media>
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class Anime : Media<Anime>
{
    public int? Episodes { get; set; }
}

public sealed class Manga : Media<Manga>
{
    public int? Volumes { get; set; }
    public int? Chapters { get; set; }
}
```

Use the variant for typed point operations and the root for set operations:

```csharp
await new Anime { Kind = "Anime", Title = "Cowboy Bebop", Episodes = 26 }.Save(ct);

Anime? anime = await Anime.Get(id, ct);
Media? media = await Media.Get(id, ct); // runtime type is Anime for that row

var animePage = await Media.Query(item => item.Kind == "Anime", ct);
var page = await Media.Page(1, 25, ct);
```

`Anime`, `Manga`, and `Media` resolve to the Media repository, so root filtering, sorting, counting,
and paging keep the connector's normal provider pushdown. Derived-only fields remain stored with the
record; put fields on `Media` when they need portable set-wide query, index, segmentation, lifecycle,
or set-policy semantics. Record-local write stamps and field transforms still follow the record's
runtime type.

Koan generates `Media<TVariant>` from the root and writes its reserved `__koan_type` hint on every
record in the family, including plain `Media`. That hint is what lets a root query hand back an
`Anime` for an anime row, which is why `Kind` above stays ordinary business data rather than a
discriminator the application has to maintain.

Use a generic shared base with separate `Entity<Self>` leaves instead when the types only reuse
properties and should own independent tables or collections.

## Choose a provider by business need

Reference only the connectors the application can actually use. The
[product surface](../product-surface.md) is the authority for current maturity; each connector README
owns its setup and backend-specific limits.

| Need | Connector | Important boundary |
|---|---|---|
| Durable local or single-node state | `Sylin.Koan.Data.Connector.Sqlite` | The simplest durable application path |
| Process-local tests or ephemeral work | `Sylin.Koan.Data.Connector.InMemory` | Non-durable; not a production persistence claim |
| File-backed, zero-infrastructure state | JSON provider included by the application bundle | Limited concurrency; not the durable foundation path |
| PostgreSQL | `Sylin.Koan.Data.Connector.Postgres` | External service and provider-owned schema/operations apply |
| SQL Server | `Sylin.Koan.Data.Connector.SqlServer` | External service and provider-owned schema/operations apply |
| MongoDB | `Sylin.Koan.Data.Connector.Mongo` | Document-store query and consistency limits apply |
| Couchbase | `Sylin.Koan.Data.Connector.Couchbase` | Bucket, query-service, and consistency limits apply |
| CouchDB (document store) | `Sylin.Koan.Data.Connector.CouchDb` | Mango query, index, and MVCC consistency limits apply |
| Firebird | `Sylin.Koan.Data.Connector.Firebird` | Scalar pushdown via shadow columns; collection filters ride the framework floor |
| CockroachDB | `Sylin.Koan.Data.Connector.Cockroach` | Cockroach-specific routing and schema policy apply |
| Redis-backed keyed state | `Sylin.Koan.Data.Connector.Redis` | Queries may scan; Entity streams reject |

**Zero configuration is the default posture, not a luxury.** Every networked provider in the
table discovers a conventional local endpoint and resolves sensible development credentials —
start the server, reference the package, and run. Credential resolution layers most-specific-first:
your configuration keys, then the server image's own environment convention, then the documented
development default. Databases, buckets, and collections are created on first use under managed
lifecycle.

Search and vector stores solve different needs. Start from the [AI pillar](../ai/index.md) for
embedding/vector retrieval and from each search connector's package documentation for indexed search.
Same syntax does not imply backend parity: query, transaction, ordering, and consistency semantics
differ by connector even where the Entity vocabulary does not.

## Pin important routing

When more than one connector is referenced, make durability intent explicit on the Entity:

```csharp
[DataAdapter("sqlite")]
public sealed class Approval : Entity<Approval>
{
    public string Subject { get; set; } = "";
}
```

Or choose the application default in configuration:

```json
{
  "Koan": {
    "Data": {
      "Sources": {
        "Default": {
          "Adapter": "sqlite",
          "ConnectionString": "Data Source=./data/app.db"
        }
      }
    }
  }
}
```

Explicit intent fails loudly. If the selected adapter is not referenced, Koan reports
`adapter-unavailable`, lists the available choices, and names the package or configuration correction.
It does not silently move the Entity to another store.

## Promote a verified default across databases

Reference `Sylin.Koan.Data.Cutover` when a single-host application must move its active default database to a new empty
SQLite, MongoDB, or PostgreSQL source without changing application Entity calls. Configure the target as a distinct Managed +
ReadWrite source, prove that external writers are absent or paused, then inspect the complete plan before running:

```csharp
using Koan.Data.Core;
using Koan.Data.Cutover;

var transition = Data.Source("Mongo").PromoteToDefault();
var plan = await transition.Plan(ct);
if (!plan.CanRun) return;

var receipt = await transition.Run(ct);
```

The first supported provider set is SQLite, MongoDB, and PostgreSQL, including verified moves between unlike stores.
The envelope remains string-keyed default-routed Entity roots and one host. The operation
fails closed for a nonempty target, unexplained source containers, managed or segmented slices, stored transforms,
custom filters, mappings, provider drift, incomplete inventory, or unproven writer ownership. It copies in bounded pages,
matches target records by exact identity, proves canonical logical readback and cardinality, durably activates one route pointer, invalidates retained default handles, and never rolls back
automatically. Follow the [verified default-route cutover how-to](../../guides/data/default-route-cutover.md) for the
operator sequence; the [package companion](../../../src/Koan.Data.Cutover/README.md) owns installation and package-specific limits.

## Relationships stay in the model

Declare a direct edge with `[Parent]`, then navigate it without introducing a repository layer:

```csharp
using Koan.Data.Core.Model;          // Entity<T>
using Koan.Data.Core.Relationships;  // Parent, GetParent, GetChildren
using Koan.Data.Core;                // Save

public sealed class Order : Entity<Order>
{
    [Parent(typeof(Customer))]
    public string CustomerId { get; set; } = "";
}

var customer = await order.GetParent<Customer>(ct);
var orders = await customer.GetChildren<Order>(ct);
```

Koan rejects an unbounded scan-backed relationship unless the call explicitly supplies a finite
candidate/result budget. Direct-edge navigation is not recursive graph traversal or snapshot
isolation.

## Add write policy only when the business needs it

The parameterless `AddKoan()` remains complete. Compose host-owned persistence rules only for a real
business invariant:

```csharp
builder.Services.AddKoan(() =>
    Product.Lifecycle.BeforeUpsert(context =>
        context.Current.Price < 0
            ? context.Cancel("Price cannot be negative.", "product.price")
            : context.Proceed()));
```

Lifecycle policy applies at the common Entity/Data boundary, so ordinary Entity calls and generated
protocol surfaces have the same persistence meaning. See [Entity lifecycle](entity-lifecycle.md) for
phases, prior values, protected fields, bulk behavior, transactions, and deliberate bypasses.

## Query and streaming honesty

A shared vocabulary does not hide physical cost:

- Prefer an explicit page when a request result can grow.
- Use `All()` only when fully materializing the visible set is intentional.
- Use Entity streams only when the elected connector advertises provider-bounded paging.
- Treat a capability rejection as a design correction, not a reason to add a silent full-set fallback.
- Inspect `Data<TEntity, string>.Capabilities` before relying on optional bulk, transaction, filter,
  or isolation behavior.

The [Entity access and streaming guide](../../guides/data/entity-access-and-streaming.md) owns the
qualified-provider matrix, ordering floor, cancellation behavior, offset-paging consistency limits,
and corrective exception.

## Verify the application contract

`Sylin.Koan.Testing` can exercise the same host and Entity surface used by the application:

```csharp
public sealed class TodoConformance : EntityConformanceSpecs<Todo>
{
    protected override Todo NewValid() => new() { Title = "A valid example" };
}
```

Capability-specific batteries skip when their prerequisite is absent. A skip means the test did not
produce evidence; it is not provider certification. External connectors still require reachable
services and provider-specific tests for the guarantees the application relies on.

## Inspect and correct

- Read startup's Data election and composition summary first.
- Use `/health/ready` for participating dependency readiness and `/health/live` for process liveness.
- Read `/.well-known/Koan/facts` or `koan://facts` for the same redacted runtime decisions.
- Review `koan.lock.json` for referenced-module drift; runtime facts add the connector actually elected.
- If an available connector is unused, it remains non-critical and must not open a connection merely
  because its package was referenced.

[Adapter diagnostics and readiness](adapter-diagnostics.md) owns the operator and connector-author
contract for availability, participation, health, route identity, and redaction.

## Add only the semantics you need

- `Sylin.Koan.Data.SoftDelete` keeps ordinary `Remove()` grammar while adding explicit recycle-bin,
  restore, and purge behavior for opted-in Entities.

The package owns its complete API and limitations in its README and technical companion. Data Backup
carries no product claim, so it does not belong in the application path above.

## Continue by task

- [Entity lifecycle](entity-lifecycle.md) — enforce persistence invariants.
- [Entity access and streaming](../../guides/data/entity-access-and-streaming.md) — process large sets
- [Entity analytics](../../guides/data/entity-analytics.md) — declared, named aggregation questions with materialized projections and an agent surface
- [DuckDB engine](../../guides/data/duckdb-engine.md) — the embedded analytical engine behind materialized projections
  without disguising provider cost.
- [Adapter diagnostics and readiness](adapter-diagnostics.md) — operate or author a connector.
- [Testing your app](../../guides/testing-your-app.md) — prove the application-facing contract.
- [`FirstUse`](../../../samples/FirstUse/README.md) — run the smallest durable application path.

## Deliberate limits

Koan Data does not promise universal provider parity, cross-provider transactions, recursive graph
loading, production migration policy, backup/SLO/security posture, or snapshot-safe streaming. Those
guarantees belong to a selected provider, another capability pillar, or the application and must be
stated and tested there.
