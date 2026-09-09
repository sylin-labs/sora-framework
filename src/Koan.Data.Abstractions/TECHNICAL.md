---
uid: reference.modules.Koan.data.abstractions
title: Koan.Data.Abstractions - Technical Reference
description: Provider-facing entity, query, result, and capability contracts.
packages: [Sylin.Koan.Data.Abstractions]
source: src/Koan.Data.Abstractions/
last_updated: 2026-07-28
validation:
  date_last_tested: 2026-07-15
  status: reviewed
  scope: public contract source inventory; runtime behavior remains owned by Data.Core and adapter suites
---

## Contract

- This project defines the boundary shared by Data.Core and data adapters. Application-facing
  `Entity<T>` statics live in `Koan.Data.Core`, not here.
- `IDataRepository<TEntity, TKey>` owns key reads, writes, bulk operations, readiness, and repository
  creation semantics.
- `IQueryRepository<TEntity, TKey>` accepts one structured `QueryDefinition` and returns a
  `RepositoryQueryResult<TEntity>` that reports which query axes the provider handled.
- Adapters declare optional behavior through `DataCaps` and the shared `CapabilitySet`; consumers
  negotiate those facts instead of inferring support from a provider name.

## Key types

- `IEntity<TKey>` — the minimal entity identity contract.
- `IDataRepository<TEntity, TKey>` — provider CRUD, bulk, readiness, and batch boundary.
- `IQueryRepository<TEntity, TKey>` — structured query and count execution.
- `QueryDefinition` — filter AST, sort, projection, page, partition, and optional count strategy.
- `RepositoryQueryResult<TEntity>` and `CountResult` — values plus execution/estimate facts.
- `MutationResult<TEntity,TKey>`, `BatchResult`, and `BatchItemResult` — mutation, commit, atomicity, and ordered
  per-operation facts.
- `DataCaps` — query, write, isolation, and retention capability tokens.
- `DataSourcePlan`, `StorageLifecycle`, `DataSourceAccess`, and `DataOperationEffect` — immutable source ceiling
  and exact dispatch-effect vocabulary.
- `DataFailure`, `DataFailureContext`, and `IDataFailureClassifier` — stable public failure/outcome contract and
  the adapter-owned native type/code translation seam.
- `IDataNativeEvidenceSink` — write-only restricted native evidence with an opaque bounded public reference.
- `DataClaimSet`, `IDataClaims`, and `DataCapabilityProfiles` — one inert executable claim projection, including
  source-specific profiles derived from `DataSourceIntegrationDescriptor`, with exact deterministic references
  shared by runtime and TestKit.
- `PatchPayload<TKey>` and `PatchOp` — the provider-neutral patch operation accepted by Data.Core.
- `IFieldTransform`, `IFieldTransformContributor`, and `IFieldTransformInspector` — neutral contracts for
  host-compiled round-trip storage transforms and cross-pillar inspection.
- `IDataSourceIntegrationFactory`, `IDataSourceIntegration`, and `IDataSourceInspectorAdapter` — source-only
  activation, registered reads, and neutral inspection without an Entity repository.
- `StorageAddress`, `StorageContainerReference`, and `StorageContainerDescriptor` — provider-neutral addressing,
  opaque source binding, intrinsic traits, and policy-projected operations.
- `DataField`, `DataRecord`, `RecordSet`, and `RecordSetExecution` — ordered neutral shape/value/completion and
  `MaterializedValueV1` accounting.
- `OperationPlan`, `IDataOperationBinding`, and `BoundOperationParameter` — immutable registered-read decisions and
  provider-native payload seam.
- `MappingDescriptor`, `MappingBindingDescriptor`, `MappingIdentityDescriptor`, `MappingPath`, and `PhysicalPath` —
  inert aggregate-to-record meaning shared by every Family.
- `IDataMappingCodec`, `MappedRecord`, and `MappingReceipt` — one physical encoding, missing-preserving values, and
  exact plan-use evidence for hydration, writes, queries, projections, and indexes.

## Mapping vocabulary

- Logical selection and physical location are separate. A logical path never carries a provider expression, and a
  physical path is an ordered root plus neutral structured segments.
- `Canonical` is the sole writable authority. `Derived` describes a provider-maintained or virtual read expression;
  it cannot enter an ordinary write receipt.
