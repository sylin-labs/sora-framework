# AE-11: Conditional publication through Entity

Explore accepted for implementation, 2026-09-09, baseline
`7a74225ccb7972b4bca8f04a011186fa39116bd5`. The lead approved this normalized contract and bounded implementation. Focused owner verification, independent review and repository coherence have passed; publication and consumer acceptance are not claimed. No application edit, commit, release or version change belongs
to this work. GW KGE-03 owns translation policy and its eventual consumer acceptance.

**Task:** Expose existing-row guarded replacement through Entity and make wrapper capability reports
agree with executable behavior.

**Application intent:** Publish generated locale text only if the destination still has the revision
observed when generation began. A delayed result must not overwrite a later publication or manual edit.

**Public expression:**

```csharp
var expected = prior.PublicationRevision;
var applied = await replacement.ReplaceIf(
    stored => stored.PublicationRevision == expected,
    partition: locale,
    ct: ct);
```

`ReplaceIf` is the only new application verb. Its instance overloads follow `AggregateExtensions.Save`
for string-key inference and generic Entity receivers. Data owns normalization and execution. A bool
is sufficient: true means acknowledged replacement and completed normal post-write work; false means
the row was absent or its guard did not match, with no replacement. False does not reveal the row.
There is no implicit insertion, `SaveIf` synonym, callback-based edit builder or retry flag.

**Guarantee/correction:** The complete guard and destination identity are tested by the native write,
not an earlier read. Unsupported provider, scope, key/mapping or guard intent refuses before mutation;
ordinary `Save` remains an upsert. Never suggest ordinary Upsert as a concurrency-safe correction.

**Complete intent surface:** An Entity type, the existing Data reference and a qualified connector,
normal `AddKoan()`, and that connector's existing source configuration. GW already has Mongo configured.
The expected revision, its advancement and which business changes invalidate it remain application
decisions. Optional Cache uses its existing reference and `[Cacheable]`; no special publication setup,
registry, lease service or background engine is introduced. No new options or constants are needed
beyond the existing conditional-write capability and operation diagnostics.

**Public concepts:** `ReplaceIf` selects an existing-row atomic guarantee; the guard expresses expected
stored state; explicit partition selects its destination; bool reports whether replacement happened.
The application must advance or consume a revision if duplicate delivery must become a conflict.
Reusing the same guard without changing the guarded value does not by itself make repeated writes inert.

## Source findings and normalized-contract comparison

**Docs read:** Root README establishes that code keeps naming the Entity; architecture principles and
the Entity grammar establish a single semantic owner and truthful capabilities. CLAUDE and Explore
require this pre-implementation receipt. Engineering README, TOC and llms index the current docs;
product-surface identifies the Data/Cache/Mongo owners. Data.Core and Data.Abstractions README/TECHNICAL
establish facade lifecycle, source policy and the existing primitive. Cache README/TECHNICAL establish
transparent decoration and canonical cache identity. GW KGE-03 findings establish retained translated
snapshots, current-state Jobs reconciliation and the absence of a cross-document source/locale fence.

**Code read at the baseline:**

- `AggregateExtensions.cs` owns instance Save grammar; `Model/Entity.cs` and `Data.cs` own static
  routing and optional capability probes. `UpsertIfChanged` is read/compare/upsert and cannot implement
  this guarantee. `WithPartition` already distinguishes null/inherit from empty/default.
- `RepositoryFacade.ConditionalReplaceAsync` already enters source/operation guards, rejects active
  managed/read scopes, runs BeforeUpsert, applies write transforms, dispatches conditional replacement
  and runs AfterUpsert only on true. It currently passes a live lambda across readiness/lifecycle awaits.
  Its scoped refusal misleadingly suggests Upsert; replace that correction when implementing.
- `IConditionalWriteRepository` carries a lambda and bool; `DataCaps.Write.ConditionalReplace` is the
  existing capability. Its documentation says callers need a fallback. For required `ReplaceIf`, that
  fallback is corrective refusal or a separately proved native strategy, never read-then-save.
- `CachedRepository` forwards all inner capability tokens but has no conditional interface.
  `EntityVariantRepository` likewise copies root tokens, while its typed conditional probe is null.
