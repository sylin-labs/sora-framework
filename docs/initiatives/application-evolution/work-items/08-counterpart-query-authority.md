# AE-08: Counterpart query authority

Explore completed and implementation authorized, 2026-09-09. Framework baseline: `9e2e30846fb3a1f30b62276ad0b0257bcf1e73c0`
on dev, including the independently accepted AE-07 insertion correction and AE-09 private build
dependency correction. Both remain unpublished. This card incorporates lead and independent
architecture review. The lead approved this contract under standing implementation authorization.
Data/Web/MCP and the Mongo provider slice are implemented locally with 209 passing focused owner
checks and independent source acceptance. The lead owns final mechanical cleanup and release preparation.
The design below is the accepted target; completed evidence is recorded separately, never inferred
from this plan. No commits, publication, or version edits are authorized to the implementation team.

**Task:** Express same-Entity, same-ID counterpart eligibility in a selected partition as
part of the native query predicate, before count, ordering, and pagination.

**Application intent:** Show matching translated catalog content only when its canonical
record currently exists and permits this caller to read it. Search and order translated
fields; use canonical publication, suppression, and claims. Missing translation stays absent
from collections. Existing keyed fallback remains application behavior.

**Public expression:** Prefer this additional overload on the existing Access accumulator:

```csharp
return action == AccessAction.Read
    ? q.Where(WorkVisibility.ReadableBy(Principal), partition: string.Empty)
    : q;
```

The policy must produce a true predicate for admins rather than returning early: a true
predicate still requires an existing counterpart. `Where(predicate)` continues to mean the
current row. `Where(predicate, partition: "")` means the same Entity and identity in the
default partition. This overload requires the partition argument rather than giving it a
default value; explicit null inherits the outer partition, consistently with EntityContext.
The outer query's null partition also inherits ambient context. No identity member string
or generic wrapper is needed.

For application-owned Entity queries, use the same node through existing QueryDefinition:

```csharp
var eligible = Filter.SameIdIn<Invoice>(invoice => invoice.Approved, partition: "");
var page = await Invoice.AllWithCount(
    QueryDefinition.All.Where(eligible).ForPartition("reporting").WithPagination(1, 25), ct);
```

`Filter.SameIdIn<TEntity>(Expression<Func<TEntity, bool>>, string? partition)` is the
Data-owned typed builder. It lowers the lambda using LinqFilterCompiler. Access's overload
is syntax for this builder, not another representation. Invoice is an independent domain
fixture for the shape, not evidence of a second adopted consumer.

**Guarantee/correction:** Returned candidates and the unpaginated count satisfy the entire
content predicate AND counterpart predicate in the provider. An orphan fails even when the
inner predicate is true. Same-ID matching uses the elected identity codec, not ToString.
Unsupported providers, identity shapes, scopes, nested counterpart nodes, residual predicates,
or unsupported uses reject before reading candidates. There is no after-page predicate,
allowed-ID harvest, per-row application lookup, or CLR counterpart evaluation.

**Complete intent surface:** Existing Entity, AddKoan, Access realization, and elected Mongo
reference/configuration remain. The application changes the location of its policy with the
overload above and retains its existing locale request/set. Ordinary managed Mongo identity
storage on one resolved source/database is the first supported shape. No new reference,
registration, attribute, switch, authority registry, job, schema migration, or materialized
authority replica is required. Other connectors remain usable for ordinary queries and reject
this additional guarantee. Initial support does not extend to mapped storage or variant set
queries; the latter already reject at EntityVariantRepository.

**Public concepts:** One counterpart Filter node and its typed builder express a necessary
business relationship. The Access overload exposes only the familiar predicate and partition.
An additive FilterSupport detail expresses native support; it is provider vocabulary. An
internal bound operand value is execution evidence, never application setup or a new query API.

**Docs read:** README and llms.txt establish Entity-first/reference-driven grammar; CLAUDE,
architecture/principles, and the Explore skill establish ownership and minimum public ceremony;
engineering/README and toc establish placement; reference/product-surface and Data.Abstractions,
Data.Core, Web, Mongo README/TECHNICAL files establish the current query, scope, and adapter
contracts. AE-07 records the prerequisite insertion correction and its authorization limits.
The paired application's KGE-01-governed-read-findings.md records live translated search/order,
five failing authority cases, and admin bulk compatibility. The inspected current
LocalizedWorkCountContractTests additionally contains a stale-replica-claim control; counts
in old receipts must not be treated as the current test-method inventory.

**Code read:**

- Data.Abstractions/Filtering/Filter.cs has row, Boolean, and CLR nodes; Exists is field presence.
  FilterSplitter splits unsupported nodes to residual; InMemoryFilterEvaluator throws for unknown
  nodes. Simply adding a node would therefore permit candidate reads before a late failure.
