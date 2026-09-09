# AE-07: Scoped endpoint insert safety

Current engineering receipt, 2026-09-09. Base: `c16c878ab`. This card records a framework
defect discovered during brownfield application characterization; it is not a new application DSL.
Implementation is authorized under the maintainer's standing framework correction scope.

**Task:** Prevent a request-hidden existing identity from being overwritten through the constrained
endpoint's Create authorization branch.

**Application intent:** A user can create their own record and update an authorized record, while a
record hidden by validated request context cannot become theirs by submitting its identifier.

**Public expression:** Existing `Todo : Entity<Todo>`, `EntityController<Todo>`, `EntityAccess<Todo>`
Create/Update constraints, and a validated `IWebContextContributor` predicate under `AddKoan()`.
The referenced elected connector must support atomic identity insertion. There is no new consumer
registration, option, attribute, provider registry, context bridge, or Entity verb.

**Guarantee/correction:** A constrained single-item request classified as Create executes an atomic
insert. Identity collision returns existence-hiding 404 without replacement or existing-row data.
Unsupported providers and mapped identity shapes reject before entity persistence. Ordinary Save
and unrestricted upsert remain upserts. Unknown commit outcomes remain failures, never conflicts
with a false NotCommitted receipt.

**Complete intent surface:** No additional application action. Provider support is automatic from the
selected connector and physical identity shape. Initial native implementations: InMemory, SQLite,
and ordinary MongoDB `_id` storage. No unsupported fallback is permitted.

**Public concepts:** `IInsertOnlyRepository<TEntity,TKey>.Insert` and `DataCaps.Write.InsertOnly`
express the provider guarantee. Existing `MutationResult`, `MutationOutcome`, and `DataCommitOutcome`
carry the result. These are framework/provider extension vocabulary, not application setup.

Consumer review refined mutation qualification: an access realization may govern only Read. The
endpoint compiles the actual Create/Update AccessFilter predicates and stamps once per request.
Only a nonempty mutation filter requires classification. An allowed coarse gate and empty mutation
constraints retain ordinary single and bulk upserts, even if reads are scoped. This includes authority
from a server AgentGrant. There is no consumer opt-out or second gate evaluation. Focused tests cover
both token-admin and grant-admin read-only policies alongside genuine Create/Update constraints.

**Docs read:** Root README establishes the unchanged Todo grammar; CLAUDE and architecture principles
assign semantics to pillars and mechanics to adapters; llms/product-surface establish current
documentation authority; engineering README and toc establish documentation placement; Data Core,
Data Abstractions, Web, Cache and connector documentation establish lifecycle, visibility, native
storage, and capability boundaries; application-evolution NOW/work-items establish this receipt's home.

**Code read:** `EntityEndpointService.Upsert/UpsertMany` classify a filtered null as Create and then
upsert; `RepositoryFacade` owns guards, lifecycle, managed stamps and transforms;
`IMutationOutcomeRepository` distinguishes successful upserts but does not prohibit replacement;
`Batch.Add` is not insert-only; `TransactionCoordinator` replays writes without native rollback;
`DataService` places decorators inside the semantic facade; `CachedRepository` must explicitly forward
native contracts; `EntityVariantRepository` must forward through the root owner; InMemory writes do
not share RowGate; SQLite PrepareInsert can omit conflict updates; Mongo mapped keys are not
necessarily unique because schema setup skips primary mapped indexes.

**Reusing:** Existing mutation receipts, capability tokens, source operation guard, operation horizon,
write plans, upsert lifecycle, managed write scope, cache invalidation, serializers and provider identity
plans. Searches of selected contracts/options/constants found no insert-only guarantee. No new option
is needed; new stable Web error codes belong in KoanWebConstants.

**Creating new:**

| Code | Location | Reason |
| --- | --- | --- |
| Optional insert contract | Data.Abstractions/IInsertOnlyRepository.cs | Shared provider guarantee, no Web dependency |
| Insert capability | Data.Abstractions/Capabilities/DataCaps.cs | Data-owned negotiation vocabulary |
| Semantic insert | Data.Core/RepositoryFacade.cs | One unavoidable guard/lifecycle/transform owner |
| Variant forwarding | Data.Core/Polymorphism/EntityVariantRepository.cs | Root identity remains authoritative |
| Cache forwarding | Cache/Decorators/CachedRepository.cs | Preserve optional terminal through existing decorator |
| Native inserts and claims | Connectors/Data/InMemory, Sqlite, Mongo | Atomic physical mechanism and shape limits |
| Endpoint selection and errors | Web/Endpoints/EntityEndpointService.cs; Infrastructure/KoanWebConstants.cs | Authorization and protocol disposition |
| Rejected delta suppression | Mcp/Execution/MutationDeltaProjector.cs | Existing protocol projection owner suppresses any short-circuited mutation delta |
| Focused proofs | Existing Data, Web, Cache and connector suites | Native persisted-state and no-completion guarantees |