- A writable codec must encode and decode. An explicit read-only binding may use a decode-only codec, but it cannot
  qualify filters, ordering, conditional writes, or indexes that require physical parameter encoding.
- `MappingConvention` is an adapter-supplied managed-store default only. An application-declared map wins; external
  shapes never depend on an adapter silently reinterpreting the fluent grammar.

## Source Integration and neutral records

- A named source resolves an exact `IDataSourceIntegrationFactory`; source-only use never enters the Entity provider
  catalog or constructs a synthetic repository.
- Inspection references and continuations are opaque and source-bound. Data Core validates capability, source,
  descriptor shape, page bounds, and policy-projected operations before returning them.
- Neutral values form a closed algebra. Provider objects, streams, cursors, arbitrary POCOs, and native document types
  convert before `DataRecord` construction, reject, or remain on an explicit native surface.
- A `RecordSet` fixes its shared field shape before the first row. Ordinal access is lossless; exact-case name access
  requires one field; presence bits preserve missing separately from explicit null.
- DTO projection compiles constructor/record or writable-property ordinal reads once per target type per result.
  Direct ADO queries, registered reads, and inspection samples use this same path without dictionary/JSON conversion.
- Registered operations are `Read + Records/Scalar + Buffered`. Opaque bindings require one frozen, provider-enforced
  read lane. Active segmentation rejects unless an explicit host/control-plane surface exists. Calls are uncached and
  never replayed after dispatch.

## Stored-field transform boundary

- A functional module contributes an `IFieldTransformContributor` through standard DI. Data.Core owns compilation,
  ordering, per-type memoization, clone-before-write, and reverse-on-read placement.
- A contributor returns `null` for Entity types it does not affect. Stable contributor ids and order are diagnostic
  and composition inputs, not application configuration.
- `IFieldTransformInspector` lets another pillar make a safe structural decision without referencing the functional
  module. Cache uses it to exclude transformed Entity types; it does not learn Classification internals.
- These contracts do not authorize adapters to apply, omit, or reorder transforms. Supported application paths enter
  through Data.Core; direct repository use is outside that facade guarantee.

## Projection isolation

- This contract assembly does not reference ASP.NET Core or a JSON Patch library.
- HTTP and agent projections parse their native request formats and normalize them to `PatchPayload<TKey>` before
  calling Data. Adapters therefore implement Data semantics without inheriting protocol machinery.
- `PatchPayload<TKey>` is the one Data patch shape; projection-specific request documents and media types remain with
  their projection owners.

## Query ownership

- Data.Core plans pushdown and residual work. An adapter translates and executes the filter it is
  given; it does not invent a second query planner.
- An adapter reports filter, sort, pagination, projection, total, and count execution independently. Data validates
  the receipt before fallback or public materialization. A false handled flag is a contract failure, not permission
  for a silent retry.
- Data owns the only correctness order: provider candidates, residual predicate, unhandled sort, true total,
  pagination, projection, then application-visible Lifecycle. Candidate rows discarded by those stages do not emit
  load callbacks.
- `QueryDefinition.CountStrategy == null` means no total was requested. Pagination alone must not
  cause an adapter to add count work.
- `DataCaps.Query.ProviderBoundedPaging` means the adapter faithfully executes the
  coordinator-supplied pushable candidate filter and applies the requested candidate page before
  application materialization. Each result must report provider-handled pagination and the complete
  total order. It does not promise cursor resumption, snapshot isolation, or mutation-safe iteration.
- There is no public `Pager`, cursor, resume-token, or provider stream primitive in this assembly.
  Data.Core composes its qualified `AllStream`/`QueryStream` facade from numbered provider pages.

## Entity and batch receipts

- `GetMany` is positional: output cardinality equals input cardinality; identities remain in input order; duplicates
  repeat the same visible result; missing or invisible identities occupy `null` slots.
- Native bulk methods execute once for the prepared set. A mismatched affected count is outcome-unknown and is never
  replayed. Lifecycle preparation completes for the whole set before dispatch; completion callbacks run only after
  an exact successful receipt.
- Exact upsert insert/update distinction is optional and requires both `DataCaps.Write.MutationOutcomes` and
  `IMutationOutcomeRepository`. Ordinary `Save` remains the compact path when the distinction is irrelevant.
