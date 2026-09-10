---
type: GUIDE
domain: framework
title: "Application evolution handoff"
audience: [maintainers, ai-agents]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  status: reviewed
  scope: restart instructions for the approved initiative
---

# Resume application evolution

Read the [charter](README.md) and [live ledger](PROGRESS.md), then
[published-package evidence](evidence/AE-01a.md). The ledger is authoritative if this handoff ages.

## Completed corrective increment

AE-12/13/14 are published through release 34411628984 at `7ceb16c9f` and consumed by Gposingway
from NuGet.org. The four enum/body-binding regressions pass. The subsequent owner increment covers
[AE-15 keyed gate lifetime](work-items/15-keyed-lease-gate.md),
[AE-16 typed PATCH](work-items/16-typed-patch-restoration.md),
[AE-17 ordered cache removal](work-items/17-ordered-cache-removal.md), and
[AE-18 claimed job gate](work-items/18-job-claimed-gate-state.md).
All four corrections passed independent review and their owner tests. Dependency stamping settled at
`4890ca83c`; all eight repository coherence legs passed. The full build retained the same 21 warning
lines as the prior increment, with none added. Release 34421172932 succeeded for 99 new packages.
Gposingway restored the exact public plan and verified NuGet.org provenance for all 33 app and 35 test
Koan dependencies. Its six actual HTTP write-policy cases pass, including the three prior PATCH
failures. Gposingway completed its routing and facet-cache acceptance and deployed the candidate;
its [deployment receipt](https://github.com/gposingway/gposingway-org/blob/dev/docs/deployments/2026-09-10-meaningful-capabilities.md)
records the application proof and operational limits. This establishes the recorded consumer scope,
not an unrestricted consistency or adoption guarantee.
The bounded [public PATCH consumer exercise](evidence/AE-16-consumer.md) passes 192 checks, including
24 PATCH checks, with Astra High review. It is internal guided evidence, not AE-02 acceptance.
The corrective consumer work is complete. Use the current public capability documentation for new
applications; these work items retain the history of the guarantees and their proof.
## Separate planned research

The following initiative work is not part of the completed corrective rollout. Select it only when
the owner requests the next research increment.

1. Check the working tree and owners, then claim [AE-02](work-items/02-application-evolution.md).
   AE-01 and AE-01a are complete; do not rebuild their applications elsewhere or rerun discovery.
2. Use `samples/applications/SharedApprovals/ApprovalDesk` and `ExpenseDesk`, with their shared
   `Foundation`. Read the workspace README and AGENTS.md. A03 owns the first app and recording;
   its published Koan dependency prerequisite is now proved, while the remaining scenes and recording
   retain A03's existing criteria.
3. Use the normal `dotnet run` commands and `verify.ps1` directly. The foundation references
   published App 1.0.53, SQLite connector 1.0.61, and MCP 1.0.65, bringing Core 1.0.38 transitively.
   There is no local Core preparation step. Never publish from the workstation.
4. Commit AE-02 task contracts before attempts: semantic search, durable background work, and
   shared-policy evolution, with explicit preserved contracts and measurements. Reuse the existing
   evaluation runner where applicable; the AE-01 fixture is not an independent agent campaign.
5. Follow [the exploration workflow](../../../.codex/skills/explore/SKILL.md) for new production
   edits. AE-03 must add real identity and tenancy boundaries; the current HTTP/MCP proof establishes
   business policy only. Its generated Code Mode SDK needs separate assessment before being advertised.

Reuse evaluation infrastructure where it fits; its existing run protocols and results retain
their provenance. The internal decision point precedes independent pilots. Public claims and
outreach follow the charter's existing boundaries.
