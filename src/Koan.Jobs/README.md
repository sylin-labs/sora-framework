# Sylin.Koan.Jobs

Reference `Sylin.Koan.Jobs` when an Entity-owned business transition should run outside the request
that requested it, survive retries, and remain inspectable.

## Install

```powershell
dotnet add package Sylin.Koan.Jobs
```

The package composes through the application's existing `AddKoan()` call. No Jobs-specific registration is
required. With no durable Data provider, the automatic ledger is deliberately in-memory; reference SQLite,
PostgreSQL, SQL Server, MongoDB, or another durable Koan Data provider when work must survive restart.

## Usage: run Entity-owned work

```csharp
public sealed class ReviewRequest : Entity<ReviewRequest>, IKoanJob<ReviewRequest>
{
    public ReviewPriority Priority { get; set; }

    public static Task Execute(ReviewRequest request, JobContext context, CancellationToken ct)
    {
        request.Assess();
        return context.Progress(1, "Ready for review");
    }
}

await review.Job.Submit();
var status = await review.Job.Status();
```

Inside the handler, `context.State` is the read-only orchestration snapshot of THIS execution. Its
`GateKey` is the gate under which this execution was claimed: the declared (or runtime-resolved)
gate for a `[JobGate]` type, the elected pool member for a `[JobPool]` type, and null when the job
carries no gate. It does not track the work item's routing after the claim, and it is not a later
`Backoff` key override:

```csharp
var claimedGate = context.State.GateKey;   // captured with THIS claim, not current routing
```

The same pointwise intent applies to a business selection or a provider-bounded Entity stream:

```csharp
JobSubmission selected = await reviews
    .Where(review => review.Priority == ReviewPriority.High)
    .Submit();

JobSubmission streamed = await ReviewRequest
    .QueryStream(review => review.Priority == ReviewPriority.High)
    .Submit();
```

`JobSubmission` is a fixed-size summary of ledger acceptance, not handler completion. `Accepted`
counts new ledger records plus declared-idempotency coalesces; `PendingCommit` says those records are
still contingent on the ambient transaction. A source or submission failure throws
`JobSubmissionException`, and cancellation throws `JobSubmissionCanceledException`; both preserve the
confirmed accepted prefix without retaining per-item handles.

The package registers its coordinator, worker, ledger, health contributor, and Communication-backed
wake hint through `AddKoan()` discovery. Application registration code is not required.

Submission also captures every composed `IKoanContextCarrier` before its first asynchronous boundary.
Execution restores that opaque context before loading the work item; an absent registered axis is
explicitly suppressed. Unknown axes or invalid carrier data dead-letter the job before application
handler code. Tenant and subject values therefore survive a durable hop without Jobs naming either
concept or requiring application plumbing.

Submission also preserves the work item's `EntityContext` source, adapter and partition. The worker
restores this address before loading, executing and saving work, including retries and chained stages.
Scheduling records, gates, wake stamps, worker roster and metrics use the host's default Jobs store.
Submitting in a locale partition therefore cannot hide a job from the central worker. Work-specific
status, cancellation, coalescing and exclusivity distinguish different data addresses. Type-wide
dashboard queries span addresses unless their `JobQuery` includes address filters.

## Capability ladder

| Composed infrastructure | Elected behavior |
|---|---|
| No durable data adapter | In-memory ledger; work is lost on restart |
| SQLite, Postgres, SQL Server, Mongo, or another durable adapter | Data-backed ledger; job state survives restart |
| Shared durable store across nodes | Competing consumers share the ledger; a one-row wake stamp lets peers discover submissions at short-probe cadence (JOBS-0009) |
| `JobsOptions.DispatchMode = JobDispatchMode.Reservation` (opt-in) | The senior live roster node actively hands due work to named fleet members ("a cookie to a named hand"); a fresh reservation binds its hand and every claim stays CAS-fenced. Default remains pull/CAS competition (PMC-056) |
| Direct Communication connector that claims framework signals, such as RabbitMQ | Wake becomes instant; the ledger remains the source of truth |

Handlers renew their lease while running (JOBS-0009); if another claimant takes a row mid-run, the
loser abandons without settling instead of racing the new owner. Inspect `jobs:ledger`, `jobs:wake`,
and `communication:framework-signals:default` through
`/.well-known/Koan/facts` or `koan://facts`.
The standard `/health/ready` response includes queue depth, running depth, reclaim backlog, and oldest
queued age in Development; production returns only aggregate readiness. Per-work-item status and history
are available through `entity.Job` and `Entity.Jobs`.

Optional retained throughput is one operation surface rather than a framework Entity. Enable
`JobsOptions.MetricsEnabled`, then read outcome totals that survive ledger retention:

```csharp
var outcomes = await JobMetrics.Summary(
    typeof(ReviewRequest).FullName!,
    DateTimeOffset.UtcNow.AddDays(-1),
    DateTimeOffset.UtcNow);
```

### Reservation dispatch (opt-in)

Pull/CAS competition is the default and needs nothing from the fleet roster beyond liveness. When a host wants
work handed to specific members instead — placement-aware assignment, no hot-row contention, each node draining
its own cookies — set the mode once:

```csharp
builder.Services.AddKoanJobs(o =>
{
    o.Mode = JobMode.Normal;
    o.DispatchMode = JobDispatchMode.Reservation;
});
```

The senior live roster member coordinates: it releases reservations of confirmed-dead hands and lapsed stamps,
then assigns the oldest due work to the least-loaded live hand. The assignment lives on the job row itself
(`JobRecord.ReservedFor`/`ReservedUntil`) — there is no side queue, no routing table, and no election protocol;
if the coordinator dies, the next-senior takes over at the death timeout, and a concurrent stamp is resolved by
the same conditional write that claims use. Reservation mode requires `Mode.Normal`: an inline host has no fleet,
and composition refuses the combination.

Inspect `jobs:dispatch` alongside `jobs:ledger` through `/.well-known/Koan/facts` or `koan://facts`.

## Boundaries

- Execution is at-least-once; handlers must be idempotent.
- The in-memory tier is development/test convenience, not durability.
- `[JobPersistence(JobPersistenceMode.DataStore)]` fails host composition when no durable Data adapter is available;
  Koan never silently weakens that declared requirement to in-memory execution.
- Durable SQLite behavior does not prove every distributed provider or topology.
- Source submission is pointwise, sequential, and not collection-atomic. A provider call that throws
  is not reported as confirmed acceptance; retry that item under the job's declared idempotency policy.
- A custom MCP or HTTP action that submits work remains responsible for its business authorization.
- Metrics are derived, opt-in, and lossy-tolerant; the Jobs ledger remains the source of truth.
- Use a window or batch as the job for large sources; do not create an unbounded job per input row.

Use the [Jobs reference](../../docs/reference/jobs/index.md) for the greenfield API map and the
[Jobs guide](../../docs/guides/jobs-howto.md) for scheduling, retries, chains, gates, and testing.
For deterministic tests against the production engine, reference
[`Sylin.Koan.Jobs.Testing`](../Koan.Jobs.Testing/README.md).
