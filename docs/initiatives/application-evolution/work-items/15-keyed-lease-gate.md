# AE-15: keyed lease lifetime

Status: focused implementation verified; independent review and publication pending.

## Intent and expression

Only one action for a given key may execute in this process, including when the final caller
finishes as another arrives. Independent keys remain concurrent. Existing callers keep
`await gate.RunAsync(key, timeout, action, ct)` and the existing singleton registration. There are
no new public concepts, references, options or setup steps. Every admitted caller runs its own
action; this is not result sharing or a distributed lease.

## Explore and ownership

The Core package owns this primitive. Its existing `KeyedLeaseGate` separates map lookup from
reference registration, and zero-count retirement from key removal. A new caller can retain a
retired gate while another caller creates a replacement for the same key. Delayed key-only cleanup
can also remove an active or replacement gate. Conditional removal alone cannot close both gaps.

Keep the gate, semaphore, reference count, interface and registration. Replace the concurrent map
and separate atomic count operations with one ordinal dictionary and one short lock. Register a
caller during lookup under that lock; retire its reference and remove the final entry under the
same lock. A gate remains mapped while any waiting or executing caller owns a reference. All waits,
actions and semaphore releases remain outside the lock. This is a correction to the existing Core
owner, without an additional registry, queue or scheduling hook.

The current production consumer is CacheClient's `ExecuteGetOrAddAsync`: it binds the physical key,
enters the gate, rechecks cache, runs the factory and publishes. Its cache recheck supplies result
coalescence. Current Storage source has no consumer of this primitive. The separate
`Core.Infrastructure.Singleflight` shares results and retains its distinct contract.

The root README and architectural principles place generic process coordination in Core; the Core
README/TECHNICAL describe its utility ownership. The gate interface, implementation, registration,
four existing tests, CacheClient and CacheServices establish the current lifetime and integration.
Existing timeout policy and types are reused; no constants, options, request/response types or
provider mechanics need to be created. The only new file is this evidence card; the existing Core
gate, test and TECHNICAL files own the implementation, verification and durable guidance.

## Preserved boundaries

Timeout limits semaphore acquisition only. Nonpositive timeouts retain the five-second default.
The action receives the original cancellation token and keeps cooperative cancellation, return
values and exceptions. Cancelled or timed-out waiters retire only their own references; only an
acquired lease releases its semaphore. No action-duration timeout, automatic retry, strict FIFO,
cross-process coordination or cache invalidation/publication fence is added. Retired managed gates
remain collectible without a new semaphore-disposal protocol.

There are no HTTP, Entity, streaming, configuration or provider changes. Application ergonomics
and IntelliSense remain identical. Core TECHNICAL receives the lifetime invariant; no ADR policy
change or new application recipe is required.

## Verification

Retain same-key exclusion, distinct-key concurrency, acquisition timeout and exception release.
Add bounded last-release/reacquire churn, cancelled-waiter exclusion, cancelled-action release and
precancelled key reuse. Churn settles each wave so the reference count can reach zero. It is stress
evidence, not a deterministic reproduction of the internal gap or proof of every schedule. The
correctness argument is the single-lock lifetime invariant, supported by observable failure and
reacquisition tests. No production test hooks or reflection-based shape assertions are added.

On baseline `7ceb16c9fec333cd345cf561be417386e22dfc3c`, the eight focused cases produced seven passes
and one churn failure: four overlapping actions were observed across 2,048 settled waves. After the
lifetime fix, all eight passed. Both runs had zero compiler warnings and no skipped cases. Private
receipts are `koan-ae15/gate-before.trx` and `koan-ae15/gate-after.trx` under the operator TEMP folder,
with matching logs. This is an observed stress reproduction, not a forced deterministic schedule.

The tests establish observable exclusion and reacquisition after failure; they do not inspect map
shape or measure retained allocations. Retirement follows from the same lock/count invariant.
No new Cache integration journey was executed in this slice. Cache invalidation against an
in-flight factory and Gposingway facet freshness remain separate work.
