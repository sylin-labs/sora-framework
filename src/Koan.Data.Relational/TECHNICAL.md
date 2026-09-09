# Sylin.Koan.Data.Relational technical notes

## Ownership

`RelationalModule` is the one functional owner of relational mapping consumption and schema orchestration. Concrete
providers reference this package and receive those services through normal Koan composition; they do not register
bridges or mutate shared relational options.

Every repository passes the already-resolved table plus an immutable `RelationalSchemaPolicy` to the orchestrator.
That policy carries projection mode, DDL policy, matching strictness, production guard, and provider schema. This makes
schema decisions local to the selected provider/source route and prevents one connector from changing another.

Provider implementations supply `IRelationalDdlExecutor`, `IRelationalStoreFeatures`, and `ILinqSqlDialect` from the
module-free Abstractions package. Application code should not consume these contracts.

## Mapping and commands

- `RelationalCommandPlanner` accepts one compiled `MappingPlan`. It emits symbolic physical reads, encoded values,
  identity predicates, conditions, filters, ordering, query intent, and the source mapping receipt; it emits no SQL.
- Insert/update/delete/get/patch/conditional/query and materialization all use `MappingBindingPlan`. Adapters lower the
  returned `PhysicalPath`; they never parse CLR expressions or rebuild naming/codec decisions.
- `SqlFilterTranslator(IRelationalMappingDialect, MappingPlan)` resolves the filter through the map and applies the
  binding codec before comparable scalar encoding. The caller-resolver constructor remains only for adapters awaiting
  their own conformance card.
- The Family owns no provider connection, SDK type, SQL dialect, native error code, or resource lifetime.

## Schema behavior

- `NoDdl`: inspect only; missing schema is reported and creation is rejected.
- `Validate`: inspect only; callers can use the report as a readiness or corrective signal.
- `AutoCreate`: create a missing table and add missing projected columns when the environment guard allows it.
- `Relaxed`: a mismatch reports `Degraded`.
- `Strict`: a mismatch reports `Unhealthy`; repositories surface their corrective schema error.
- Definition validation compares physical type, nullability, scalar/structured shape, identity, and generation. A
  same-named incompatible value is unhealthy; name existence alone is at most degraded/unverified.
- `External`: validation is allowed, but create/add/index/TTL mutation is rejected before the DDL executor.

Creation is additive. Koan does not rename/drop columns, infer destructive migrations, or promise full migration
management. Production DDL remains denied unless the selected provider explicitly allows it.

Index plans carry the same binding and encoding identities as filter/write plans. Nested expression indexes,
rewrite-free behavior, and TTL are executed only when `IRelationalStoreFeatures` proves the matching native profile;
unsupported TTL metadata is never lowered as an ordinary index.

## Query translation

The common translator supports scalar equality/comparison, logical composition, null checks, Boolean members, and the
basic string operations providers can represent safely. Provider dialects own quoting, parameters, LIKE escaping, and
JSON-array operations. Unsupported expressions fail closed; Koan does not silently scan an unbounded source.

Provider-specific SQL, connection lifecycle, discovery, health, and startup reporting remain in each concrete connector.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
