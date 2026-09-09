# AE-18: claimed gate on JobState

Status: implemented and tests authored; builds, runs, and receipts pending (the verification slot is lead-owned).

## Intent and expression

A job handler can compare the work item's current business routing with the gate under which THIS
execution was claimed. The smallest public expression is `context.State.GateKey`. Nothing else
changes: the same-ID submission path and ordinary settlement remain the scheduling owner, no job is
rerouted, and no lease is renewed by this surface.

## Explore and ownership

`JobState` is a ten-parameter positional record, and `JobOrchestrator.ToState` is its sole
projection, invoked once at context creation after the claim. `JobRecord.GateKey` already carries
the claimed gate: the declared or runtime-resolved key for ordinary jobs, the pool-elected member
for pooled jobs (null while queued), and null when no gate applies. `Backoff` key overrides and
work-item mutations never rewrite the claimed row's key. There is no `JobSnapshot` type and no
reason to introduce one; the existing record is the immutable snapshot.

The owner is the existing record plus its existing single projection. `GateKey` is added as one
non-positional init-only member, populated through an object initializer in `ToState`; the
constructor and Deconstruct shapes are unchanged. The avoided alternative, a second `IJobLedger.Get`
per execution merely to read its own claim, is avoidable scaffolding on the handler's hot path and
is deliberately not added. Nothing is deleted; no application ledger query is needed.

## Preserved boundaries

The member is a claim-time capture. It is not the work item's current mutable routing, not a later
`Backoff` override, and not proof of continuing ownership. Pool semantics are unchanged: a queued
pool row still has a null key. The snapshot is an immutable retained value that may outlive the
claim; it never proves continuing ownership. Record equality now covers the captured gate,
consistent with every other field of the snapshot. No new concept, provider, persistence field,
option, or scheduling behavior; `JobRecord` itself is untouched.

## Verification

Shared behavior coverage runs unchanged on every tier through the existing suite fixtures:
ungated work sees null; a submitted gate stays captured across a persisted routing change of the
work item; a `Backoff` override gates peers while the snapshot keeps the claimed gate; a pooled job
sees the elected member although the queued key was null. `CompatibilitySurfaceSpec` pins the old
constructor call shape, the ten-slot deconstruction, non-destructive mutation, and equality that
includes the gate.

After GLM implementation and Astra High source/test review, the lead ran the focused checks on
2026-09-09. In-memory passes nine cases and the existing local Mongo adapter passes four, with zero
failures, skips or compiler warnings. Private receipts and complete logs are under
`artifacts/agent-work/ae18/state-inmemory.trx` and `state-mongo.trx`. Commands use the explicit project
files, `--no-restore -c Release`, the filters below, and VSTest `--logger trx` output.

- in-memory tier:

```text
dotnet test tests/Suites/Jobs/Koan.Jobs.Tests --filter "FullyQualifiedName~CompatibilitySurfaceSpec|FullyQualifiedName~InMemoryBehaviors.claimed_gate_state_survives_a_persisted_routing_change|FullyQualifiedName~InMemoryBehaviors.backoff_override_gates_peers_while_the_snapshot_keeps_the_claimed_gate|FullyQualifiedName~InMemoryBehaviors.pooled_job_sees_the_elected_member_as_its_claimed_gate|FullyQualifiedName~InMemoryBehaviors.state_gatekey_is_null_for_an_ungated_job"
```

- existing local Mongo adapter tier:

```text
dotnet test tests/Suites/Jobs/Adapter.Mongo/Koan.Jobs.Adapter.Mongo.Tests --filter "FullyQualifiedName~MongoBehaviors.claimed_gate_state_survives_a_persisted_routing_change|FullyQualifiedName~MongoBehaviors.backoff_override_gates_peers_while_the_snapshot_keeps_the_claimed_gate|FullyQualifiedName~MongoBehaviors.pooled_job_sees_the_elected_member_as_its_claimed_gate|FullyQualifiedName~MongoBehaviors.state_gatekey_is_null_for_an_ungated_job"
```

Limits: only the focused in-memory and Mongo cases were executed for this slice. This adds no
scheduling behavior, schema change or lease-ownership proof. Package publication remains pending.
