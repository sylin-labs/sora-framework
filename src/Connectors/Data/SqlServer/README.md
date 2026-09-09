# Sylin.Koan.Data.Connector.SqlServer

SQL Server provider for Koan relational data with safe defaults, pushdowns, and schema helpers.

The generated [product surface](../../../../docs/reference/product-surface.md) owns support maturity.
Referencing this package makes SQL Server eligible for normal `AddKoan()` provider selection; no
provider-specific registration API is required.

- Target framework: net10.0
- License: Apache-2.0

## Capabilities

- Connection + health integration with minimal options
- JSON projection and filter/paging pushdowns where supported
- Schema helpers via Koan.Data.Relational (add-only create/index)
- Provider-bounded Entity streams through `DataCaps.Query.ProviderBoundedPaging`
- Compact flat, object, nested-path, composite-key, and generated-key maps
- Read-only fail-fast safety and non-creating `StorageLifecycle.External`
- Provider-neutral inspection plus registered parameterized SQL reads/scalars

## Install

```powershell
dotnet add package Sylin.Koan.Data.Connector.SqlServer
```

## Minimal setup

```csharp
builder.Services.AddKoan();

public sealed class Item : Entity<Item>;

var saved = await new Item().Save();
var same = await Item.Get(saved.Id);
```

Configure a connection using first-win resolution:

- `Koan:Data:SqlServer:ConnectionString`
- `Koan:Data:Sources:Default:sqlserver:ConnectionString`
- `ConnectionStrings:SqlServer`
- `ConnectionStrings:Default`

With `ConnectionString=auto` (the default), local orchestration discovery is attempted and then the
documented localhost development fallback is used. Keep explicit credentials in secret stores. A
reachable SQL Server instance is the only external runtime prerequisite.

Map an existing table with the same Entity surface:

```csharp
builder.Services.AddKoan(koan => koan.Data.Source("Legacy").Map<Customer>(map => map
    .Container("CUSTOMER")
    .Key(customer => customer.Id).Name("CUSTOMER_NO")
    .Property(customer => customer.DisplayName).Name("DISPLAY_NM")
    .Property(customer => customer.Profile).Object("PROFILE_JSON")));
```

Set `StorageLifecycle=External` to prohibit DDL and `Access=ReadOnly` to reject Entity writes. `Inspect()` exposes
neutral containers and bounded samples. Registered `.Sql(...)` reads require a configured read lane; use database
grants as the security boundary. Koan also wraps SQL Server named reads in a rollback-only transaction.

## Usage - safe snippets

- Prefer first-class model statics from your entity models:
  - `Item.FirstPage(50, ct)` and then `Item.Page(2, 50, ct)`
  - `Item.Query(x => x.Status == "Open", ct)`
  - `await foreach (var i in Item.QueryStream(x => x.Flag, ct)) { ... }`
- Avoid unbounded materialization; use paging or streaming for large sets.

```csharp
// Page through items explicitly
const int pageSize = 50;
for (var pageNumber = 1; ; pageNumber++)
{
    var items = await Item.Page(pageNumber, pageSize, ct);
    foreach (var item in items) { /* ... */ }
    if (items.Count < pageSize) break;
}
```

## Streaming boundary

`AllStream` and `QueryStream` request one numbered SQL Server page at a time. `batchSize` caps the
Koan-visible candidate page; it does not claim a bound for opaque provider-driver buffers. Streaming
orders by a portable scalar -- an enum, or an integral, decimal, floating, or temporal type -- with a
comparison that holds on any backend. Any other key still streams stably here, but SQL Server defines its
comparison rather than Koan, which records that as a runtime fact instead of refusing the query.
Data.Core separately appends the usual string Entity identifier as an opaque provider-stable
tie-break, not a cross-provider collation promise.

These streams do not provide snapshot consistency, mutation-safe traversal, resumability, or a public
cursor. Concurrent writes can therefore cause skips or duplicates during offset-based traversal.

See TECHNICAL.md for contracts, options, and pushdown notes.

## References

- [DATA-0107 provider-bounded Entity streams](../../../../docs/decisions/DATA-0107-provider-bounded-entity-streams.md)
- [Entity access and streaming](../../../../docs/guides/data/entity-access-and-streaming.md)
- Engineering front door: `~/engineering/README.md`

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