- Mongo uses identity AND the compiled predicate AND its managed write guard in one ReplaceOne or
  mapped UpdateOne, with acknowledged MatchedCount. SQLite uses one conditional UPDATE. Neither needs
  a new publication engine. Mapped guard usage and classified-field restrictions must remain enforced.

**Reusing:** Eleven native implementations exist. Ten already lower the lambda with
`LinqFilterCompiler.Compile`: Mongo, SQLite, PostgreSQL, SQL Server, MySQL, Firebird, DuckDB, Redis,
CouchDB and Couchbase. InMemory alone compiles the lambda directly. Existing internal consumers are
four Jobs ledger calls, four embedding-worker calls and three invitation calls in two Identity files.
Their domain fallback decisions are separate from the required Entity verb.

**Accepted decision:** Change the existing primitive's guard parameter to `Filter`, retaining its
name and bool result. This is an intentional low-level interface break, not a second optional
interface or default-interface bridge. Update those eleven adapters and eleven internal call sites
in one change. Lambda application conveniences stay; they lower immediately into the same normalized
Filter used by queries. Internal low-level callers can explicitly lower their existing lambdas.

Freeze the Filter at facade admission, before its first await, using existing
`Filter.Snapshot(..., requireImmutableValues: true)`. Entity lambda lowering must occur synchronously
when the operation is invoked, before any routing lease/readiness/lifecycle await. This deliberately
changes captured-variable timing from adapter dispatch to operation contribution, and needs an
adversarial delayed-lifecycle test. Reject ClrFilter nodes, counterpart nodes and unsupported native
operators as complete trees; do not split off residual predicates. `Filter.Snapshot` currently retains
ClrFilter delegates even in strict mode, so strict atoms alone are not enough. Reuse the existing
field/path and Filter-support owners for admission. Generalize its counterpart-specific snapshot
error text when sharing strict values with mutation guards.

Alternative rejected: a new expression-tree closure freezer retains the primitive signature but
duplicates existing Filter snapshot semantics and risks provider-specific captured values or arbitrary
CLR execution. An AST-to-expression round trip is more machinery still. Normalizing the existing
primitive is the smaller semantic system even though it touches more adapter signatures. It removes
ten late lowerings; InMemory should consume the existing InMemoryFilterEvaluator.

## Execution and failure boundaries

1. Validate model, non-default stable identity and complete frozen guard. Capture the submitted key;
   a lifecycle or payload transform must not retarget it. Do not generate a missing key for replacement.
2. Enter the requested partition before resolving the repository and capability. Null inherits;
   empty selects default; a named value selects that partition. Hold the selected context through
   awaited dispatch/completion and restore the caller on success, refusal and cancellation. Preserve
   the existing source/route binding and operation lease; reject route/context drift before dispatch.
3. Qualify the conditional interface plus capability, complete native guard and existing source,
   segmentation and classified-field rules before provisioning readiness or lifecycle effects. Provider-specific
   mapping/value qualification may occur after advisory BeforeUpsert but must precede mutation.
   Reuse `Guard(..., ensureReadiness: false)` as insertion does, then perform permitted readiness.
   This slice retains corrective refusal for active managed/read scopes and deferred transaction
   coordination. A normalized guard does not automatically grant scope-aware writes.
4. BeforeUpsert and optional Prior read remain existing lifecycle behavior. Prior is advisory and may
   already be stale; it is not the atomic preimage. A false match can still run BeforeUpsert. Only
   acknowledged true runs AfterUpsert. Lifecycle side effects are not rolled back by a conflict.
5. Dispatch exactly once. Missing rows, failed guards and deletion before dispatch return false; a
   successful identical replacement may return true because the predicate matched. No facade retry. Existing bounded native transaction retries remain provider implementation details.
6. Cache forwards to the qualified native primitive and uses its existing removal/key owner only
   after confirmed true. False/refusal must not seed, invalidate or fabricate a successful value.
   Scope/key capture must survive the await; verify cache failure and cancellation after native success.
   Existing in-flight fill/coherence semantics are not upgraded to transactional caching by this verb.