- QueryDefinition carries Partition, but Data.QueryWithCount/CountCore and RepositoryFacade query
  methods do not bind it. Web QueryCollection enters EntityContext itself, explaining why Set
  works. Entity removal helpers consume only nonempty QueryDefinition.Partition, and
  Data.WithPartition treats empty as no-op. Conversely EntityContext.With uses null to inherit
  and its ContextState converts explicit empty to default. These are distinct existing behaviors.
  This is now a reproduced advertised structured-read defect, not only source inference:
  `Structured_query_honors_explicit_partition_and_restores_ambient_scope` returns ambient Name for
  both named and explicit-empty selections against public packages. The lead's private
  `structured-partition-before.trx` records two expected failures. Five canonical-authority failures
  plus these two partition failures remain; translated search/order and canonical claim-marker
  controls pass. These receipts do not establish a shared snapshot guarantee.
- DataService resolves source/adapter/route generation; RepositoryFacade owns source ceiling,
  operation horizon, segmentation, guards, ReadScopeFold, transforms, and load lifecycle.
  DataSegmentationBinding is one operation's immutable bound values. Provider naming alone
  cannot substitute for those semantic actions.
- FilterPushdownCoordinator and QueryReceiptValidator own whole-filter/order/page/count truth.
  Data and RelationshipQueryExecutor plan before calling the facade; streams plan before their
  first page. The new no-residual qualification must run before these paths dispatch candidates.
- AccessFilter, IAccessFilter, QueryOptions, QueryFilterComposer, and EntityAccessConstrainHook
  transport lambda predicates. GetById, parent expansion, RowProjection, and mutation ownership
  checks currently compile lambdas directly. All are consumers, not just collection Query.
- MongoRepository.Query currently runs CountDocuments then Find. MongoQueryCompiler also supports
  computed-sort aggregation. MongoEntityPlan owns codec/identity; CollectionName and the factory's
  INamingProvider own partition naming. Cache query/count methods already forward QueryDefinition.
- GW WorkAccess currently filters replica policy and returns early for admins. WorkVisibilityReadHook
  reads canonical rows after fetch and removes items after TotalCount. These two decisions must
  change together; merely adding another predicate preserves stale denial of newly permitted rows.

**Reusing:** QueryDefinition, Boolean Filter AST, LinqFilterCompiler, FilterSupport, existing
query result receipts, Data route/source/segmentation guards, provider naming/identity plans,
bounded Entity pages, shared REST/MCP endpoint, and per-related-type BuildOptions composition.
Searches found no relational counterpart node or existing cross-partition query-binding contract.
No new options are justified. Stable diagnostic IDs belong beside existing Data/Web/Mongo constants.

**Creating new:** Proposed exact owner changes, not an implementation completed by this card:

| Code | Location | Justification |
| --- | --- | --- |
| SameIdInFilter record, typed Filter.SameIdIn builder | src/Koan.Data.Abstractions/Filtering/SameIdInFilter.cs; Filtering/Filter.cs | Logical relationship belongs to normalized Data input, not Web or Mongo |
| Native counterpart support detail and structural qualification | src/Koan.Data.Abstractions/Filtering/FilterSupport.cs; FilterSplitter.cs | Default false, including FilterSupport.Full; full row operators do not imply relational support |
| Early no-residual rejection and receipt protection | src/Koan.Data.Core/Querying/FilterPushdownCoordinator.cs; QueryReceiptValidator.cs | Reject any residual in a counterpart query before candidates; existing full-filter receipt remains authoritative |
| Bound counterpart operand and scoped query preparation | src/Koan.Data.Core/Querying/CounterpartQueryBinding.cs; RepositoryFacade.cs; DataService.cs | One internal execution owner binds both operands and keeps route leases alive |
| Result-local read evidence | src/Koan.Data.Abstractions/IQueryReadEvidence.cs; RepositoryQueryResult.cs; BoundedQueryResult.cs; src/Koan.Data.Core/QueryResult.cs; Querying/DataQueryExecution.cs | Carry exact rows, identities, route, partition, and scope from Data execution through materialization; excluded from JSON |
| QueryDefinition.Partition normalization | src/Koan.Data.Core/RepositoryFacade.cs; Querying/CounterpartQueryBinding.cs | Bind before readiness, naming, scopes, and dispatch; no provider-private interpretation |
| Access overload and normalized predicate snapshot | src/Koan.Web/Authorization/IAccessFilter.cs; AccessFilter.cs; EntityAccessConstrainHook.cs | Same declaration reaches every read surface without a new registry |
| One normalized filter store and immediate lambda lowering | src/Koan.Web/Hooks/QueryOptions.cs; QueryOptionsExtensions.cs; Endpoints/EntityEndpointService.cs | Replace the lambda list, preserve AddPredicate syntax, and execute one immutable Filter snapshot |
| Keyed/expansion/projection consumers and mutation rejection | src/Koan.Web/Endpoints/EntityEndpointService.cs; GovernedRelationshipExpander.cs; Authorization/RowProjection.cs | A relational declaration must never be compiled as a CLR row predicate or silently omitted |
| Lookup lowering, count/page pipelines, support claim | src/Connectors/Data/Mongo/Runtime/MongoQueryCompiler.cs; MongoRepository.cs; Runtime/MongoFeatures.cs; Infrastructure/Constants.cs | Native mechanics, codec, physical name, and executable support claim stay with the adapter |
| Focused owner proofs | existing Data.Core, Web.WellKnown, MCP.Conformance, Connector.Mongo suites | Use real AddKoan hosts, native persisted fixtures, and command observations |
| Current contract documentation | selected package README/TECHNICAL files; existing query/access ADR owners and initiative progress | Required in implementation, not edited during this design-only pass |

