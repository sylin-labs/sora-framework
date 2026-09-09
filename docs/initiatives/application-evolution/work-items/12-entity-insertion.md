# AE-12: Entity insertion with an exact receipt

Explore accepted for implementation, 2026-09-09, baseline
`6f5a6576b185a008b29f2aeb47d981dcd28004de`. The lead approved the thin public expression and
ordinary single-entity Save default-partition correction. No family generator, provider, version,
commit or publication change belongs to this slice. Focused owner verification and independent source
review passed; publication and application adoption are not claimed.

**Task:** Make existing native insert-only semantics available through ordinary Entity syntax.

**Application intent:** Create a chosen identity only if no physical row already owns it, including a
row hidden by the caller's read scope. A publication or reconciliation workflow must not replace an
existing row merely because an earlier read found nothing.

**Public expression:**

```csharp
var result = await candidate.Insert(partition: "", ct: ct);
// Equivalent root expression: await Work.Insert(candidate, partition: "", ct: ct).
if (result.Outcome == MutationOutcome.Conflict)
{
    // Resolve the business conflict without receiving the existing row.
}
```

**Complete intent surface:** Reference Data.Core and a qualified existing connector, use normal
`AddKoan()` and its normal source configuration, declare an Entity and supply the candidate.
`Insert` returns the existing `MutationResult<TEntity,TKey>`; it introduces no result type, bool alias,
registry, option, capability token or transaction engine. The chosen identity and retry/recovery
decision remain application policy. Ordinary `Save` remains an upsert.

**Guarantee and correction:** Inserted/Committed reports the submitted application entity with its
assigned key after normal completion. Conflict/NotCommitted proves identity collision, returns the
submitted key and no entity, and never replaces the existing row. Unsupported insertion, deferred
coordination or unqualified native identity/mapping refuses. Other constraint failures, ambiguous
commit, invalid provider receipts and post-commit lifecycle failures remain exceptions; an exception
does not prove rollback. There is no facade retry or read-then-save fallback.

**Partition intent:** Null inherits the current partition; empty selects default; a named value
selects that partition. Enter the existing scope before resolving the repository and await insertion
inside it, restoring the caller on success, conflict, refusal and cancellation. Source, adapter,
tenant and segmentation policy continue through the existing facade. Ordinary single-entity
`Save(partition: "")` must reach the same default routing rather than rejecting empty in Entity.Upsert.
Other bulk and conditional-change overloads are outside this correction.

## Evidence and owner selection

**Docs read:** Root README, `llms.txt`, architecture principles and Entity semantics establish the
Entity-first grammar and truthful guarantees. CLAUDE, Explore, engineering README, product-surface
and TOC establish the pre-implementation receipt and current owners. Data.Core README/TECHNICAL and
Data.Abstractions TECHNICAL establish the existing insertion receipt and facade lifecycle. DATA-0109
defines the family companion's four exact point-Get forwards, not a universal static typing promise.
GW KGE-03/KGE-04 findings supply missing-destination intent but do not authorize application recovery
changes before its own baseline and domain decisions.

**Code read:** `Data.cs`, `Model/Entity.cs` and `AggregateExtensions.cs` own routing and public grammar.
`WithPartition` already distinguishes null from empty. Entity's partitioned Upsert rejects empty
before reaching it. `IInsertOnlyRepository` and `MutationResult` already define the operation and
receipt. `RepositoryFacade.Insert` owns source/write policy, capability admission, no-prior lifecycle,
transforms, one dispatch, receipt validation and completion. `CachedRepository.Insert` and
`EntityVariantRepository.Insert` already preserve that boundary. Mongo, SQLite and InMemory own
native collision classification. The family generator emits only point-Get forwards; root-typed
static writes and exact string-key receiver inference are existing grammar.

**Reusing:** Existing insertion interface, receipt, scope lease, facade, cache/variant forwarding,
provider capability and native implementations. Existing owner tests cover collision races, hidden
prior rows, lifecycle/transforms, impossible receipts, deferred coordination and source refusal.

**Creating new:**