Cancellation before dispatch prevents this write. Cancellation or transport failure after dispatch
can mean an unknown commit. A failure in cache invalidation or AfterUpsert can happen after a confirmed
store commit. The existing bool primitive has no post-commit error receipt: preserve exceptions and
document that an exception is not evidence of rollback. Do not turn these into false, retry them,
or invent a committed result after failed completion. Existing DataFailurePolicy can classify native
failures where evidence exists; no new general exception/retry engine is proposed. Focused tests must
show the persisted row after a throwing AfterUpsert and confirm no second native dispatch.

## Variant and provider honesty

**Variant decision:** Mask ConditionalReplace until an exact stored-membership native guard exists.
Use the existing capability-copy owner to exclude this token while retaining details for others.
Do not read a root, check its CLR type and then issue an unguarded root replacement. Root receivers
whose runtime object is a variant also need qualification; ordinary generic key inference must not
silently erase this boundary. Do not introduce a reserved discriminator string into application code.

**Provider caveat discovered:** InMemory's conditional method locks RowGate, but ordinary WriteAsync
assigns the dictionary and RemoveAsync/ClearAsync mutate it without that gate. Sequential controls
below do not prove atomicity against ordinary Save/Delete. Implementation must use an atomic
compare/exchange against the observed stored record, or consistently synchronize competing writes,
and prove a forced interleaving. The replacement now uses dictionary TryUpdate against the observed serialized record. Two deterministic
getter barriers prove an ordinary Save and Delete can finish between observation and native compare/exchange;
both conditional attempts return false and preserve the competitor. The old RowGate has been deleted.

Native Mongo and SQLite race/cancellation/guard support must be proved using their existing fixtures
before publication. Other signature migrations need focused adapter/contract checks; do not equate
successful compilation with native parity. Recheck mapped identity/TTL variants of conditional
providers rather than assuming the capability token alone covers every configured shape.

## Exact owners and proof plan

**Creating new / current owned files:**

| File | Purpose |
| --- | --- |
| `docs/initiatives/application-evolution/work-items/11-conditional-publication.md` | Accepted contract, implementation and owner receipts |
| `tests/Suites/Data/Core/Koan.Tests.Data.Core/Specs/Entity/ConditionalPublicationSpec.cs` | Native root control and variant capability mismatch |
| `tests/Suites/Cache/Topology/Koan.Tests.Cache.Topology/Specs/ConditionalPublicationCacheSpec.cs` | Same-host native control and cached forwarding mismatch |

Implementation owners are `src/Koan.Data.Core/AggregateExtensions.cs`, `Data.cs`,
`RepositoryFacade.cs`, `Polymorphism/EntityVariantRepository.cs`, the existing
`src/Koan.Data.Abstractions/IConditionalWriteRepository.cs` and `Filtering/Filter.cs`,
`src/Koan.Cache/Decorators/CachedRepository.cs`, and existing native conditional methods in
`src/Connectors/Data/{Mongo,Sqlite,InMemory,Redis,CouchDb,Couchbase,MySql,SqlServer,Firebird,DuckDb}`
plus `src/Koan.Data.Relational.Npgsql/NpgsqlRepository.cs`. Internal consumer migrations belong to
`src/Koan.Jobs/DataJobLedger.cs`, `src/Koan.Data.AI/Workers/EmbeddingWorker.cs`, and
`src/Koan.Identity.Tenancy/Invitations/{InviteIssuanceService,InviteAcceptanceService}.cs`.
Package owner docs and current initiative pointers change only with accepted implementation.

**Baseline receipts:** Real AddKoan hosts, existing native InMemory connector, isolated named
partitions, no new references or services. Four executed cases, two passed controls and two expected
failures, zero skipped:

- `TEMP/koan-ae11/conditional-core-before-20260909-162327334.trx`: root success/conflict/missing
  control passes; variant reports true while its conditional probe is null.
- `TEMP/koan-ae11/conditional-cache-before-20260909-162532095.trx`: uncached control passes;
  Cache reports support but facade dispatch throws NotSupportedException. Both the populated cache
  and bypassed store retain the original after refusal.