- `IBatchSet.ExecutionCapabilities` qualifies the created execution seam. `BatchResult.Atomicity` and
  `CommitOutcome` report what occurred. Complete item outcomes are returned in logical builder order even when the
  provider groups native adds, updates, and deletes.
- Data's transaction coordinator is deferred sequential coordination, published as `TxCaps.DeferredCoordination`.
  It never advertises local/native atomicity; native transaction capabilities belong to the provider's Direct seam.

## Error and compatibility posture

- Unsupported optional behavior should be reported through capability negotiation or a corrective
  `NotSupportedException`, not silently approximated as a stronger guarantee.
- Data owns provider-neutral failure kind, commit outcome, retry disposition, and replay disposition. Adapters
  classify native exception types/codes through `IDataFailureClassifier`; message-text classification is invalid.
- `Committed` and commit `Unknown` outcomes require replay `Never`. Timeout is distinct from caller cancellation,
  and retryability never implies business-operation replay safety.
- Adding a capability token is not sufficient by itself: the adapter must implement and test the
  advertised behavior.

## References

- [DATA-0107 — provider-bounded Entity streams](https://github.com/sylin-org/koan-framework/blob/main/docs/decisions/DATA-0107-provider-bounded-entity-streams.md)
- [DATA-0096 — unified filter pipeline](https://github.com/sylin-org/koan-framework/blob/main/docs/decisions/DATA-0096-unified-filter-pipeline.md)
- [ARCH-0084 — unified capability model](https://github.com/sylin-org/koan-framework/blob/main/docs/decisions/ARCH-0084-unified-capability-model.md)
- [ARCH-0040 — configuration and constants naming](https://github.com/sylin-org/koan-framework/blob/main/docs/decisions/ARCH-0040-config-and-constants-naming.md)

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
## Atomic insertion contract

`IInsertOnlyRepository<TEntity,TKey>.Insert` requires native physical identity uniqueness. Success
returns the submitted entity with its assigned key and Inserted/Committed. A proven collision returns
Conflict/NotCommitted with the submitted key and no entity. It never loads or returns the existing row.
Unsupported shape, unrelated constraint errors, cancellation after dispatch, and uncertain commit must
not become collision receipts. `DataCaps.Write.InsertOnly` is shape-qualified by the selected repository.
## Counterpart query contract

SameIdInFilter is a logical same-Entity identity relationship. Data replaces it with a
BoundSameIdInFilter containing a scoped predicate and opaque provider target captured under
the guarded target context. ICounterpartQueryRepository binds that target without candidate
reads. FilterSupport.SupportsSameIdIn defaults false, including FilterSupport.Full.
Filter.Snapshot owns operand/path/set collections and clones binary atoms. Ordinary row-only
filters preserve existing opaque provider scalar behavior. Counterpart trees and their isolation
scopes require supported immutable or copied atoms; unsupported opaque values reject before
provider dispatch. DateTime equivalence preserves its binary representation, including kind and
ambiguous local state. ClrFilter remains explicitly opaque and cannot enter a counterpart query. IQueryReadEvidence is operation-local Data
evidence for returned row identities, references, routes, partitions, and scope; it is not
a reusable permission or provider snapshot-isolation receipt.

ReadEvidence metadata is ignored by System.Text.Json and by serializers honoring IgnoreDataMember,
including the default Newtonsoft.Json contract. It must not be exposed as application JSON.

## Conditional write guard

IConditionalWriteRepository.ConditionalReplaceAsync now accepts the existing normalized Filter,
not an Expression. This intentional low-level interface change removes adapter-local late lambda
capture; existing low-level callers use LinqFilterCompiler.Compile explicitly. Required Entity
ReplaceIf intent has no read-then-save fallback. Strict snapshot, complete native support and row-only
qualification belong to Data admission. Binary atoms are copied; unsupported opaque atoms refuse.

Document CAS implementations using the existing CLR evaluator call CompileConditional. That bounded
entrypoint rejects managed, binary and DateTime fields rather than claiming unproved comparison
semantics. The ordinary query evaluator remains unchanged. A bool cannot describe a postcommit
exception: native acknowledgement failure, cancellation and completion errors propagate.
