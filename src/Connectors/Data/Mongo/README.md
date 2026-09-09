# Sylin.Koan.Data.Connector.Mongo

Use MongoDB through Koan's ordinary Entity and Source surfaces. The connector owns clients, BSON, collection naming,
indexes, discovery, mapping, inspection, and native execution.

```powershell
dotnet add package Sylin.Koan.Data.Connector.Mongo
```

## Save and query documents

```csharp
builder.Services.AddKoan();

public sealed class Book : Entity<Book>
{
    public string Title { get; set; } = "";
    public bool Published { get; set; }
}

var book = await new Book { Title = "Meaningful steps", Published = true }.Save();
var published = await Book.Query(item => item.Published);
```

No MongoDB repository, client, serializer registration, or collection bootstrap appears in application code.

Constrained single-item creates use atomic insert-only execution for ordinary managed `_id` storage. An existing
identity cannot be replaced through that create path. Explicit mappings and constrained bulk requests containing
creates currently reject before persistence. Ordinary Entity `Save` remains an upsert.
Default numeric keys reject before insertion; assign a non-default identity for those Entity types.

Existing equivalent indexes keep their names across framework upgrades when the model does not explicitly name
them. Koan checks their constraints once per physical collection. Incompatible constraints or explicit names
require an operator migration; the connector never drops or renames an existing index automatically.

## Fit a legacy collection without changing the model

```csharp
koan.Data.Source("Legacy").Map<Customer>(map => map
    .Container("CUSTOMER")
    .Key(customer => customer.Id).Name("CUSTOMER_NO")
    .Property(customer => customer.Name.Full).Name("DISPLAY_NM")
    .Property(customer => customer.Profile).Object("PROFILE")
    .Property(customer => customer.Name.First).Path("NAME_DATA", "first"));
```

The same Entity verbs now use those physical names. Mapped writes update only declared physical paths, so fields owned
by the legacy system remain untouched. Composite keys use `.Key(...).Parts(...)`. Mark the source `External` to prevent
collection creation, and `ReadOnly` to reject every mutation before provider I/O.

```json
{
  "Koan": {
    "Data": {
      "Sources": {
        "Legacy": {
          "Adapter": "mongo",
          "ConnectionString": "mongodb://legacy-host:27017",
          "Database": "erp",
          "StorageLifecycle": "External",
          "Access": "ReadOnly"
        }
      }
    }
  }
}
```

## See what a source contains

```csharp
var source = Koan.Data.Core.Data.Source("Legacy");
var page = await source.Inspect().Containers(take: 25);
var customer = await source.Inspect().Resolve(StorageAddress.From("CUSTOMER"));
RecordSet sample = await source.Inspect().Sample(customer, take: 20);
```

The vocabulary stays provider-neutral: sources contain addressable containers with traits and operations. MongoDB
returns collections and views through that surface. Samples preserve top-level BSON field order and duplicate names,
missing values, binary and temporal values, nested objects, arrays, nulls, and explicit result bounds.

## Give a native pipeline a business name

```csharp
koan.Data.Source("Catalog").Query("products.low-stock", query => query
    .Pipeline("products",
        """{ "$match": { "stock": { "$lte": "{{threshold}}" } } }""",
        """{ "$sort": { "stock": 1 } }""")
    .Parameter<int>("threshold"));

RecordSet result = await Koan.Data.Core.Data
    .Source("Catalog")
    .Query("products.low-stock", new { threshold = 5 });
```

Parameters replace exact `{{name}}` BSON string values after parsing; they are never interpolated into JSON. `$out`
and `$merge` are rejected when the operation is declared, so registered pipelines are validated reads.

## Configuration

`auto` is the default. Options access remains pure; the first operation on an active MongoDB route asks Koan's
discovery coordinator for MongoDB and falls back to `mongodb://localhost:27017` when automatic discovery has no result.
Concurrent first callers share that one resolution. A concrete connection string is authoritative.

```json
{
  "ConnectionStrings": { "Mongo": "mongodb://localhost:27017" },
  "Koan": { "Data": { "Mongo": { "Database": "Books" } } }
}
```

Explicit `zen-garden://...` intent must resolve; it never silently falls back. Credentials belong in the platform's
secret store.

## Honest capability boundary

MongoDB provides native filters, exact counts, explicit paging, bulk upsert/delete, conditional replace, TTL indexes,
and row/container/database isolation. The connector does not claim fast remove or atomic batch execution. A batch is
an ordered bulk write; `RequireAtomic=true` rejects before mutation because transaction support depends on topology and
has not been selected as a connector guarantee.

`All()` means all visible records. Use explicit pages or Koan's bounded stream surface for growing sets. Numbered-page
streaming is not snapshot-consistent or resumable and concurrent writes can cause skips or duplicates.

- Target framework: net10.0
- License: Apache-2.0
- [Technical reference](TECHNICAL.md)
- [Data adapter development primer](../../../../docs/architecture/data-adapter-development-primer.md)

## What it adds

MongoDB data provider for Koan: options binding and repository integration for document databases.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.

## Same-ID counterpart queries

Ordinary managed Mongo storage supports `Filter.SameIdIn<TEntity>(predicate, partition)` in structured reads.
The selected partition contributes eligibility; filtering, sorting, and returned content still use the outer row.
For example, translated catalog rows can require an existing readable canonical row before count and pagination.
Even a true counterpart predicate excludes orphaned outer rows.

Data binds both operands and their scopes before Mongo receives the query. Native identity-correlated lookup
applies the complete Boolean predicate before count, ordering, and paging. Counterpart rows are never hydrated.
Mapped storage, nested counterpart predicates, incompatible routes/identity shapes, and residual predicates reject.
Row-only queries retain the existing Find/CountDocuments path.

Count and page use separate native commands with the same predicate plan. Concurrent changes can produce
different committed observations between those commands; this support does not add snapshot consistency.

Counterpart identity evidence is initially qualified for string, Guid, and the eight integral CLR
key types. Mapped identities and other key types (including mutable byte[] keys) do not advertise
SupportsSameIdIn and reject counterpart binding. Ordinary persistence support is unchanged.
