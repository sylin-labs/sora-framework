---
type: PLAN
domain: framework
title: "Data transaction and query truth"
audience: [maintainers, ai-agents]
status: current
last_updated: 2026-09-12
framework_version: v1.0.0
validation:
  status: passed
  scope: Data Core owner suite, SQLite provider-bounded paging, and independent pinned-runtime SQLite/Mongo evidence
---

# AE-19 — Data transaction and query truth

## Application intent

A consumer can coordinate deferred Entity writes without being told they are atomic, and can fetch a
filtered, ordered, limited Entity list without paying for an unrequested total.

## Public expression and complete intent surface

```csharp
using (EntityContext.Transaction("publish"))
{
    await message.Save(ct);
    await receipt.Save(ct);
    await EntityContext.Commit(ct);
}

var page = await Message.Query(
    message => message.RoomKey == roomKey && message.Sequence > after,
    QueryDefinition.All
        .WithSort<Message>(sort => sort.OrderBy(message => message.Sequence))
        .WithPagination(1, 21),
    ct);
```

The transaction expression requests deferred, ordered coordination. It does not request or receive
native local or distributed atomicity. The list expression requests items only because its
`CountStrategy` is null. `QueryWithCount` remains the explicit items-plus-total expression.

No package, registration, decoration, configuration, or new public type is added. A caller requiring a
proved atomic batch uses the existing same-Entity `BatchOptions(RequireAtomic: true)` seam; unsupported
adapters reject before lifecycle or provider dispatch.

## Guarantee and corrective failure

- `EntityContext.Transaction` holds operations until `Commit`, then executes them sequentially. A
  failure can leave a durable prefix. `TransactionException.CompletedOperationCount`, commit outcome,
  retry disposition, and replay disposition describe that boundary; rollback discards only work not
  already dispatched.
- `Query` and `All` preserve the `QueryDefinition.CountStrategy` supplied by the caller. Null remains
  count-free. `QueryWithCount` substitutes `Optimized` only on its explicitly counted surface.
- `BatchOptions.RequireAtomic` remains the only Entity-level fail-before-write atomicity requirement,
  and it applies to one Entity root through one qualified native batch.
- There is no current Entity-level cross-Entity atomic transaction. Required local or distributed
  atomicity must not fall back to the deferred coordinator.

## Coalescence and ergonomics

The closest count-free pattern is `Data<TEntity,TKey>.PageCore`, while `QueryWithCount` owns the full
materialized plan/execute/finalize path. Absorb item-only and numbered-page materialization into that
one Data Core owner, parameterized by nullable count intent, and delete the duplicate `PageCore`
execution body. Do not add `QueryWithoutCount`, a count boolean, or another result type: `Query` versus
`QueryWithCount` already says the business decision and is legible to people, IntelliSense, and coding
models.

The transaction coordinator remains the sole deferred-coordination owner. Keep its implementation;
replace atomic wording in the public facade, runtime narration, and current guidance. Do not introduce
a second coordinator or publish `TxCaps.Local`/`Distributed` without a native proof.

## Bounded implementation

1. Correct `EntityContext.Transaction` and coordinator narration.
2. Add a failure-after-one-operation regression proving the durable prefix and non-replay receipt.
3. Preserve nullable count intent through list-returning materialized queries and retain explicit
   `QueryWithCount` semantics.
4. Add a facade-level predicate/sort/page regression plus the existing SQLite null-count proof.
5. Correct current transaction/outbox and entity-read guidance.
6. Forward effective native batch execution capabilities through the application-facing facade and
   transparent cache/variant wrappers; retain fail-closed `RequireAtomic` qualification.

## Batch capability transparency

`IBatchSet.ExecutionCapabilities` is already the public pre-execution contract for the created batch.
The application-facing `RepositoryFacade.BatchFacade` consumed its native value during `Save` but inherited
the interface default (`None`) when callers inspected it. PostgreSQL therefore executed and receipted a
qualified atomic batch while `Message.Batch().ExecutionCapabilities` contradicted that same execution seam.

The facade now creates and retains one pure native batch plan, exposes the intersection of its native
guarantees and the provider's advertised atomic-batch fact, and dispatches that same plan. A queued
soft-delete lowering removes atomic and complete-item-outcome claims because that path spans the native
batch plus separate updates. Transparent cache and polymorphic-variant wrappers forward the qualified
value rather than resetting it to the interface's conservative default. `RequireAtomic` remains fail-closed.

## Native cross-Entity atomicity investigation

The smallest sound future contract is not another option on the sequential coordinator. It needs:

1. A required-atomic scope that captures all intended participants before dispatch.
2. Pure preflight resolving every Entity root to its physical source, provider, and native transaction
   family before lifecycle callbacks or provider I/O.
3. One provider-owned unit of work spanning repositories/collections on a single physical source.
   Relational adapters must share one `DbConnection`/`DbTransaction`; Mongo must share one client
   session and prove transaction-capable topology.
4. Corrective rejection when participants span sources/providers without a proved distributed
   implementation. Koan should not emulate distributed atomicity.
5. One rollback/commit receipt with an unambiguous outcome. Lifecycle callbacks and external effects
   remain outside native rollback unless separately staged.

Implementation slices would be: inert contracts and preflight; relational family realization; Mongo
session realization; single-source multi-Entity conformance; then explicit cross-source rejection.
Until those proofs exist, applications needing several Entity roots must use a durable, idempotent
recovery workflow or remodel one aggregate into a qualifying native batch.

## Evidence plan

- The complete Data Core owner suite passed after the final batch-capability correction: 561 tests,
  zero failures.
- The focused SQLite provider-bounded paging proof passed: two tests, zero failures, including null count
  intent on the ordinary page surface.
- The Data Core capability regression proves the public batch reports native atomic + complete-item
  guarantees only when the provider fact also qualifies atomicity. The read-only source guard regression
  also proves that lazy capability discovery does not move inner batch construction ahead of policy rejection.
- Tangent independently reproduced the original failures against pinned runtime
  `e07a84cc3f71a0867f1122b03b723cc80727e772` on SQLite and MongoDB 8.3.4. Mongo confirmed the same
  durable-prefix boundary, pre-I/O `RequireAtomic` rejection, exact count plus find on the materialized
  facade, and find-without-count on `QueryStream`. Its raw receipt is
  `E:/repo/github/sylin-org/tangent-space/.local/experiments/epic005/mongo-baseline-d342e81c22a942798f0b819666fd0765/result.json`.
- Current-guide/source search rejects claims that deferred coordination is atomic or a transactional
  outbox; historical ADRs and assessment evidence remain dated records rather than rewritten history.
- No full release certification, publication, consumer pin change, or cross-provider atomicity claim.