These historical baseline receipts prove capability inconsistency and safe refusal. Implementation receipts separately own native, concurrency and lifecycle claims.

**Coalescence:** Keep the existing native compare-and-set engine and facade lifecycle; absorb lambda
lowering into the existing normalized Filter owner; repair Cache forwarding; remove the unsupported
variant claim and duplicated adapter lowering. Data is the correct specificity because all callers
need identity/route/lifecycle guarantees. Jobs remains ledger assurance, and translation remains an
application workflow. There is no representation service or new durable state.

**Ergonomics:** Code continues to name the entity and its expected stored state. IntelliSense exposes
one operation with bool completion, not a repository interface, retry policy or new facet. The lambda
remains ordinary C# while unsupported native meaning fails clearly. The low-level interface break is
explicit and migratable during the unannounced 1.x stabilization train.

**Constraints satisfied:** No HTTP, app, topology, deployment or credentials change; no new option
registry, raw collection routing or unbounded materialization; no broad test certification. Current
Data guard/scope refusal remains. New examples use plain Koan verbs without an Async suffix.

**Risks / consumer limits:** This fences one destination document only. A source read followed by
destination CAS does not establish canonical equality at the exact commit instant; GW's retained
older-translation policy still applies. Missing-destination creation needs the existing insert-only
primitive exposed through an equally honest Entity operation if the app requires it; this proposed
ReplaceIf slice does not complete that path. Legacy rows need an explicit application guard policy.
Revision advancement, manual edits while automation is off, deletion/recreation, routing changes,
duplicate jobs and current source identity remain KGE-03 consumer proof, not framework guarantees.

## Implementation qualification

The sole Entity verb, normalized native interface and wrapper corrections are implemented. Existing
Jobs, embedding and invitation primitive callers lower lambdas explicitly. Ordinary Save and their
existing workflow decisions remain unchanged. No expression compatibility bridge remains.

- Mongo native `_id` replacement is supported. Explicit mapped keys refuse and do not advertise the
  capability because mapping does not establish uniqueness. No mapped UpdateOne fallback remains.
- InMemory uses one observed-record compare/exchange, including competition with ordinary mutations.
  InMemory, Redis, CouchDB and Couchbase reuse `InMemoryFilterEvaluator.CompileConditional`; managed,
  binary and DateTime guard fields refuse because those evaluator comparison semantics are not
  qualified for conditional publication. Ordinary query evaluation is unchanged.
- Redis immediate-expiry replacement refuses before reads. It cannot take an ordinary Delete branch
  outside the transaction's observed-value condition.
- MySQL with `UseAffectedRows=true` masks/refuses conditional replacement because changed-row counts
  cannot distinguish an identical successful match from conflict. Other operations keep that setting.
- Variants mask the native capability; a runtime variant passed as a root receiver also refuses.
- Stable submitted keys, full frozen guards and route checks precede native dispatch. Caller mutation
  of an in-flight model is not a supported way to coordinate writes. Lifecycle Prior remains advisory.

Focused receipt work is complete below. Baseline failures above remain historical evidence; passing
compilation does not certify unexecuted native connectors. Missing-row Entity insertion remains the
next separate prerequisite, exposing the existing Insert/MutationResult owner without a bool alias.
## Focused implementation receipts

All paths below are private TEMP/koan-ae11 artifacts, not committed logs. Latest qualified source:

| Receipt | Result | Scope |
| --- | --- | --- |
| core-final-20260909-170752684.trx | 47 passed, zero failed/skipped | Entity conditional baseline and semantics plus existing EntityExecutionSemantics; contribution capture, lifecycle, identity/route refusal, deterministic InMemory ordinary Save/Delete races |
| sqlite-20260909-170105539.trx | 5 passed, zero failed/skipped | Native match/identical/conflict/missing, eight-contender winner, null/named/default partition and cancellation restoration, mapped numeric identity success and duplicate non-PK schema refusal |
| mongo-conditional-publication-after.trx | 8 passed, zero failed/skipped | Native identity match/counts, concurrency, capture/cancellation/manual Save/Delete, partition routing, mapped duplicate BSON unchanged |
| cache-20260909-170418240.trx | 9 passed, zero failed/skipped | Confirmed-success invalidation, false/missing no invalidation, partition identity, postcommit removal failure/cancellation without replay |