**Coalescence:** Filter.And combines normalized inputs at the existing endpoint, followed by
RepositoryFacade's ReadScopeFilter. The normalized query is the target owner;
Web owns declaration transport and Mongo owns lowering. A wider generic Core authority abstraction
would lose Data identity/source meaning; a narrower Web hook cannot implement database cardinality.
Keep source ceilings and scopes, absorb counterpart meaning into Filter, rebuild the affected
Web consumers around normalized input, and delete superseded consumer post-page authority decisions.

**Normalized storage decision:** Replace the public low-level lambda-list storage. Both
IAccessFilter/AccessFilter and QueryOptions expose a single `Filter? Filter` value; null means no
constraint. The mutation stamp list remains separate because it expresses writes, not predicates.
`q.Where(lambda)` and `opts.AddPredicate(lambda)` remain and immediately lower through
LinqFilterCompiler, then AND into that Filter. The counterpart overload lowers into the same value.
The Access hook contributes its complete Filter once; the endpoint combines it with the
request's user Filter through Filter.And, then freezes the execution snapshot after BuildOptions.
The former QueryFilterComposer pass-through is retired rather than kept as another runtime owner. There is no second
server-filter input, legacy predicate list, mirrored AST, rejecting legacy getter, expression marker,
or default-interface compatibility bridge. All consumers use the complete normalized value.

This intentionally removes `IAccessFilter.Predicates`, `AccessFilter.Predicates`, and
`QueryOptions.Predicates`, including its mutable `List<LambdaExpression>` contract. Direct callers
and custom IAccessFilter implementations must recompile and migrate; compile errors identify the
removed members. The migration is `Where`/`AddPredicate` for lambdas or complete Filter composition
for low-level AST consumers. Release notes must call out this source/binary break and the timing
change below. The maintainer has authorized this rebuild; speculative external list consumers are
not grounds for keeping two authorities on the unpublished train.

The pre-change bounded repository inventory found production member consumers in
`Hooks/QueryOptionsExtensions.cs`, `Filtering/QueryFilterComposer.cs`,
`Authorization/EntityAccess.cs` (Create Where-without-Stamp validation),
`Authorization/EntityAccessConstrainHook.cs`, `Endpoints/EntityEndpointService.cs`, and
`Endpoints/GovernedRelationshipExpander.cs`, plus definitions/docs in IAccessFilter/AccessFilter/
QueryOptions. Migrate RowProjection's lambda arrays and constructor as part of the same cut even
though it does not use the member name. Tests directly inspect the accumulator in
`tests/Suites/Web/Koan.Web.Extensions.Tests/AccessConstrainTests.cs`; update the predicate-hook suite
under `tests/Suites/Web/AdapterSurface/Koan.Web.AdapterSurface.InMemory.Tests/PredicateHook/`
and its stale list documentation. A paired GW search found no `.Predicates` access: WorkAccess uses
q.Where and ArticleVisibilityHook uses opts.AddPredicate, so those ordinary call sites retain syntax.

**Normalization timing:** Recognized captured scalar/collection values are evaluated when
LinqFilterCompiler is called. The old QueryFilterComposer did this after all BuildOptions hooks;
the new lambda conveniences do it at contribution time. Choose and document that timing explicitly:
a contribution records the values present when it is made. Later hooks cannot change that predicate
by mutating its captured variable. Characterize a hook that contributes `row.Owner == captured`
and a later hook that changes captured: the new contract keeps the first value, while an explicit
later Filter contribution still ANDs normally. Snapshot supported collection constants into owned
immutable values. After BuildOptions, freeze the complete filter/sort/partition execution and proof
snapshot; do not rerun Constrain to generate a different proof. Existing opaque ClrFilter nodes
remain row-only fallback expressions with their documented CLR semantics, not falsely frozen
captured state; any counterpart query with such a residual rejects before candidate reads.

