---
type: PLAN
domain: framework
title: "Application evolution progress"
audience: [maintainers, ai-agents]
status: current
last_updated: 2026-09-12
framework_version: v1.0.0
validation:
  status: reviewed
  scope: published foundation and corrective capabilities with recorded consumer acceptance; independent adoption unmeasured
---

# Application evolution progress

This file alone owns execution status. The [charter](README.md) owns scope and dependencies;
[NOW](NOW.md) owns restart instructions. Update this ledger when claiming or completing work,
with links to the actual deliverable and evidence.

## Initiative state

- Overall: corrective increment complete; shared-foundation milestone proved with published Koan
  dependencies. The independent application-evolution research remains planned.
- Published increment: AE-12 insertion, AE-13 enum names and AE-14 additive collection construction reached NuGet.org through release 34411628984 at `7ceb16c9f`. Gposingway verified all 33 app and 35 test Koan dependencies against that public plan; its four enum/body-binding failures now pass.
- Latest published increment: AE-15 keyed lease lifetime, AE-16 typed PATCH restoration, AE-17 ordered cache removal and AE-18 claimed gate state. All passed independent review and owner tests. Six dependency stamp commits settled at `4890ca83c`; all eight coherence legs passed, with the same 21 existing build warning lines and none added. Release 34421172932 published 99 new packages. Gposingway restored the exact plan, verified all 33 app and 35 test package origins at NuGet.org, and passed all six HTTP write-policy cases, including the three prior PATCH failures. Its routing and facet freshness acceptance is recorded in the consumer completion below.
- AE-19 corrected deferred-transaction truth, removed implicit count work from list-returning queries,
  and restored public batch capability transparency. Release 34716171377 published 82 packages from
  `e9b3f2e71`; Data Core 1.0.69 and representative Cache/PostgreSQL/SQLite packages returned HTTP 200
  from NuGet.org. Consumer adoption and native cross-Entity transactions remain separate gates.
