# MySQL connector technical notes

## Ownership boundary

`Koan.Data.Relational` owns mapping plans, managed mapping, structured-value encoding, filter translation, schema policy, and relational source integration. This connector owns MySqlConnector lifetime, endpoint normalization, MySQL SQL and DDL, routing, inspection, health, and capability declarations.

Applications continue to use `Entity<T>` operations. The adapter key is `mysql`; package-reference discovery registers `MySqlModule` and the factory.

## Routing and discovery

The factory resolves the standard source route through `AdapterConnectionResolver`. A source-specific concrete connection wins; an `auto` named source collapses to the discovered default connection. If discovery leaves `auto` unresolved, route creation fails before `MySqlConnection` is constructed.

The final connection is normalized through `MySqlConnectionStringBuilder`. Key-value strings are preserved by the driver. `mysql://` URIs map host, port, credentials, database, and recognized query options into the same builder. The final connection's database is retained for named-source isolation unless a provider-scoped source `Database` setting overrides it.

## Storage shape

Managed Entities use one InnoDB table per resolved container:

- `Id` is a native primary-key column. String identities use an explicit binary, case-sensitive collation.
- `Json` is a MySQL `JSON` column containing the Entity body and framework-managed fields.
- Provider-generated integer identities use `AUTO_INCREMENT` and `LAST_INSERT_ID()` on the same connection.
- Structured patches use `JSON_SET`; reads and filters use `JSON_EXTRACT` / `JSON_UNQUOTE` with type-aware casts.

The database must exist before the connector opens it. With managed storage, readiness creates a missing table only when source access and DDL policy allow it; production also requires explicit `AllowProductionDdl` consent. Relaxed validation requires every mapped storage root, the exact primary key, InnoDB, and compatible identity/JSON shapes. Strict validation additionally checks every mapped native type, nullability, auto-increment decision, and stored generated column. External mappings keep their declared database, table, bindings, and lifecycle policy.

## Query execution

The shared filter AST lowers through `SqlFilterTranslator` and `MySqlDialect`. Scalar comparisons, set operators, string predicates, collection membership/size, managed fields, and composed boolean filters remain parameterized and execute in MySQL. The connector rewrites the translator's backslash `LIKE` escape literal into MySQL's default SQL-mode representation; it does not mutate the session SQL mode or raw-SQL semantics.

Provider-bounded paging uses `LIMIT` / `OFFSET`. Natural order is the full identity; explicit sorts append identity columns not already requested, giving stable ordering across equal values and page boundaries. Counts are exact.

## Writes and isolation

Ordinary upserts use parameterized `INSERT ... ON DUPLICATE KEY UPDATE`. A managed row scope uses an InnoDB transaction: update is constrained by identity plus managed-field predicates, ownership is verified with a locking read, and an existing identity owned by another scope raises a corrective `cross-scope write` error. Concurrent insertion collisions are rechecked under the same transaction.

Bulk upsert and batch operations share one MySQL transaction. Bulk delete uses one parameterized identity predicate set. Conditional replace combines the identity predicate with the shared native filter lowering. Fast removal uses `TRUNCATE` only when source policy allows schema/admin effects.

## Source integration and diagnostics

Registered SQL record/scalar operations require a configured read lane and begin a read-only transaction. The inspector lists, resolves, describes, and samples tables/views within the routed database through `information_schema` and the shared neutral reader. Health opens the resolved source and executes `SELECT 1`.

Capability declarations cover native LINQ/filter execution, provider-bounded paging, bulk writes/deletes, atomic batches, fast removal, conditional replace, and row/container/database isolation. No vector capability or MariaDB-specific dialect branch is present.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