**Binding decision:** The counterpart changes only partition. It cannot select another Entity,
source, adapter, tenant, or arbitrary collection. Data prepares the query under the outer effective
partition (null inherits, empty clears), retaining source policy and operation horizon. Under the
explicit target partition it resolves/compares the same route generation, enters the target read
guard, binds segmentation, and folds target read contributors. Source, adapter, logical segmentation
values, identity shape, and route generation must remain compatible; otherwise reject before reads.
DataSegmentationBinding alone is insufficient: ManagedEqualityReadContributor separately calls
ManagedFieldRegistry equality descriptors' ValueProvider and emits legacy isolation predicates.
Capture and compare those applicable descriptor bindings, including null/unscoped state, as well
as segmentation values. Reuse the captured values for the emitted filters rather than calling an
ambient provider again and comparing one value while querying another. Preserve all other target
read contributors. If compatibility of an applicable isolation binding cannot be established,
reject before reading; no alternate authority service is introduced.
The target predicate is inner business predicate AND its bound Data scopes. Root scopes still apply
to the content operand. Do not recursively invoke Web Access to discover authority: the application
already selected that predicate, and doing so would reintroduce the rejected broad request bridge.

The internal bound value contains effective partition, scoped inner Filter, exact entity/root/key
shape, and the existing route binding evidence. Keep this request-scoped; never cache a principal,
bound tenant value, or ambient callback output in a host plan. Acquire the outer read lease first,
then target leases while binding each target under its EntityContext. Exit each target EntityContext
before outer native dispatch, restoring the outer content partition while keeping those read leases
alive. Dispose target leases in reverse acquisition order, then the outer lease, on success, failure,
and cancellation. DataOperationHorizon.Exit checks its ambient stack and rejects out-of-order
disposal; context restoration and lease ownership are distinct lifetimes, not one using block.
Expose the bound value to the adapter
through an editor-hidden provider contract only if the existing cross-assembly query payload cannot
carry it safely; do not create another query service or register an operand resolver. This small
cross-assembly representation is an implementation detail to settle against accessibility, not
permission to skip binding. Provider compilation must reject an unbound counterpart.

QueryDefinition.Partition is normalized for structured reads at this boundary, including ordinary
queries, rather than activating only when a counterpart happens to be present. Characterize null,
empty, named, nested ambient restoration, direct facade, Entity, Web Set, Count, and streaming paths.
The lead additionally reproduced a restricted public-feed leak because positional
`Work.Get(ids, string.Empty)` returned an ambient stale locale replica. Private
`public-event-scope-before.trx` records the failed control. Correct Data.WithPartition in this slice:
null inherits, explicit empty selects default, and the ambient scope restores after success/failure.
This supersedes the initial plan to defer the positional-helper inconsistency. Reject counterpart use in mutation QueryDefinitions
that currently consume only the partition and ignore Filter, including Entity.Remove overloads.

**Native plan:** For each supported counterpart leaf, use an identity-correlated lookup against
the Data-bound target, apply its fully native inner filter/scopes, limit to one identity-only result,
and derive a temporary Boolean existence value. Evaluate the complete outer Boolean tree using
those values. Conjunction-only early root matches are optional optimizations; never pull an OR arm
out as an unconditional match. Support All/Any/Not composition or reject the full unsupported shape
before execution. Nested counterpart nodes reject initially, including ones contributed by target
read scopes, preventing recursive authority expansion.

Reuse Mongo's identity codec, naming provider, and computed-sort stages. Preserve the original
`$$ROOT` inside an internal pipeline envelope; lookup, existence, and computed-sort slots live beside
that envelope, never in the source document. Restore the original document before deserialization.
Do not inject/unset fields on the content row based only on a supposedly unusual prefix: a legitimate
stored property may use that name. Existing computed sorts must use the same envelope when combined
with counterpart stages. No canonical document is hydrated or returned. Count uses the same
qualified match/lookup prefix followed by count; page uses that prefix followed by full translated
sort, stable identity tiebreaker, skip/limit. Prefer the existing two-command count/page posture over
a new facet containing the entire page in one BSON document. Count-only sends no page work and a
query without requested count sends none. The ordinary row-only path retains CountDocuments/Find.
Initial mappings and incompatible source/database/identity shapes reject; do not infer uniqueness.

Same-partition simplification is legal only after both operands have identical bound physical
route, root identity domain, and scope. Then existence follows from the candidate itself and the
inner predicate can replace the lookup. It must still apply target scopes and the inner true/false
predicate; comparing raw partition strings or treating true as an unconditional cross-partition
match is unsafe. Deferring this optimization is acceptable and lowers the first proof burden.

**Surface completion:**

- Collection, count, bounded query, and stream consume the same complete normalized filter.
  Cached Query/Count forwarding must preserve it; do not cache counterpart decisions as entity hits.
- Keyed reads with a relational constraint execute ID AND full filter before content materialization
  and load hooks. Existing application fallback/AfterModelFetch cannot cause that predicate to be
  ignored. Characterize the actual GW keyed fallback separately; the node does not invent fallback.
