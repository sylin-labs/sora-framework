---
type: GUIDE
domain: framework
title: "Application evolution handoff"
audience: [maintainers, ai-agents]
status: current
last_updated: 2026-09-09
framework_version: v1.0.0
validation:
  status: reviewed
  scope: restart instructions for the approved initiative
---

# Resume application evolution

Read the [charter](README.md) and [live ledger](PROGRESS.md), then
[published-package evidence](evidence/AE-01a.md). The ledger is authoritative if this handoff ages.

## Current corrective slice

Resume [AE-10 conditional field access](work-items/10-field-access.md) on the existing working
tree. Coordinate Web/MCP source and build ownership before testing. 132 focused checks and independent
source review pass locally; repository coherence and normal publication remain; the card holds exact receipts and compatibility boundaries. AE-07/08/09 were
published in release 34390309739. Brownfield public-package validation remains a separate receipt.
Do not replace this work with a new ledger or start AE-02 while the corrective slice is unsettled.

## After the current corrective slice

1. Check the working tree and owners, then claim [AE-02](work-items/02-application-evolution.md).
   AE-01 and AE-01a are complete; do not rebuild their applications elsewhere or rerun discovery.
2. Use `samples/applications/SharedApprovals/ApprovalDesk` and `ExpenseDesk`, with their shared
   `Foundation`. Read the workspace README and AGENTS.md. A03 owns the first app and recording;
   its published Koan dependency prerequisite is now proved, while the remaining scenes and recording
   retain A03's existing criteria.
3. Use the normal `dotnet run` commands and `verify.ps1` directly. The foundation references
   published App 1.0.23, SQLite connector 1.0.30, and MCP 1.0.29, bringing Core 1.0.34 transitively.
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