These current runs are warning-clean. Earlier Core attempts found test setup mistakes (auto-generated
null string identity, unsupported span-based array Contains, and ordinary Save's empty-partition overload),
then corrected the fixtures without weakening assertions. The supported List.Contains capture and direct
Filter set snapshot both pass. The first Cache run's nullable-key warning was fixed and rerun cleanly.
Internal-consumer and remaining adapter compilation receipts are recorded below. No complete connector certification or public-package application acceptance is claimed.

**Next grammar prerequisite:** ordinary Save(partition: "") still rejects an empty value while reads
and ReplaceIf select default. Review that divergence together with exposing the existing Insert and
MutationResult owner as an Entity operation. This slice uses the existing EntityContext.Partition("")
for seed setup and does not silently alter ordinary Save. SourceVersion and cross-document source/locale
publication authority remain distinct application/next-owner questions.

| Internal receipt | Result | Scope |
| --- | --- | --- |
| internal-jobs-20260909-170455982.trx | 8 passed | SQLite ledger claim, settlement/progress, reservations, retries, renewal/loss/reaping |
| internal-embedding-20260909-170523593.trx | 3 passed | Pending/reclaimed embedding leases, completion and active-lease exclusion |
| invitation-final-20260909-171007560.trx | 6 passed | Invitation claim/accept/revoke and interrupted-claim recovery |

The initial invitation run exposed one unsupported static string.Equals guard after seat materialization.
The final guard now uses equivalent ordinal string equality through `==`, which the existing compiler
lowers natively. Six existing scenarios pass. Three warnings in that touched owner were removed without
suppressions: the one-pass initial claim loop is a straight conditional with the same decisions; a
claimed capability without its interface refuses explicitly; the IsRedeemable documentation link is
qualified. Existing claim-recovery decisions and final settlement retry limits are unchanged.

Eight remaining production adapter projects compile with zero warnings/errors in
`*-build-20260909-170825919.log`: CouchDB, Couchbase, Redis, MySQL, SQL Server, Firebird, DuckDB and
Npgsql. InMemory/Mongo/SQLite compiled in their executed test closures. The five test projects whose
existing primitive calls changed also compile cleanly in `*Tests-build-20260909-171040581.log`:
Couchbase, Redis, SQL Server, PostgreSQL and Cockroach. Those compilation checks are not runtime
certification of their configured stores.

Current total: **86 focused tests passed, zero failed/skipped, warning-clean final receipts**.
Runtime and test source is frozen for independent review and repository coherence. No commit,
version stamp, remote operation or application workaround was performed by this owner.
Independent final source review accepted the native/wrapper boundaries, deterministic race proof,
postcommit cache limits and the invitation cleanup. The reviewer confirmed mapped SQLite readiness
requires the physical primary key and accepted Mongo mapped refusal rather than inferring uniqueness.
The supported guarantee freezes guard contribution and prevents lifecycle/preparation retargeting;
it does not snapshot an arbitrarily mutated caller model after dispatch. Consumers must not mutate
an in-flight replacement object. Data dispatches once; existing bounded failed-compare native retries
remain adapter behavior and never justify replaying an uncertain completed application write.

## Final ownership handoff

Lead and independent final source review accepted this bounded implementation. Repository coherence
passed all eight legs in `TEMP/koan-ae11/coherence.log`. That broader check reports 21 pre-existing
warnings outside selected owners, three fewer after the invitation cleanup. Documentation code
verification had zero opt-in examples, skills reported no directory, and AOT checking was static;
these are the receipt's actual limits, not additional execution proof.

The exact 54-file AE-11 source/docs/test inventory is retained privately at
`TEMP/koan-ae11/ae11-owned-files.txt`, including the root-authored Mongo test and independently authored
Cache test. It excludes the four separately owned AE-10 discovery-correction files. `git diff --check`
passes. All build/test processes have exited and the build lock is released to the lead. Source and
documentation are frozen for the normal commit/publication boundary; public-package adoption remains
unmeasured by this owner.