- Consumer completion: Gposingway completed its final 1,228 backend and 24 frontend checks, copied-data
  compatibility and existing-container journeys, then deployed the public-package candidate. Its
  [deployment receipt](https://github.com/gposingway/gposingway-org/blob/dev/docs/deployments/2026-09-10-meaningful-capabilities.md)
  records bounded live proof. Routing, facet freshness, field disclosure, insertion and conditional
  publication acceptance are complete within those recorded contracts. Earlier work-item receipts
  remain dated evidence rather than current publication status.
- Independent participants: none recruited; results and productivity measurements unavailable.

## Work ledger

| Card | Status | Deliverable / evidence / next condition |
|---|---|---|
| [AE-01 Shared foundation](work-items/01-shared-foundation.md) | done | [Applications](../../../samples/applications/SharedApprovals/README.md), [original 163-check receipt and limits](evidence/AE-01.md); published dependency follow-up in AE-01a |
| [AE-01a Published consumption](work-items/01a-published-foundation.md) | done | [Release and 168-check fresh-cache receipt](evidence/AE-01a.md); both apps use published Koan dependencies without local preparation |
| [AE-02 Application evolution](work-items/02-application-evolution.md) | planned | Requires AE-01's reproducible consumer baseline |
| [AE-03 Governed agent workflow](work-items/03-governed-agent-workflow.md) | planned | Requires AE-01's shared policy and application boundary |
| [AE-04 Change review](work-items/04-change-review.md) | planned | Requires changes and behavior evidence from AE-02 and AE-03 |
| [AE-05 Incremental adoption](work-items/05-incremental-adoption.md) | planned | Requires AE-01's bounded feature/foundation |
| [AE-06 Independent validation](work-items/06-independent-validation.md) | planned | Requires internal findings, a recorded pilot decision, and consenting participants |
| [AE-07 Scoped insert safety](work-items/07-scoped-insert-safety.md) | done | Atomic insert boundary and native provider proofs accepted locally; constrained bulk creates reject pending native mixed-write guarantees. Published-package consumer insertion and suppression checks pass; broader authorization limits remain explicit. |
| [AE-08 Counterpart query authority](work-items/08-counterpart-query-authority.md) | done | 220 focused checks, independent source acceptance and repository coherence pass. Published through release 34390309739; published-package filter and counterpart journeys pass. |
| [AE-09 Build dependency security](work-items/09-build-dependency-security.md) | done | SourceLink pin selects patched Git build tasks; forced audit restore and focused build pass with zero warnings/errors. Included in the subsequent public releases; the final public-package application build and certification pass with recorded pre-existing warnings. |
| [AE-10 Conditional field access](work-items/10-field-access.md) | done | One property Access owner across typed MVC/shared Entity/MCP; 132 focused checks and independent source acceptance. Published through 34400186238; discovery correction published through 34406712072. Public-package application access and publication journeys pass within the recorded limits. |
| [AE-11 Conditional publication](work-items/11-conditional-publication.md) | done | Normalized Filter primitive and sole Entity ReplaceIf verb; 86 focused tests, independent review and coherence pass. Release 34406712072 published 87 packages; public-package guarded publication and cooperative restart acceptance pass within the recorded consumer limits. |
| [AE-12 Entity insertion](work-items/12-entity-insertion.md) | done | 36 focused cases, review and coherence pass. Published through 34411628984; public Gposingway translation consumes Insert, with completed conditional-publication and cooperative restart checks recorded by that consumer. |
| [AE-13 MCP enum names](work-items/13-mcp-enum-wire.md) | done | One enum output convention; legacy numeric input retained. 58 focused MCP checks, review, release 34411628984 and public consumer enum regression pass. |
| [AE-14 Additive JSON collections](work-items/14-additive-json-collections.md) | done | Shared Core.Json construction; 68 Web and six Data checks, including custom reader preservation. Review, release 34411628984 and public consumer admin body regressions pass. |
| [AE-15 Keyed lease lifetime](work-items/15-keyed-lease-gate.md) | done | One short accounting lock replaces racy detached gate retirement. Eight tests pass after reproduced overlap; independent review, release 34421172932 and public package adoption pass. |
| [AE-16 Typed PATCH](work-items/16-typed-patch-restoration.md) | done | Typed copy restoration, recursive writable admission, explicit null defaults and exact dictionary keys. 139 Web, 32 Data, 14 PatchOps, 95 in-memory, 59 JSON and five Canon controls pass. Independent review, release 34421172932 and all three formerly failing public GW PATCH cases pass. The [internal public consumer exercise](evidence/AE-16-consumer.md) adds 192 verifier checks, including 24 PATCH checks, with reviewed provenance. |
| [AE-17 Ordered cache removal](work-items/17-ordered-cache-removal.md) | done | Existing gate orders removal with fills and backfills; all started tier writes settle before failure escapes. Seven ordering and all 82 owner cases pass, with independent review. Published in release 34421172932 and restored from NuGet.org; all fifteen application facet-cache controls and its final acceptance pass. |
| [AE-18 Claimed job gate](work-items/18-job-claimed-gate-state.md) | done | Optional immutable JobState.GateKey exposes existing claim without ledger reads. Nine in-memory and four Mongo cases pass; independent review, release 34421172932 and public package adoption pass. Gposingway routing and cooperative host-replacement acceptance pass. |
| [AE-19 Data transaction and query truth](work-items/19-data-transaction-and-query-truth.md) | done | Deferred coordination now narrates and proves its durable-prefix boundary; list queries preserve null count intent; public batches expose only jointly qualified execution capabilities. All 561 Data Core owner checks and two focused SQLite checks pass. Release 34716171377 published 82 packages; representative NuGet probes pass. Native cross-Entity atomicity remains a separate provider-family design gate. |

Use `in-progress`, `blocked`, `done`, or `stopped` as execution proceeds. A blocker names the
missing input and useful restart point; stopped work retains its findings. Front matter in
the charter and cards describes document status, not successful implementation.

## Decisions and history

### 2026-09-12 — Data execution truth published

- Commit `f75fb79a5` aligned ambient transaction narration with its durable-prefix receipt, preserved
  count-free intent on list queries, and forwarded jointly qualified batch execution capabilities.
- Four generated dependency-floor commits reached quiescence (`0 stamped, 107 unchanged`) at
  `e9b3f2e71`; `main` fast-forwarded from `dev` without a merge commit.
- [Release 34716171377](https://github.com/sylin-org/koan-framework/actions/runs/34716171377)
  passed planning, packing, package-only application proof, and isolated publication for 82 packages.
- Direct NuGet probes returned HTTP 200 for Data Core 1.0.69, Cache 1.0.59, PostgreSQL connector
  1.0.62, and SQLite connector 1.0.69. Tangent's consumer pin remains unchanged.

### 2026-09-05 — Published dependency path proved; local preparation removed

- [PR #139](https://github.com/sylin-org/koan-framework/pull/139) passed the required gate and
  was promoted through dev to main. [Release 33979924764](https://github.com/sylin-org/koan-framework/actions/runs/33979924764)
  certified and published 99 packages, including Core 1.0.34 and its updated dependents.
- The foundation now references App 1.0.23, SQLite connector 1.0.30, and MCP 1.0.29. Core arrives
  transitively. The preparation script, local build import, gate, and direct Core override are removed.
- [AE-01a evidence](evidence/AE-01a.md) records 168 passing checks with a fresh cache and every
  Koan package restored from nuget.org. Original HTTP/MCP, persistence, update, and rollback
  contracts passed. Normal authoring built with zero warnings and errors; 35 Core tests passed.
- Verified sample revision: `3956b6aca3ebd9a556ee0ac3b89d0520c72db2f2`. Use the current
  published dependency baseline for AE-02/AE-03, retaining the original AE-01 receipt as history.
- A03's local Core prerequisite is cleared. Remaining scenes, recording, and existing launch
  criteria remain with A03. Independent participants and productivity measurements remain absent.

### 2026-09-05 — Shared foundation proved across two real consumers

- ApprovalDesk records purchase orders; ExpenseDesk records reimbursements. Both use one approval
  lifecycle policy and separate SQLite files. Browser submission, approval, and final actions passed.
- A local NuGet fixture advanced the foundation from computed 1.0.1 (USD 1,000) to 1.0.2 (USD 500),
  then restored 1.0.1 on isolated data. Consumer source stayed unchanged. All 163 final checks passed,
  including HTTP rejection, MCP boundary behavior, matching packaged guidance, and preserved records.
- The experiment found and repaired Core's rejection of ordinary organization-owned foundation
  identities. Core repair commit: `6e2aafc56`; 35 focused tests passed. The run used local Core 1.0.34
  plus published App, SQLite, and MCP packages; it is not a nuget.org-only or launch-readiness receipt.
- [Evidence](evidence/AE-01.md) records source hashes, full package graphs, failed attempts, adoption
  work, and missing measurements. No independent participants or comparative productivity results.
- Committed application baseline: `157f9053ec10cce86154c433be119f9a2d624e0e`. Future tasks start
  from this revision and declare their own preserved contracts before edits.
- Final documentation checks: zero errors; the public documentation truth gate passed. The focused
  lint reported four existing announcement-handoff metadata warnings and five missing-front-matter
  warnings for ordinary sample/package README files. No full release certification was claimed.

### 2026-09-05 — Internal implementation authorized and AE-01 claimed

- Maintainer authorized proceeding with the internally executable work. Start with AE-01's
  shared-policy update across two distinct applications.
- Canonical working source: `samples/applications/SharedApprovals/ApprovalDesk/`; second
  consumer: `samples/applications/SharedApprovals/ExpenseDesk/`; shared package source:
  `samples/applications/SharedApprovals/Foundation/`. These paths now contain the proved implementation.
- FirstUse remains the small first-use contract; its grammar is reused without expanding it.
- The first policy experiment tightens the amount eligible for approval from USD 1,000 to
  USD 500. Purchase ordering and expense reimbursement remain consumer-owned extensions.

### 2026-09-05 — Approved initiative recorded

- Maintainer approved the proposal to capture six connected opportunities, their dependencies,
  acceptance evidence, and links to existing owners before implementation.
- Approval desk and expense requests are the working domains. A03 remains the flagship
  application/recording owner; AE-01 owns the shared foundation and second consumer.
- The initiative index and durable memory route here. Announcement guidance links the shared
  work; its existing publication and benchmark boundaries remain in force.
- No application, capability, productivity, or independent-adoption result is asserted by setup.
- Setup validation: focused documentation lint passed with zero errors and no warnings in the
  new initiative; four existing announcement-handoff metadata warnings remain. The public
  documentation truth gate passed. No runtime code changed.