- Child expansion uses the complete filter with the existing parent-ID bound. Parent expansion
  must stop doing a Get followed by lambda Compile and instead use bounded identity+filter queries.
  Batch where the existing expansion model permits; no new per-returned-row counterpart calls.
- Access projection consumes read authorization evidence for the selected identities instead of
  re-evaluating the relational predicate on translated fields. Bind evidence to exact Entity/root/key
  shape, returned IDs, effective source/route generation and partition, principal/query scope, and
  the frozen normalized predicate/isolation bindings. Keep it within the current result's lifetime;
  it is not a request-level "passed" Boolean or a reusable identity permission cache. AfterCollection
  runs before manifest projection and can add/replace rows. Track returned identities and changes
  to their binding, including replacement objects; a newly added/replaced identity, changed partition,
  or changed proof scope cannot inherit earlier evidence. Revalidate changed candidates in one bounded
  membership query under the same frozen scope or reject before emitting the result/manifest. Never
  drop added rows after pagination and call the old count correct. Keyed fallback and AfterModelFetch
  replacements obey the same rule. Deletions/subsets cannot grant rights to an unproved identity.
  Existing coarse/owner gate behavior is not
  redesigned here. Any counterpart declaration in Create/Update/Delete rejects before hooks/writes,
  including patch, bulk delete, mass operations, and access projection of unsupported mutation intent.
- GW replaces WorkAccess's replica predicate/early admin return. Retain any necessary bounded
  canonical overlay for badges as application projection, but remove its role in deciding collection
  membership/count. Missing canonical during a later overlay must have an explicit concurrent-read
  disposition, not an indexer exception or a claim of snapshot consistency.

**Ergonomics:** Humans and coding models see the existing q.Where plus the business-selected
partition. IntelliSense explains local row versus same-ID counterpart; no owner/entity identity
type parameters are repeated in Access. Direct Entity code uses the same Filter builder. The
extra runtime mechanics are paid only when that relationship exists. Immediate lambda lowering
preserves ordinary call-site syntax while deleting the low-level split storage and its interoperability
ceremony. The documented property break has one correction, not a runtime compatibility mode.

**Alternatives considered:** A canonical-first query is valid only if the locale join and content
filter/sort still occur before page; it is an optional physical plan, not a smaller public model.
An allowed-ID list or after-fetch drop cannot provide bounded correct cardinality. A generic join
DSL/authority registry/general source reference adds decisions this business does not need. A
special Access canonical option leaves direct Entity queries without the same semantic owner.
Expression marker tricks preserve a list type but break the advertised executable-lambda contract.
The previously proposed legacy-list plus server-Filter bridge is rejected: actual consumer evidence
and the maintainer's rebuild mandate favor one normalized store over preserving low-level collection
types. Its rejecting projection/default-interface machinery would add parts without new meaning.

**Constraints satisfied:** Controller routes and MCP contracts remain; Entity statics remain the
application path; no new environment posture, option, storage registry, or service locator; native
bounded paging and qualified streaming; stable diagnostics use owner constants; instruction-first
package/ADR updates belong to implementation. The initial design-only pass changed only this card;
subsequent source and test edits follow the lead's explicit implementation approval.

**Risks and required proof:**

1. Real AddKoan Mongo tests cover public/suppressed canonical rows, raw stale replica status both
   directions, newly granted/revoked claims, true-admin orphan exclusion, missing locale, and a
   translated-only search whose translated sort reverses canonical order. Assert count and page IDs.
   Repeat the shape with Invoice/reporting fixtures without claiming a second deployed consumer.
2. Same IDs across tenants/sources/partitions must never cross-match. Exercise target scope exclusion,
   partition-sensitive contributors, missing required segmentation, read-only/external source policy,
   route generation changes, entity families/mappings rejection, and ambient restoration on failure.
   Include legacy managed equality values and null/unscoped transitions, not only segmentation.
   Assert outer collection dispatch after target context restoration and reverse lease disposal with
   multiple leaves, cancellation, and a failure during target binding. Named/empty structured reads
   must turn the lead's two regressions green; null still inherits and the caller's ambient is restored.
   Use native stored-row and command assertions; names alone are not isolation evidence.
3. Unsupported providers (including SQLite/InMemory), inner CLR residual, unsupported Boolean shape,
   mutation use, and malformed/unbound node fail before any candidate command. A supported counterpart
   plus an unrelated residual also rejects. Provider false FilterHandled receipts never release rows.
4. REST/MCP collections, keyed reads, related parent/child expansion, and access manifests must carry
   the full predicate. Existing row-only lambda hooks, admin mixed bulk writes, keyed fallback,
   ordinary Save, count-only/no-count, and cached read behavior are compatibility controls.
   Add AfterCollection/AfterModelFetch fixtures inserting or replacing identities, changing effective
   partition, and reusing a proof under another query/principal scope; no manifest may inherit an
   unrelated row's evidence. Prove bounded revalidation or pre-emission rejection. Test immediate
   capture normalization, immutable set values, ordinary lambda conveniences, and migrated low-level
   in-repository consumers without retaining the removed property or a default-interface bridge.
