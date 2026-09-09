# Sylin.Koan.Data.Connector.MySql

Use MySQL as the record store behind Koan's existing Entity API.

## Use the connector

Reference `Sylin.Koan.Data.Connector.MySql`; keep the application's single `AddKoan()` call.

```bash
dotnet add package Sylin.Koan.Data.Connector.MySql
```

```csharp
public sealed class Todo : Entity<Todo>
{
    public string Title { get; set; } = "";
    public bool Done { get; set; }
}

var todo = await new Todo { Title = "Ship it" }.Save(ct);
var open = await Todo.Query(item => !item.Done, ct);
```

No MySQL repository or registration API is required.

## Configure MySQL

Use a normal MySqlConnector connection string when discovery is not appropriate:

```json
{
  "ConnectionStrings": {
    "MySql": "Server=localhost;Port=3306;Database=Koan;User ID=root;Password=mysql"
  }
}
```

`Koan:Data:MySql:ConnectionString` is the provider-scoped alternative. A `mysql://` URI is accepted and normalized; recognized MySqlConnector options may be supplied as query parameters. Unknown URI options fail before opening a connection.

For a named source, configure the standard source connection and select it with `EntityContext.Source(...)`. The database in that source's final connection string is its storage boundary unless a source-scoped MySQL `Database` setting explicitly overrides it.

## Guarantees and limits

- The supported server line is MySQL 8.4.
- The configured database must already exist. Managed lifecycle can create Entity tables; it does not create databases.
- Production table creation additionally requires `AllowProductionDdl=true`; Koan never infers production DDL consent.
- Managed tables use InnoDB. An existing non-InnoDB table is rejected because atomic-batch semantics cannot be claimed for it.
- `SchemaMatching=Relaxed` still requires every mapped storage column, the exact primary key, and compatible identity/JSON shapes. `Strict` additionally verifies every mapped column's native type, nullability, and generated-column shape.
- Filters execute in MySQL, and paging adds a stable identity tiebreak.
- Managed row-scope values are stored in the Entity JSON document and guard conflicting writes.
- Unreachable endpoints, unresolved `auto`, unconfigured named sources, denied DDL/write access, and incompatible schemas fail at the connector boundary.

This connector targets MySQL. MariaDB compatibility is not asserted by this package.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
