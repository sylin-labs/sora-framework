# Koan Jobs Technical Contract

## Composition

`KoanJobsModule` is discovered from the referenced assembly. It calls `AddKoanJobs()`, which registers
one `IJobCoordinator`, worker, scheduler, ledger, wake coordinator, and health contributor per host.

Ledger election is capability-driven:

- only in-memory/JSON data factories: `InMemoryJobLedger`;
- any durable data factory: `RoutingJobLedger`, which routes `JobPersistenceMode.InMemory` work to an
  in-memory ledger and `Auto`/`DataStore` work to `DataJobLedger`;
- custom registrations may replace the default interfaces before the host is built.

`[JobPersistence(DataStore)]` is a required guarantee. If any discovered work type declares it while the host has
no durable Data adapter, ledger composition throws one corrective `InvalidOperationException` naming the affected
types. `Auto` remains the explicit capability-graded default; `InMemory` remains an explicit volatile override.
Built-in ledger, registry, scheduler, selector, and orchestrator implementations are internal host mechanics.

The retained module's `JobsCompositionFacts` projector publishes this decision into the shared composition model:

| Subject | Selection | Reason |
|---|---|---|
| `jobs:ledger` | `in-memory` | `no-durable-data-adapter` |
| `jobs:ledger` | `durable-data` | `durable-data-adapter` |
| `jobs:wake` | elected Communication provider | `ledger-backed-latency-hint` |
| `jobs:dispatch` | `pull-cas` | `default-dispatch-mode` |
| `jobs:dispatch` | `reservation-roster` | `reservation-opt-in` |

These are semantic tiers, not CLR implementation names. They describe the running host without
claiming provider-fleet certification. Cross-node discovery with only a durable Data reference rides
the framework-owned `WakeStamp` sentinel (JOBS-0009): submissions bump it inside the submission
transaction and durable-tier workers probe it every `WakeProbeInterval`; the full claim scan still
runs at most one `PollInterval` apart.

Dispatch-mode composition has one corrective boundary (PMC-056): `JobsOptions.DispatchMode.Reservation`
with `JobsOptions.Mode.Inline` throws at orchestrator construction — an inline host executes on the
caller and owns no fleet roster, so reservation cannot be honored and is never silently degraded to pull.

## Authoring and persistence

`IKoanJob<T>` constrains `T` to `Entity<T>`. The static `Execute` handler receives the mutable work
item and a read-only orchestration snapshot. The coordinator persists the work item before enqueue and
after handler mutation. The ledger stores orchestration state separately as `JobRecord` entities.

The snapshot's `State.GateKey` is the gate key captured with THIS execution's claim: the declared or
runtime-resolved gate for ordinary jobs, the pool-elected member for pooled jobs, and null when the
job carries no gate. It is the claim-time projection, not the work item's current routing and not a
later `Backoff` key override, so a handler compares business routing with the claimed gate without
querying its own ledger entry.

### Work address and scheduling placement

The coordinator captures an immutable `EntityContext` before the first asynchronous boundary, together
with the opaque context carrier. Deferred source enumeration and each accepted work item restore that
snapshot. `JobRecord.WorkSource`, `WorkAdapter` and `WorkPartition` persist only the logical data address;
transactions and cache controls are never serialized. Missing address fields on legacy records mean
the host default. Source and adapter selectors are case-normalized; partition spelling is preserved.

The built-in durable ledger clears source, adapter and partition at its I/O boundary, retaining the
submission transaction coordinator. Work persistence, ledger append and wake stamp therefore retain
the existing provider transaction guarantee. Worker execution restores the work address without a
submission transaction. Successors inherit the original record address. Invalid addresses are rejected
before handler execution through the existing context-failure settlement.

Ledger, gates, wake stamp, roster and metrics reside in the default host data store. `IAmbientExempt`
continues to govern managed ambient fields only; this change does not alter physical routing for other
Entity types. Existing partitioned ledger collections require operator reconciliation before upgrade;
Jobs does not discover or silently move records between stores.

Per-work status/cancel and exclusivity include the full work address. Coalescing folds the address
through `AmbientAxisComposer`, preserving the exact old coalesce key for the default address.
`JobQuery` address fields use null as a wildcard and empty string as the default address, including
legacy null or absent fields. Explicit gates retain their declared shared resource identity.

Use:

- `item.Job.Submit/Status/Cancel` for one work item;
- `items.Submit` or `Entity.QueryStream(...).Submit` for pointwise source acceptance;
- `Entity.Jobs.Trigger/Query/WithStatus/Cancel` for the type-wide control plane;
- `ctx.Progress` for durable progress;
- one of `ContinueWith`, `StopChain`, `Reschedule`, or `Backoff` to alter the normal settle result.

Calling more than one control signal in a handler fails immediately.

Scalar and source submission converge on one coordinator acceptance operation: resolve policy and
coalescing, persist the work Entity, append the ledger record, then emit a bounded wake hint. A source
captures logical context once at the terminal and restores it around deferred enumeration and every
item save. Items are accepted sequentially, preserving source order, multiplicity, one-pass behavior,
and bounded producer memory. Long-running sources wake at bounded intervals; inline mode drains after
each new record.

`JobSubmission` retains counters only. It distinguishes newly submitted records from explicit
idempotency coalesces, reports whether the source ended naturally, and exposes ambient-transaction
enlistment through `PendingCommit`. `JobSubmissionException` and
`JobSubmissionCanceledException` carry that same confirmed prefix. Submission does not promise
collection atomicity, retain per-item handles, or count a provider call that throws as confirmed—even
though a provider-specific side effect at that failing boundary can be intrinsically unknowable.

## Delivery and recovery

