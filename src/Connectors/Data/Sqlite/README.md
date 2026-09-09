# Sylin.Koan.Data.Connector.Sqlite

SQLite is Koan's embedded relational reference adapter. It supports the ordinary Entity experience for a managed
store and the same compact source experience for external or legacy files.

## Install

```powershell
dotnet add package Sylin.Koan.Data.Connector.Sqlite
```

For an application-owned store, reference the package and use the normal bootstrap:

```csharp
builder.Services.AddKoan();

var todo = await new Todo { Title = "Ship" }.Save();
var open = await Todo.Query(item => !item.Done, ct);
```

The zero-configuration target is `.koan/data/Koan.sqlite`. SQLite creates it on first elected use; merely loading
the connector does not touch disk. File, private-memory, and named shared-memory connection strings are supported.

## Inspect and name useful reads

Use provider-neutral storage vocabulary to see what a source contains:

```csharp
var source = Data.Source("Legacy");
var page = await source.Inspect().Containers(100, ct: ct);
var customer = await source.Inspect().Resolve(StorageAddress.From("CUSTOMER"), ct);
var shape = await source.Inspect().Describe(customer, ct);
var sample = await source.Inspect().Sample(customer, 20, ct);
```

Give reusable SQL a business name in the one application composition call. Opaque SQL requires a configured read
lane; SQLite enforces it with `PRAGMA query_only = ON` on the selected lane connection.

```csharp
builder.Services.AddKoan(koan =>
{
    koan.Data.Source("Legacy").Query("customers.active", query => query
        .Lane("Reports")
        .Sql("select CUSTOMER_NO as Id, DISPLAY_NM as Name from CUSTOMER where ACTIVE = @active")
        .Parameter<bool>("active"));

    koan.Data.Source("Legacy").Scalar<long>("customers.count", query => query
        .Lane("Reports")
        .Sql("select count(*) from CUSTOMER"));
});

var active = await Data.Source("Legacy").Query("customers.active", new { active = true }, ct);
var count = await Data.Source("Legacy").Scalar<long>("customers.count", ct);
```

## Map a legacy shape

One compiled map drives hydration, writes, identity predicates, filters, sorts, batches, and schema validation:

```csharp
builder.Services.AddKoan(koan =>
    koan.Data.Source("Legacy").Map<Customer>(map => map
        .Container("CUSTOMER")
        .Key(customer => customer.Id).Name("CUSTOMER_NO")
        .Property(customer => customer.Name.Full).Name("DISPLAY_NM")
        .Property(customer => customer.Profile).Object("PROFILE_JSON")));

using (EntityContext.Source("Legacy"))
{
    var customer = await Customer.Get(7, ct);
    customer!.Name.Full = "Updated";
    await customer.Save(ct);
}
```

Supported shapes are identity plus whole object, flat physical names, mixed scalar/object values, and logical
properties backed by nested structured paths. Composite and provider-generated identities are supported. Nested
path updates preserve unrelated JSON properties.

Set `StorageLifecycle: External` to prohibit physical shape mutation. Set `Access: ReadOnly` to reject every Entity
write before provider I/O. An explicit map pins its declared container, so combining it with an ambient partition
rejects rather than silently sharing that container.

See [TECHNICAL.md](TECHNICAL.md) for guarantees and limits.

## Limits

Constrained single-item creates cannot replace an existing identity, including one hidden by request visibility.
SQLite requires native identity uniqueness for this operation. Constrained bulk requests containing creates
currently reject before persistence. Ordinary Entity `Save` remains an upsert.

Configuration decides participation; unsupported requests reject before provider work with a named capability and a correction. Provider-specific limits live in the package's TECHNICAL.md.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