5. Observe native commands/explain on isolated synthetic data: one lookup stage per distinct leaf,
   identity index usage, no application ID harvest or N+1 calls, no accidental page/count commands,
   original document preservation when properties collide with every temporary slot name,
   cancellation before and during lookup, and no canonical load lifecycle.
   Compare representative page and count latency/allocation against the existing two-command path;
   command count is not a claim that lookup work is free. No threshold is invented before measurement.
6. Commit suppression/deletion before execution and verify it affects the next query. Also inject
   moderation between count and page and during the application metadata overlay. A shared predicate
   plan is not a shared snapshot: count and page may observe different committed states, and a
   cursor cannot retract already read data. Cross-command snapshot guarantees require separate session/
   transaction/read-concern design. Do not claim exact temporal agreement from this slice alone.

Design recommendation: accept the same-ID Filter semantic and compact Access overload, with the
one normalized store with immediate lambda lowering, both-operand Data binding, and complete
surface rejection/proof included
in the vertical slice. The editor-hidden bound payload shape and keyed fallback/projection receipt
integration are the remaining implementation-review points. Do not reduce scope to Mongo lookup
syntax while leaving those paths able to omit the predicate.

## Implementation evidence in progress

The first native pass has twelve passing Mongo counterpart cases and eight passing affected
connector compatibility cases, without skips or compiler warnings. Native profiling/explain verifies
identity-only limit-one target lookup, count-only/no-count command behavior, and a safe leading
mandatory row match that uses the outer identity index. The keyed explain examines one outer and
one counterpart document. The original full Boolean predicate still runs after lookup; OR/NOT
controls pass. A second-domain Invoice fixture uses Guid identity without string conversion.

The first multiple-target scope test exposed a real lease-lifetime defect: activating leases inside
an async binder then returning them cannot reactivate the one-shot horizon in its caller. The
correction keeps native execution and reverse lease disposal inside the same async binder lifetime,
after target EntityContexts have exited. The three managed-scope/evidence cases then pass, including
changed tenant/null bindings rejecting before candidate commands. This does not relax the horizon's
reverse-disposal check.

The current Core focused receipt has forty-five passing cases, no skips or compiler warnings,
covering structured and positional partition correction, snapshot ownership/refusal, AND-preserving
lambda/JSON query overloads, and affected existing lifecycle/receipt/transaction cases. The lambda/JSON
overloads previously replaced QueryDefinition.Filter; they now preserve it by conjunction so the
new relationship cannot silently disappear when a caller also supplies a content predicate.

Independent review found that the public bounded-candidate path still needed evidence validation
after load lifecycle. It now carries evidence in BoundedQueryResult and validates references,
identities, and scope. The native load-ID regression has been extended to bounded queries and the
structured QueryStream overload; this latest extension awaits the next focused run. Web/MCP
transport and adversarial hook tests remain in progress, so these intermediate receipts do not
close AE-08 or claim consumer acceptance.

Raw receipts remain in the task's private temporary `koan-kge01` directory:
`counterpart-core.trx`, `counterpart-mongo-final.trx`, and `counterpart-mongo-compat.trx`.
Cancellation proof so far is pre-dispatch; committed revocation/deletion affects the next query.
No deterministic between-count/page moderation test, shared snapshot, production benchmark,
package publication, or application compatibility acceptance is claimed by these receipts.


Review corrections awaiting the final focused receipt: evidence carriers now also use
IgnoreDataMember so default Newtonsoft.Json cannot serialize their internal graph. One snapshot
rail owns AST containers and binary values while preserving opaque native scalar support for
ordinary row-only filters; strict counterpart proof snapshots reject unsupported opaque atoms.
DateTime scalar/set equivalence and isolation binding comparison preserve ToBinary representation,
so different timezone interpretations cannot collapse Boolean leaves or share a scope proof.
Owner regressions cover both serializers, ordinary managed binary filtering, date OR/AND meaning,
and date isolation/evidence invalidation. These are source changes until their focused runs pass.


## Explore addendum: source-bound emit projection

Lead consumer review found an adoption blocker after the initial safe emit refusal: GW's
WorkLikesEmitHook, WorkCollectedByMeEmitHook, and WorkClaimedByMeEmitHook replace every collection
with WorkSummary, including anonymous collections. A blanket replacement refusal protects native
read evidence but cannot satisfy this consumer. This is an existing Emit/Projection owner seam,
not a new data authority or permission service.