The ledger is the queue. A provider-declared Data conditional replace is the durable atomic claim primitive;
an adapter without it retains the documented optimistic at-least-once fallback. There is no user-selected claim
algorithm, clock-skew election window, or claim-ticket store.

`JobWakeCoordinator` emits one internal, bounded Communication signal after a non-transactional
submit. The process-local provider is automatic; directly referencing a Communication connector
transparently changes its reach. Independently of signals, durable submissions bump the single-row
`WakeStamp` inside the same transaction and durable-tier workers probe it at `WakeProbeInterval`
(JOBS-0009), so peers on any store discover work well under `PollInterval`. A dropped or duplicated
hint costs at most one poll pass. Claims, leases, retries, and reclaim behavior remain ledger-owned.

A claimed job renews its lease on a derived cadence (`LeaseDuration`/3, 100 ms floor) through a
guarded `TryRenewLease` that only extends rows it still owns (JOBS-0009). Failing renewal means
another claimant won: execution cancels and settles nothing, so no write can clobber the new owner.
The renewal loop swallows its own cancellation; transient store errors retry next tick with the
reaper as the bound.

### Dispatch modality (PMC-056)

Pull/CAS is the only default. `DispatchMode.Reservation` layers an active coordinator over the same
ledger, strictly opt-in:

- Assignment state is ledger-verifiable dispatch metadata on the row itself — `JobRecord.ReservedFor`
  / `ReservedUntil` while Queued. No side queues, no routing tables, no membership primitive of its
  own; assignment writes no transition and raises no lifecycle event.
- Stamp: a guarded narrow write (`IJobLedger.TryReserve`) applying only while the row is Queued and
  unreserved — capability-graded exactly like `TrySettle`. Claim eligibility converges across tiers:
  unreserved / reserved-for-me / lapsed; stamping at claim consumes the cookie.
- Coordinator duty runs on the senior live roster member (oldest alive `StartedAt`, id tie-break) as a
  derived fact of the shared roster — no election protocol exists to split or fail. It self-throttles
  to `WorkerHeartbeatInterval`, releases reservations whose hand is confirmed dead or whose stamp
  lapsed (re-verifying the stored row immediately before each write), then assigns oldest-due work to
  the least-loaded live hand bounded by hands × `WorkerConcurrency` and one scan batch per pass.
- Coordinator loss self-heals: seniority migrates at the death timeout, lapsed stamps re-open rows,
  and pull behavior returns automatically for never-reserved work. In Pull mode the entire machinery
  is inert.

Jobs does not own a transport provider or application-visible message contract. Connector election,
health, wire encoding, and local/network delivery belong exclusively to Communication. The wake
stamp is an ordinary Jobs-owned Entity — carriage needs no adapter surface. Hints carry no job or
ambient business context; the claimed ledger record remains the durable, context-bearing truth.

## Logical-flow context

- `JobsContextPlan` wraps Core's memoized `SegmentationContextPlan`. `JobCoordinator` binds every hard
  obligation and captures exactly once before persistence or the first await. Source items share that
  submission snapshot, and coalescing folds the opaque bag so work from distinct context axes cannot
  collapse together accidentally. Missing required context rejects before work or ledger persistence.
- `JobOrchestrator` restores with `ContextIngressTrust.HostTrusted`, requires every applicable axis,
  and re-binds segmentation before loading the work item or
  invoking its handler. This states that the durable ledger is inside the application's administrative
  trust boundary; it does not claim that opaque syntax is tamper detection.
- A missing value suppresses that registered axis rather than inheriting the worker flow. Unknown axes,
  malformed values, unsupported versions, or insufficient trust settle as
  `DeadReason.CarrierRestoreFailed` before application code.
- Jobs owns capture timing and durable settlement. Each module-owned `IKoanContextCarrier` owns the
  meaning and versioned encoding of its axis; Jobs never names tenant, subject, or another axis.
- The Jobs realization receipt covers submit, coalesce identity, load, execute, settle, retry, and
  chain propagation. The ledger stays host-scoped/shared, Data remains responsible for work-item state
  isolation, and the context-free wake signal never becomes a tenant-routing authority.

The contract is at-least-once. A process may stop after an external effect but before settlement, so
handlers must make external effects idempotent or use a business-specific deduplication/outbox boundary.
Koan does not imply cross-provider transactions.

## Inspection

- startup provenance reports the number of discovered job types;
- runtime facts report ledger selection, the dispatch regime, the wake provider, Communication's
  framework-signal election, the Jobs segmentation realization, and a guarantee statement that names
  host-trusted restoration, shared control-plane ledger, Data-owned state isolation, at-least-once
  execution, and context-free wake;
- `/health/ready` reports bounded aggregate queue facts in Development and aggregate status in production;
- `JobRecord` queries provide per-work-item transitions, progress, and failure text;
- optional `JobMetrics.Summary(...)` reads an internal node-sharded rollup that preserves aggregate
  outcomes beyond ledger retention without exposing the framework persistence row as an application Entity.

Health inspection is intentionally bounded and does not scan every lane. `QueueAgeWarning` opts into a
degraded signal; the underlying age and depth facts are always returned.

If the ledger becomes unavailable, readiness becomes unhealthy. The worker logs the first failed
iteration at Error, paces retries at `PollInterval`, keeps repeated failures at Debug, and reports one
Information transition when it recovers. Health is the persistent operator signal; repeated Error lines
are not.

## Unsupported claims

Current focused evidence covers the core/in-process suite and SQLite-backed durable behavior. It does
not certify every database adapter, clock-skew envelope, multi-region topology, every Communication connector,
upgrade path, or exactly-once external effect. See the V1 capability ledger before making broader
support claims.

Streaming bounds application memory, not ledger size or lifecycle cost. Very large or unbounded
sources should model a cursor/window/conveyor as the job rather than minting one job per row.