| Owner | Change |
| --- | --- |
| `src/Koan.Data.Core/Data.cs` | Thin Insert forwarding with awaited existing partition scope |
| `src/Koan.Data.Core/Model/Entity.cs` | Static Insert forward; allow explicit empty on single Upsert |
| `src/Koan.Data.Core/AggregateExtensions.cs` | String-key and key-inferred Entity receiver forwards |
| `src/Koan.Data.Core/README.md`, `TECHNICAL.md` | Document the one public expression and its limits |
| `tests/Suites/Data/Core/Koan.Tests.Data.Core/Specs/Entity/EntityInsertionPublicSpec.cs` | Public routing, receipt and compile-time family typing proof |

**Coalescence:** Add no second semantic owner and remove the stale claim that no Entity insertion
verb is needed. Data forwards; the facade continues to interpret receipts and policy. No provider or
family generator change is justified by this syntax addition.

**Ergonomics:** `await candidate.Insert(...)` expresses a stronger guarantee than Save with one verb
and the existing result. Static root and inherited variant static methods return the root-typed
receipt; a string-key variant receiver returns its exact typed receipt. A root static accepting a
runtime variant must retain its discriminator, runtime entity and shared identity collision rules.
No new exact non-string family-variant typing promise is made.

**Constraints satisfied:** No new registration, source configuration, background coordination,
raw-provider application call, unbounded query, provider fallback or services. No complete framework
certification for this small forwarding slice.

## Proof plan and limits

First run the new empty-partition Save test against the baseline and retain its expected failure.
Then prove new public Insert routing for inherited/default/named partition, receipt and same-instance
behavior, collision preservation, pre-dispatch cancellation, and async lifecycle scope restoration.
Compile and execute root/inherited-static versus exact string-key receiver family calls and a
non-string root receiver. Reuse existing facade, Cache, Mongo and SQLite insertion tests rather than
repeat their full implementation matrix in new public tests. Use existing local fixtures only.

Mongo's initial insertion capability is acknowledged managed `_id` with an assigned non-default key;
SQLite also qualifies mapped generated keys; InMemory proves host-local atomic insertion. Cross-store
transactionality, read snapshot isolation, process-restart recovery, authorization after final
completion and application idempotency policy are not added by this expression.

## Implementation and verification receipt

The implementation retains exactly three production source owners. Data.Insert is one ordinary async
method with validation, the existing partition lease, repository resolution and awaited insertion.
There is no expression capture requiring an extra synchronous wrapper. Static and receiver methods
only forward; Entity.Upsert admits explicit empty while preserving its null/nonempty-whitespace
refusal. Root/inherited static and exact string-key variant receiver assignments compile and execute.
No generator or provider source changed.

Independent source review accepted the bounded forwarding, scope restoration, receipt typing and
failure tests with no blocker. The lead requested removal of the initial redundant local async
closure; the final public-expression suite was rerun after that reduction. Native owner receipts
remain valid because their implementations were unchanged.

All receipts are private under `%TEMP%/koan-ae12`, with matching `.log` and `.command.txt` files:

| Proof | Result | TRX receipt |
| --- | --- | --- |
| Baseline empty-partition Save | 1 expected failure: Entity.Upsert rejects empty before routing | `save-empty-before-20260909-173030589.trx` |
| Final public Entity expressions and empty Save | 12 passed | `entity-insert-public-reviewed-20260909-173737702.trx` |
| Existing facade, variant, deferred and source insertion owners | 12 passed | `insert-core-owners-20260909-173502839.trx` |
| Existing SQLite insertion owner | 6 passed | `insert-sqlite-owner-20260909-173546601.trx` |
| Existing Mongo insertion owner | 5 passed | `insert-mongo-owner-20260909-173837686.trx` |
| Existing Cache insertion forwarding owner | 1 passed | `insert-cache-owner-20260909-173636587.trx` |

The 36 distinct final cases passed with no skipped cases or compiler warnings. Mongo used the already
running local test endpoint through the fixture's process-only connection override; no container or
service was started. SQLite and InMemory reused existing test fixtures. This is affected-owner
verification, not complete framework certification or a claim of application recovery guarantees.

The lead parsed all five final receipts and reviewed the source and new public tests. Repository
coherence passes all eight legs in `TEMP/koan-ae12/coherence.log`, with 21 pre-existing warnings outside
these owners. Documentation code had zero opt-in examples, skills reported no directory, and AOT
checking was static. Normal publication and public-package application acceptance remain pending.
