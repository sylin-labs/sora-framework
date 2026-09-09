---
type: ANALYSIS
domain: framework
title: "Durable work addresses and central scheduling"
audience: [maintainers, ai-agents]
status: archived
last_updated: 2026-09-09
framework_version: v1.0.0
validation:
  status: reviewed
  scope: historical dogfeeding evidence classification; original execution claims retained
---

# Durable work addresses and central scheduling

**Task:** Run locale-originated Gposingway media jobs after restoring production data.
**Application intent:** Submitting an Entity job inside a data partition must execute against that same Entity.
**Public expression:** Existing `EntityContext.Partition/Source/Adapter` and `entity.Job.Submit()`.
**Guarantee/correction:** Persist the work's logical data address, place scheduling records in the host's default
Jobs store, and restore the address before loading and saving work. Default-address legacy records stay compatible.
**Complete intent surface:** No configuration switch or application-level routing workaround.
**Public concepts:** JobRecord exposes its work source, adapter and partition as ordinary nullable metadata.
Entity status/cancel operations use that address; explicit dashboard queries may span addresses.

**Docs read:** Jobs README/TECHNICAL, Data EntityContext and IAmbientExempt contracts, generic context-carrier
and segmentation plans, Jobs tests and Mongo fixture. IAmbientExempt explicitly does not clear physical partitions.
**Code read:** Coordinator, record factory, orchestrator, DataJobLedger, wake/worker records, coalescing and queries.
**Reusing:** Existing Entity routing scope, central Data-backed ledger, transaction coordinator, carrier restoration,
AmbientAxisComposer and real Jobs harness. No repository wrapper or parallel dispatcher.
**Creating new:** One private Jobs work-address helper and one private scheduling-storage scope, plus nullable
record metadata and focused tests in the existing Jobs suite. Mongo tests may use the already running local server.

**Data-owned defect exposed by characterization:** The partitioned transaction commit test lost the central
ledger row because `TrackedOperations` used null routing arguments, which now mean inherit. Each deferred
operation must restore its exact captured source/adapter/partition, including explicit default values, for
both route resolution and execution. The existing `EntityContext.With` supports empty-string clearing.
Repair those six scopes in Data.Core rather than adding a Jobs-specific commit workaround. Data.Core README,
TECHNICAL transaction contract, coordinator and tracked operations were inspected before this change.

The full in-memory Jobs suite also exposed an empty/default partition mismatch. `EntityContext` now
normalizes empty routing selectors to null after applying inheritance, so explicitly clearing an axis
selects the same default on every provider. The former in-memory empty-string store is no longer selected
by a cleared scope. A routing-scope regression and the shared Jobs behaviors characterize this boundary.
**Coalescence:** Jobs owns the persisted address of its work and the placement of its scheduling infrastructure.
Do not register a new global Data context carrier: doing so would also change unrelated Communication ingress trust.
Do not change IAmbientExempt into a partition override, because its contract explicitly forbids moving existing rows.
**Ergonomics:** Applications retain normal Entity submission within their current data context.
**Constraints satisfied:** No production writes, no alternate app stack, no index rename or automatic data migration.
**Risks:** Preserve transaction participation while clearing scheduling routing. Coalescing, exclusivity, source
submission, status/cancel, retries and chains must include or retain the work address. Missing legacy metadata means
the default address. Never carry a live transaction object into persisted job metadata.

The longer local rehearsal created 224 queued MediaPrewarm jobs in each of five `JobRecord#locale` collections.
None completed because the worker polls the default collection. The application is stopped and the local evidence
is retained. The original production archive contains no locale-partitioned job collections, so it can be restored
again for final verification without a production migration.

## Verification

The native Mongo regression failed before the fix because only the default-partition job was visible.
The partitioned SQLite commit regression then exposed deferred route inheritance. The in-memory suite
exposed empty/default route divergence. Each was corrected at its owning boundary.

The complete Data.Core owner suite passes 498 cases. Jobs verification covers the shared ledger contract,
central queue visibility, work loading and saving, coalescing/status/cancellation, independent exclusivity,
source enumeration capture, both chain paths, malformed-address dead lettering, named-source restart,
commit/rollback and existing tenant carriage. Tests reuse the available Mongo server when
`Koan_MONGO__CONNECTION_STRING` is supplied.

A repeated Mongo pass caught a terminal-status timing failure consistent with native TTL expiration:
the test clock was fixed in January 2026, behind the server's wall clock. The harness now starts at
tomorrow's UTC midnight while retaining manually driven elapsed time. Tests drive archival explicitly;
they do not race the real server's TTL monitor or disable its production behavior.

Final affected-owner results: Data.Core 498, Jobs in-memory/unit 123, native Mongo 83, native SQLite 96,
and tenant carriage 16 passed, with zero failures or skips. Release and application rehearsal follow
through public packages; these counts are not whole-framework certification.
