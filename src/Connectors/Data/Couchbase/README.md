# Sylin.Koan.Data.Connector.Couchbase

Couchbase persistence for Koan entities, compact external mappings, neutral source inspection, and
registered SQL++ reads. Add the package, keep the normal `AddKoan()` bootstrap, and use Entity verbs.

- Target framework: net10.0
- License: Apache-2.0

## Install

```powershell
dotnet add package Sylin.Koan.Data.Connector.Couchbase
```

## Managed entities

```csharp
builder.Services.AddKoan();

public sealed class Product : Entity<Product>
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

var product = await new Product { Name = "Garden sensor", Price = 24.50m }.Save();
var affordable = await Product.Query(item => item.Price < 50m);
```

Known-key reads and writes use Couchbase KV. Set queries, counts, sorting, and explicit pages use
parameterized SQL++. Managed sources create missing scopes, collections, and query indexes on demand.

## Existing collections

`Namespace` maps to a Couchbase scope and `Container` maps to a collection:

```csharp
koan.Data.Source("Legacy").Map<Customer>(map => map
    .Container(StorageAddress.From("erp", "customers"))
    .Key(customer => customer.Id).Name("CUSTOMER_NO")
    .Property(customer => customer.Name).Name("DISPLAY_NM")
    .Property(customer => customer.Profile).Object("PROFILE"));
```

Mapped updates use CAS and change only declared paths, preserving fields owned by the external
system. Nested `.Path(...)` and composite `.Parts(...)` keys are supported. Couchbase document keys
are application-assigned; `.Generated()` is rejected with a corrective error.

Use `StorageLifecycle.External` when Koan must never create scopes, collections, or indexes. Use
`DataSourceAccess.ReadOnly` when every mutation must fail before provider I/O. The two policies are
independent and default to Managed/ReadWrite.

## Inspect and run named reads

```csharp
koan.Data.Source("Legacy").Query("customers.active", query => query
    .Lane("Reports")
    .Sql("SELECT META(c).id AS Id, c.name AS Name FROM `erp`.`crm`.`customers` AS c WHERE c.active = $active")
    .Parameter<bool>("active"));

var source = Koan.Data.Core.Data.Source("Legacy");
var active = await source.Query("customers.active", new { active = true });
var containers = await source.Inspect().Containers(take: 25);
```

Registered SQL++ requires a configured read lane and is executed with Couchbase's read-only query
option. Inspection returns Koan Source/Namespace/Container descriptors and neutral records, not SDK
bucket-manager types.

## Configuration

```json
{
  "ConnectionStrings": { "Couchbase": "couchbase://localhost" },
  "Koan": {
    "Data": {
      "Couchbase": {
        "Bucket": "Products",
        "Username": "${secret}",
        "Password": "${secret}",
        "Durability": "Majority"
      }
    }
  }
}
```

`Durability` accepts `None`, `Majority`, `MajorityAndPersistToActive`, or `PersistToMajority` and is
applied to every KV mutation. Invalid values fail during route construction. `Scope`, `Collection`,
`QueryTimeout`, `BootstrapTimeout`, and `BootstrapPollInterval` are optional provider settings.

## Guarantee boundary

- LINQ/filter translation is native, parameterized, and receipt-backed.
- Conditional replace uses opaque Couchbase CAS.
- Bulk operations are bounded; batches are non-atomic unless and until Koan has a replay-safe
  transaction callback contract. `RequireAtomic` and idempotency-key claims reject.
- Entity streaming uses explicit provider-bounded pages. It is not snapshot-consistent, resumable,
  or mutation-safe.
- One host-owned cluster is shared per connection and credential identity; bucket handles are shared
  within it. Repositories never own SDK clients.
- External queries require externally managed indexes. Koan does not create them as a side effect.

See [TECHNICAL.md](TECHNICAL.md) for the complete implementation contract and evidence.

## What it adds

Couchbase provider for Koan Data with direct KV, SQL++, CAS, compact mappings, and neutral source integration.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
