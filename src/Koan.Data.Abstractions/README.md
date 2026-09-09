# Sylin.Koan.Data.Abstractions

Provider-neutral contracts shared by Koan's Entity runtime, data connectors, and modules that need to negotiate data
behavior without activating a provider or host.

Applications normally receive this package through `Sylin.Koan` or `Sylin.Koan.App`. Reference it directly when
implementing a repository/adapter, declaring data capabilities, or consuming the contracts without Data Core.

## Install

```powershell
dotnet add package Sylin.Koan.Data.Abstractions
```

## Meaningful use

An adapter implements `IDataRepository<TEntity,TKey>` and, when applicable, `IQueryRepository<TEntity,TKey>`.
It advertises only behavior it proves through the shared capability model:

```csharp
var capabilities = DataCaps.Describe(repository, repository.GetType().Name);
var canStreamByPages = capabilities.Has(DataCaps.Query.ProviderBoundedPaging);
```

`QueryDefinition` carries the structured candidate filter, sort, projection, page, partition, and count intent.
`RepositoryQueryResult<TEntity>` reports which axes the provider actually handled. Consumers negotiate those facts;
they do not infer guarantees from an adapter name.

Receipts are executable truth. A result reports handled filter, sort, page, projection, and count work separately;
`CountResult.Execution` distinguishes exact, optimized-exact, and fast/estimate execution. Data rejects an impossible
or incomplete receipt and never replays a dispatched query or mutation to repair one.

Entity mutation contracts follow the same rule:

- `MutationResult<TEntity,TKey>` reports insert, update, delete, missing, or conflict plus commit outcome.
- `BatchResult` reports realized atomicity and, when the created native batch earns
  `CompleteItemOutcomes`, one outcome per builder call in logical call order.
- `RequireAtomic` rejects before deferred loads, Lifecycle callbacks, or provider dispatch unless both the adapter
  claim and the created native execution seam prove atomicity.
- `GetMany` returns exactly one slot per requested identity, preserving order and duplicates and using `null` for
  missing or invisible records.

Adapters also consume one provider-neutral source and failure vocabulary:

- `DataSourcePlan` carries the immutable `StorageLifecycle`, `DataSourceAccess`, route identity, and redacted
  connection identity compiled by Data Core.
- `DataOperationEffect` proves whether dispatch is a read, data write, storage/admin action, or unknown.
- `IDataFailureClassifier` translates native exception types/codes into `DataFailureKind`, `DataCommitOutcome`,
  retry disposition, and replay disposition. Message text is never a classifier. `IDataNativeEvidenceSink` records
  restricted native type/code evidence and returns only an opaque bounded reference to public channels.
- `IAdapterFactory.DescribeClaims` declares adapter capabilities and non-source profiles. For
  `IDataSourceIntegrationFactory`, the pure `DescribeSource(source)` descriptor automatically contributes the
  registered-read, record-result, and granular inspection profiles it supports. The resulting `DataClaimSet`
  supplies the exact references used by execution diagnostics and conformance tooling.

Source-only adapters implement `IDataSourceIntegrationFactory`; they do not manufacture an Entity repository.
`IDataSourceInspectorAdapter` supplies bounded, source-bound container inspection, while `IDataSourceIntegration`
executes immutable registered read plans. Both return the same neutral record contracts:

- `RecordSet` preserves field order, duplicate names, missing versus null, nested neutral values, completion, and
  deterministic byte accounting.
- `INeutralRecordReader` converts one provider result channel incrementally; another channel rejects instead of being
  discarded.
- `OperationPlan` keeps effect, result kind, delivery, binding, read lane, parameters, timeout, and bounds independent.
- `IDataSourceNativeInspector` is the only marker admitted by the explicit native-inspection probe; the raw common
  adapter is not exposed as a policy bypass.

Aggregate mapping uses the same provider-neutral posture. `MappingDescriptor` contains one `StorageAddress`, complete
single/composite identity, logical `MappingPath` values, physical `PhysicalPath` locations, value shape, direction,
generation, authority, and optional `IDataMappingCodec`. `MappedRecord` preserves missing versus present-null values,
and `MappingReceipt` identifies the exact compiled plan/bindings used. These contracts contain no SQL column, BSON,
JSON-column, table, or provider SDK type.

The package also owns the canonical provider-neutral `PatchPayload<TKey>` operation. HTTP JSON Patch, Merge Patch,
MCP, or another projection must normalize its protocol into that operation before entering Data.

## Boundaries and failures

- This package is inert vocabulary. It does not register Data Core, elect a provider, open storage, create schemas,
  or expose `Entity<T>` statics.
- It has no ASP.NET Core dependency. Media types, JSON Patch documents, controllers, and HTTP errors belong to Web.
- `ProviderBoundedPaging` promises bounded candidate pages and a complete order, not cursor resumption, snapshot
  isolation, or mutation-safe traversal.
- A provider capability is insufficient without its matching native seam and execution receipt. Stronger behavior
  rejects before work when either half is absent.
- Unsupported optional behavior must reject or negotiate honestly; adapters must not approximate a stronger
  guarantee silently.
- A capability token is a claim, not proof. Provider conformance tests must exercise every advertised behavior.
- Native exception evidence remains adapter-owned and restricted. Data owns the stable public failure kind,
  commit outcome, retry disposition, replay disposition, and safe corrective facts.

See [TECHNICAL.md](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Data.Abstractions/TECHNICAL.md)
for query ownership, capability semantics, and adapter compatibility rules.

## Atomic insertion contract

`IInsertOnlyRepository<TEntity,TKey>` is an optional native write guarantee, advertised through
`DataCaps.Write.InsertOnly`. It inserts an absent physical identity and never replaces an existing row.
Reuse `MutationResult` for Inserted/Committed or Conflict/NotCommitted with no existing entity.
Ordinary `IDataRepository.Upsert` and `IMutationOutcomeRepository.UpsertWithOutcome` remain upserts.
Unsupported identity shapes fail before persistence; ambiguous native outcomes remain failures.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