**Coalescence:** Rebuild the constrained Create terminal, keep ordinary Save/upsert, absorb receipt and
lifecycle machinery at Data. Do not suppress visibility filters, inspect hidden entities, use outcome
upsert as conditional insertion, or invent an atomic mixed batch. The narrower Web owner cannot enforce
physical uniqueness; the wider Core owner does not own persistence semantics.

**Ergonomics:** Consumer code keeps saying Todo. One automatic safety correction, no activation knobs
or second access registry. Corrective failures name the unsupported guarantee and supported action.

**Constraints satisfied:** Controllers remain the HTTP owner; no inline routes; stable errors use Web
constants; no tunables, service locators or application repositories; no unbounded read; docs accompany
behavior; only focused owner tests are planned. No commits, publication, package-version edits or
production data operations were performed by the implementation worker.

**Risks and explicit interim compatibility boundary:** Constrained bulk requests containing any
create-classified row reject before persistence. Restricting this only to presently scoped reads would
leave null-read/create races unprotected, so this bound applies regardless of current read contributors.
Visible authorized update-only batches retain existing semantics. A native atomic mixed contract is
separate work. Dry-run is a tentative validation preview, does not execute insertion, and cannot prove
identity availability; it must reveal no hidden prior state. Existing authorized-update races remain:
ownership change or delete/recreate between authorization and upsert requires a future atomic predicate
or version guarantee. Before handlers can run for an attempted insertion; completion handlers, success
audit, and successful deltas require committed insertion. Mongo non-_id mapping support is deliberately
unclaimed until physical uniqueness is proven.

**Open KGE-01 governance finding:** Owner-gate-only row enforcement is outside this correction's
guarantee. `EntityFloorAuthorizationProvider.EvaluateAsync` deliberately treats authenticated callers
as owner-satisfied at the coarse gate and enriches a local principal when an AgentGrant authorizes.
`EntityEndpointService.RowProjection` separately evaluates row policy using `context.User`, while
the existing Upsert mutation checks use Create/Update Constrain predicates. The coarse owner pass
does not itself prove row ownership, and the enriched principal is not returned to row projection.
Inferring additional mutation restrictions from the raw principal would disagree with server-granted
authority. This slice preserves those existing boundaries and adds no owner-gate evaluator; a separate
authorization-owner correction must resolve effective authority and row enforcement together.

## Verification receipt

Implemented and independently reviewed. The pre-change synthetic Web reproduction returned 200 and
replaced another owner's row. The same regression now returns 404 and preserves owner/content.
Local raw evidence remains outside the repository in the task's temporary test directory; no host
identity or production data is included here.

Focused completed proofs:

- Web: eleven ScopedMutationSafetySpec cases, including hidden POST/PUT, null-read race, one-winner
  concurrency, requested-set isolation, whole-bulk preflight rejection, and non-disclosing dry-run.
  Read-only token-admin and server-granted-admin policies retain single and bulk upsert. A stamp-only
  Create declaration also protects hidden collisions without any Update predicates.
- Data: thirty focused cases include receipt coupling, no replay/completion on invalid receipts, null
  Prior without a store read, source ReadOnly and deferred-coordination rejection, variant/root identity,
  and detached field transforms.
- Cache: real AddKoan composition forwards insertion and preserves cached and physical originals
  after a conflict. Restore reports the existing Microsoft.Build.Tasks.Git NU1902 dependency advisory;
  no dependency change or warning suppression belongs to this slice.
- MCP: scoped collision emits no delta or private prior; dry-run performs no completion and persists
  nothing. Existing dry-run/state-delta cases remain passing.
- Native SQLite: six cases prove unchanged stored JSON, single winner, generated identity,
  unrelated constraints, and trigger failures that must not become NotCommitted conflicts.
- Native Mongo: five cases prove unchanged BSON, single winner, unrelated unique failures, and
  rejection of unproven mapped identities.

Default numeric identities reject before native insertion in InMemory, Mongo and non-generated
SQLite. A mapped SQLite Generated identity remains supported. This prevents a predictable bad key
from committing and only then failing the semantic receipt check.

SQLite uses INSERT OR ABORT and qualifies a primary-key collision by native code and exact physical
identity target; zero affected rows and unmatched diagnostics remain failures. Mongo uses an identity
filter with only $setOnInsert, so matching an existing identity performs no update and the acknowledged
match receipt establishes collision without a hidden-row read. These mechanics refine the original
InsertOne proposal, whose driver exception loses structured key-pattern evidence.

Independent GLM 5.3 red-team review and its coordinating reviewer accepted the settled insertion
scope after correcting the overbroad accessor qualifier, default numeric keys, and an attempted
parallel owner-gate inference. Session `ses_f790b1911ffeHDb2IRBHdNmlu6` completed its initial and
corrective reviews with exit 0. A stale-snapshot claim about failing tests was withdrawn; the lead
independently inspected all six TRX receipts: 59 passed, zero failed, zero skipped. Raw traces remain
private. Review acceptance does not cover the explicit broader authorization limits above.

No full certification, package publication, production deployment, or public-package consumer
acceptance is claimed. The existing application boundary remains open.
