---
type: GUIDE
domain: framework
title: "Public PATCH consumer exercise"
audience: [maintainers, ai-agents]
status: current
last_updated: 2026-09-09
framework_version: v1.0.0
validation:
  status: verified
  scope: bounded internal unfamiliar-worker attempt; 192 verifier checks and independent source review
---

# Public PATCH consumer exercise

This bounded attempt complements AE-16 owner and application evidence. It is not AE-02's planned
search, Jobs or shared-policy experiment, and it does not establish independent adoption or a
productivity gain. Starting Koan revision is `4890ca83c43bc041e67d96b37ee902f8cc75d7dd`.

## Contract recorded before the attempt

Business sentence: an expense clerk can correct a pending request without disturbing employee or
receipt details; approving the expense freezes its common business fields across partial updates.

Use existing ExpenseDesk, its shared Foundation and `SharedApprovals/verify.ps1`. The shared policy
already permits pending edits and rejects changes to approved subject, amount or state with
`approval.already-approved`. The existing verifier proves this through replacement bodies; the new
journey must use actual partial HTTP updates. No feature, controller, policy or registration is added.

The unfamiliar GLM worker receives the application-engineering guide, current package documentation,
SharedApprovals README/AGENTS/Foundation guidance, this contract and the public release plan. It does
not receive the implementation conversation or a copy of the framework PATCH implementation. The
lead may clarify real blockers; all interventions and missing measurements belong in the receipt.

Allowed edits:

- Foundation's three existing Koan references, to published App 1.0.53, SQLite 1.0.61 and MCP 1.0.65;
- pending and approved ExpenseDesk PATCH checks in the existing verifier's application journey;
- the two sample README files where package-version guidance becomes stale.

Keep both apps, business policy, routes, modules, UI and SQLite identity unchanged. Preserve existing
package/project switching and computed Foundation versions. No local Koan package, source reference,
new sample, runner or framework workaround is allowed.

The journey creates a pending expense below the approval limit, changes only subject and amount,
and verifies every omitted business field and identity survive. It then approves the row, attempts
an approved common-field PATCH, expects HTTP 409 with the existing reason and useful explanation,
and proves a fresh GET retains every business field. Retain package provenance and HTTP receipts.

After focused development, run the existing full verifier once for acceptance as required by the
sample's operating contract. Its existing local Foundation package experiment and fresh public Koan
restore remain the verification owner. No package or application is published by this attempt.

## Result

GLM 5.3 Flash was dispatched through OpenCode session `ses_f76fa4de2ffe4G6M2svrzUPmrn` with the
contract above. The first verifier attempt exposed a worker test mistake: its negative PATCH reused
the stored values, so the policy correctly allowed an unchanged write. The worker corrected the
attempt to change frozen fields. A subsequent attempt exposed strict PowerShell access to an omitted
null JSON property; the worker adopted the verifier's existing dictionary-indexer pattern.

The lead required string enum assertions without numeric alternatives and explicit preservation of
identity and unset reimbursement in both journeys. The earlier successful 189-check run was retained
as intermediate evidence, then the final verifier passed all 192 checks, including 24 PATCH checks,
with no failure. The receipt is
`artifacts/application-evolution/6517c8e37e6b4f1386d29cb8247d1b38/receipt.json`.

The lead inspected the receipt and Astra High independently accepted the source and evidence. All
16 Koan packages have NuGet.org metadata; Core 1.0.38's hash and the four changed source-file hashes
match the receipt. Both consumers run through the existing baseline, policy upgrade and rollback
experiment. Rejected PATCH attempts actually change approved fields, receive 409 with the business
reason, and leave identity and all six business fields unchanged. New enum checks require string
names. Reimbursement assertions establish an unset value, not exact JSON-null wire representation.

This is an internal guided attempt with zero independent participants. No setup reduction,
independent adoption, productivity result or AE-02 acceptance is claimed. There is no new application,
runner, framework implementation, policy change or source-reference workaround.