Lead-approved decision: add `EmitDecision.Project(rows, row => View.From(row) with { ... })`
to the existing emit decision. The factory snapshots typed source references and order plus the
mapper. It does not run the mapper or accept caller-created evidence. HookRunner treats it as a
terminal deferred projection: subsequent emit hooks do not receive either the mapper output or
framework response containers. The endpoint first verifies the exact source sequence against its
selected page and its Data/Web/relationship proofs, invokes the mapper once per source, then checks
source identity and execution/principal scope again. No arbitrary replacement obtains authority
merely because its type implements IProjectionOf. Existing IProjectionOf.From remains ordinary
trusted application mapping code; the new decision constrains which source rows the framework maps,
not the honesty of application DTO field calculations. It cannot add/remove/reorder source members,
change page counts, or issue a reusable permission token.

Framework-owned response shaping occurs after governed emit/projection. The initial Project
contract supports flat collection/query responses. Combining it with map/dict/relationship shaping
rejects correctively before invoking the mapper; no selected view is silently discarded. Next
continues into existing framework-owned map/dict/relationship shaping, whose edges retain their own
read proofs. No subsequent custom hook receives graph/map wrappers. This removes
the need to inspect arbitrary object graphs or retain a growing list of mutable wrapper snapshots.
Ordinary row-only With replacement chains retain their existing behavior; counterpart arbitrary
With and successful custom short-circuit payloads continue to reject. Collection and Query are the
required projection surface for GW; a model projection, if exposed by the same decision, must bind
exactly the selected one-row source with the same checks. Mutation emit does not gain counterpart
support from this decision.

Implementation owners are existing EmitDecision, HookRunner, the existing hook pipeline forwarding
contract if required, and EntityEndpointService. A private deferred mapping implementation can
travel through the existing emit result; it must never serialize as a response. No registry,
public proof object, new identity-bearing entity wrapper, or runtime opt-out is introduced. GW
can combine its three batch personalizers into one hook that loads bounded membership data once
and returns one source-bound mapper. That consumer replacement remains root-owned and uses public
packages only after normal publication.

Required focused proof: native authorized collection and MCP query project exactly the selected
translated rows with original counts/access manifest; anonymous projection works; replacement,
foreign/reordered/subset source lists, ID mutation during mapper, and scope/principal mutation
reject before output; ordinary row-only With chains remain green; map/dict/graph shapes are built
after hook execution and cannot be mutated by a later hook. Keep the interim refusal until this
composition is implemented and tested. This addendum is architecture, not a passing receipt.


## Reviewed Data/provider execution receipt

The final Data/provider review corrections passed focused execution on 2026-09-09:

| Owner selection | Receipt in private TEMP/koan-kge01 | Passed | Skipped |
| --- | --- | ---: | ---: |
| Core counterpart semantics and affected execution/family/transaction cases | counterpart-core-final.trx | 48 | 0 |
| Mongo counterpart query/scope cases | counterpart-mongo-reviewed.trx | 15 | 0 |
| Cache Entity composition and unsupported counterpart preservation | counterpart-cache-final.trx | 3 | 0 |

These runs compiled without compiler warnings. They supersede the intermediate Core45/Mongo12
counts above. The Mongo fifteen include the extended bounded/stream load-ID substitution case,
ordinary managed binary scalar compatibility, DateTime Boolean deduplication, and DateTime isolation
and evidence invalidation. Core includes four-carrier Newtonsoft/System.Text.Json exclusion and
owned sort-member input. Independent Data/Mongo source review found no remaining blocker after
these corrections. The Web terminal projection addition remains in progress and is not covered by
this Data/provider receipt. The three Cache cases also compile warning-clean; a populated identity
cache cannot bypass an unsupported counterpart query. Final Web/MCP evidence is recorded when complete.


Final identity review established that IEntity<TKey> does not promise key immutability. A mutable
reference key could otherwise change behind captured result evidence. Mongo counterpart eligibility
now uses one predicate for Describe, Bind, and target resolution: ordinary managed storage with
string, Guid, or byte/sbyte/short/ushort/int/uint/long/ulong keys. Other key types reject this
capability before candidate reads; their ordinary persistence behavior is unchanged. A byte[] key
capability/binding/Data refusal regression awaits the final Mongo rerun.

The lead also approved one normalized IncludeRelationships boolean on existing QueryOptions,
derived from current request.With meaning before hooks and thereafter the shared shaping owner.
Hooks can inspect Shape/View/IncludeRelationships through HookContext without HTTP or Items keys.
The proof freezes all three. Current GW view=full behavior is being characterized by the lead;
this framework work does not assert the application's existing summary hooks honored that view.


Maintainer simplification review removed the redundant evidence ID-only membership set/API and
public Filter/RouteIdentity echoes. Exact reference plus captured typed-key membership remains;
Web retains duplicate-row rejection and its frozen query/options/principal, while Data retains
route/isolation validation privately. Evidence is attached by Data to the same locally executed
query result, never selected by an application token. Distinct predicates targeting the same
normalized partition now share one target capture/lease/scope stamp inside the binder; each
Boolean leaf and native lookup retains its own predicate. No service or cross-operation cache is
involved. A focused count test checks one source and one target contributor capture for two
distinct counterpart predicates in that target. Final reruns follow these reductions.


## Final implementation handoff, 2026-09-09

The implementation team completed the accepted vertical slice and its review corrections. The
final independent source review accepted the Data/Mongo/Web result. The following non-overlapping
focused selections passed on the final implementation source, with zero failures, zero skips,
and zero compiler warnings. The VSTest runner merely noted replacement of two prior TRX files.

| Owner selection | Private temporary receipt | Passed |
| --- | --- | ---: |
| Data Core semantic and existing execution controls | koan-kge01/counterpart-core-final.trx | 48 |
| Native Mongo counterpart semantics, evidence, key qualification and grouped scopes | koan-kge01/counterpart-mongo-final-reviewed.trx | 17 |
| Cache Entity composition and unsupported counterpart rejection | koan-kge01/counterpart-cache-final.trx | 3 |
| Native REST and registered MCP execution, projection and adversarial hooks | koan-ae08-web/web-native-final.trx | 45 |
| Web read refusal, hooks and existing insertion controls | koan-ae08-web/web-focused-final.trx | 21 |
| Access accumulator and projection compatibility | koan-ae08-web/web-access.trx | 28 |
| InMemory Web adapter visibility compatibility | koan-ae08-web/web-adapter-final.trx | 36 |
| Existing MCP conformance selection | koan-ae08-web/mcp-final.trx | 11 |
| Total | | 209 |

The terminal projection is flat-only, with exact ordered source membership and bounded batch
pre/post validation. Native REST and registered MCP dispatch use real IProjectionOf summaries;
no native Mongo HTTP `/mcp` transport journey is claimed. Framework map/dict/relationship shapes
are created after custom hooks. The normalized Shape/View/IncludeRelationships context lets an
application preserve nonflat behavior without inspecting HTTP. Arbitrary counterpart replacement
and successful custom short-circuit payloads remain refused. Custom denial status is retained while
its arbitrary body is stripped. Model hook stops now consistently precede the corresponding
persistence action; post-write response stops do not undo committed data. Mapper exceptions return
no partial view; mapper side effects are ordinary application code and are not rolled back.

Intentional API/compatibility boundaries remain: the low-level Predicates list properties are
removed, recognized lambda constants snapshot at contribution time, explicit empty partition means
default, and new counterpart/projection identity admission is string/Guid/integral. Mapped storage,
other key shapes, unsupported providers, nested/residual counterpart predicates, and counterpart
mutation declarations refuse. Ordinary Save/upsert and row-only With chains retain their meaning.
Count and page use the same native plan but separate commands; this slice provides no shared
snapshot, no deterministic cancellation-during-lookup proof, no production latency/allocation
benchmark, no independent adopted consumer, and no public-package acceptance or publication.

The lead removed the inert QueryFilterComposer pass-through and its obsolete namespace import.
The first cleanup build found that leftover import; removing it restored compilation. Final native
insertion compatibility adds Mongo five and SQLite six passing checks with zero skips or compiler
warnings (`koan-kge01/insert-mongo-integrated.trx` and `insert-sqlite-integrated.trx`). There are 220
passing focused checks across the selected owners. The lead parsed every receipt rather than relying
on worker summaries. Repository coherence and normal publication are the next boundary; no public
package adoption or production deployment is claimed.

Gposingway's public-package baseline now proves summary/map/dict and caller personalization behavior,
including localized content. Its documented full view still returns summaries because its three emit
hooks project before the controller opt-out. Consolidation will correct that application defect and
remove the intermediate claimant-ID carrier. ReadableBy against canonical state remains the one
business declaration; the post-read metadata overlay must preserve the selected objects and fail the
whole response if a later canonical lookup revokes or loses a selected row. It cannot trim the native
page and keep its count. That consumer implementation follows public-package publication.

Repository coherence completed after classifying six earlier dogfeeding documents as historical
evidence, preserving their original contents. The initial ratchet passed tools, the full Release
solution build, composition lockfiles, documentation lint, code-example scope, skills lint and AOT
source lint; only the public documentation classification gate failed. That gate and focused
front-matter validation now pass. Generated product surface and agent-skill snapshot/structure checks
also pass. No full behavioral certification or NativeAOT runtime proof is claimed by these checks.
The full solution build reported 24 warnings in unchanged Analytics, tenant-invite and PgVector code;
the selected owner builds above remain warning-clean. Those warnings were not suppressed or treated
as new AE-08 behavior. Private receipts are coherence.log, public-docs-final.log,
dogfood-docs-classification.log, product-surface-check.log, agent-skills-snapshot.log and
skills-structure.log under TEMP/koan-kge01.
