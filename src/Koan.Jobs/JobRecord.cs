using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Core.Model;

namespace Koan.Jobs;

/// <summary>
/// The enqueued unit of work and the single source of truth — one ledger entry = (work-item × action × lifecycle).
/// Persisted as <c>Entity&lt;JobRecord&gt;</c> so the durable tier rides the existing data layer (no per-DB job
/// adapters; durability follows the host default store). The <c>Id</c> is the Job id; <see cref="WorkId"/>
/// points at the work-item entity. JOBS-0005 §3/§7/§9.
/// </summary>
public sealed class JobRecord : Entity<JobRecord>, IAmbientExempt
{
    /// <summary>Stable key of the discovered <see cref="IKoanJob"/> work-item type (its full name).</summary>
    // JOBS-0005 §19.3: (WorkType,Status) serves the dashboard/coalesce; (WorkType,WorkId) the §17.2 exclusivity probe + history.
    [Index(Group = "ix_jobs_wt_status", Order = 0)]
    [Index(Group = "ix_jobs_wt_workid", Order = 0)]
    public string WorkType { get; set; } = "";

    /// <summary>Id of the work-item entity this job acts on.</summary>
    [Index(Group = "ix_jobs_wt_workid", Order = 1)]
    public string WorkId { get; set; } = "";

    /// <summary>Named data source of the work item. Null denotes the host default.</summary>
    public string? WorkSource { get; set; }

    /// <summary>Data adapter override of the work item. Mutually exclusive with WorkSource.</summary>
    public string? WorkAdapter { get; set; }

    /// <summary>Physical partition of the work item. Null denotes the default partition.</summary>
    public string? WorkPartition { get; set; }

    /// <summary>The action/stage being executed (empty for single-action jobs).</summary>
    public string Action { get; set; } = "";

    // JOBS-0005 §19.3: the claim loop's full sort key — Status prefix, then the (VisibleAt, FirstSubmittedAt) order.
    // JOBS-0008: also the (Lane, Status, VisibleAt, FirstSubmittedAt) index serving the per-lane head seek.
    [Index(Group = "ix_jobs_claim", Order = 0)]
    [Index(Group = "ix_jobs_wt_status", Order = 1)]
    [Index(Group = "ix_jobs_lane_claim", Order = 1)]
    public JobStatus Status { get; set; } = JobStatus.Created;

    /// <summary>Failure-and-retry count (distinct from <see cref="Reschedules"/>).</summary>
    public int Attempt { get; set; }

    /// <summary>Cooperative-deferral count (Reschedule != Retry).</summary>
    public int Reschedules { get; set; }

    /// <summary>Ready to claim when <c>VisibleAt &lt;= now</c> (future for delayed/deferred jobs).</summary>
    [Index(Group = "ix_jobs_claim", Order = 1)]
    [Index(Group = "ix_jobs_lane_claim", Order = 2)]
    public DateTimeOffset VisibleAt { get; set; }

    [Index(Group = "ix_jobs_claim", Order = 2)]
    [Index(Group = "ix_jobs_lane_claim", Order = 3)]
    public DateTimeOffset FirstSubmittedAt { get; set; }
    public DateTimeOffset? LastSettledAt { get; set; }

    /// <summary>Absolute time after which this terminal row may be deleted (JOBS-0005 §20.4) — set at settle per outcome
    /// (Completed/Cancelled → +ArchiveAfter, Failed/Dead → +FailedAfter). On a TTL-capable store (Mongo) the
    /// <c>[Index(Ttl)]</c> makes the store expire the row automatically; elsewhere the periodic purge handles it. Null
    /// (non-terminal) is never expired.</summary>
    [Index(Group = "ix_jobs_ttl", Ttl = true)]
    public DateTimeOffset? ExpireAt { get; set; }

    /// <summary>Concurrency lane (defaults to the action name). The leading key of the JOBS-0008 per-lane head index.</summary>
    [Index(Group = "ix_jobs_lane_claim", Order = 0)]
    public string Lane { get; set; } = "";

    // --- lease (claim ownership) ---
    public string? Owner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }

    // --- reservation (dispatch hand-off, PMC-056) ---
    /// <summary>The named roster node this Queued row is reserved for (<c>DispatchMode.Reservation</c> only).
    /// The assignment is dispatch metadata, not lifecycle: the row stays <see cref="JobStatus.Queued"/>, writes no
    /// transition, and raises no event. Claimable by this hand while fresh; open to any hand once lapsed or when the
    /// coordinator releases a dead hand's reservation. Consumed at claim together with Owner.</summary>
    public string? ReservedFor { get; set; }
    public DateTimeOffset? ReservedUntil { get; set; }

    // --- coalesce / idempotency ---
    public string? CoalesceKey { get; set; }

    // --- gate (cooperative backoff / dispatch-time pool) ---
    /// <summary>The resolved resource key for this job: set at submit for <c>[JobGate]</c> types; stamped at
    /// claim time for <c>[JobPool]</c> types (the chosen pool member). Null means no gate constraint.</summary>
    public string? GateKey { get; set; }

    /// <summary>Pool name for dispatch-time pool allocation (<c>[JobPool]</c>). Null for non-pool jobs. When set,
    /// GateKey starts null at submit and is stamped with the chosen member key atomically at claim (JOBS-0007).</summary>
    public string? PoolKey { get; set; }

    /// <summary>When true (default), this job holds its work-item exclusively: no other job for the same
    /// <c>(WorkType, WorkId)</c> may run concurrently. False for <c>[ParallelSafe]</c> types (JOBS-0005 §17.2).</summary>
    public bool Exclusive { get; set; } = true;

    // --- cancellation (durable marker) ---
    public DateTimeOffset? CancelRequestedAt { get; set; }

    // --- diagnostics / observability ---
    public string? LastError { get; set; }
    public string? DeferReason { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public string? CorrelationId { get; set; }

    /// <summary>The Koan context (tenant, access subject, …) captured at submit, keyed by axis — restored before the
    /// handler runs so the job executes in the context it was submitted under across the async hop. Null when no
    /// cross-cutting axis was in scope (the hot-path common case; absent on the row). Carried opaquely: this entity
    /// names no axis. The property name is retained for persisted-wire compatibility. Immutable after capture;
    /// deep-copied by <see cref="Clone"/>.</summary>
    public Dictionary<string, string>? AmbientCarrier { get; set; }

    public double ProgressFraction { get; set; }
    public string? ProgressMessage { get; set; }
    public string? DeadReason { get; set; }

    /// <summary>Append-only audit trail of status transitions.</summary>
    public List<JobTransition> Transitions { get; set; } = new();

    /// <summary>True when the job is in a terminal state.</summary>
    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Dead;

    /// <summary>Deep copy. The in-memory ledger stores/returns clones so it converges with the durable tier
    /// (which round-trips through the store), keeping the single-writer-via-Update contract identical.</summary>
    public JobRecord Clone()
    {
        var c = (JobRecord)MemberwiseClone();
        c.Transitions = Transitions.ConvertAll(t => new JobTransition { At = t.At, From = t.From, To = t.To, Note = t.Note });
        // Deep-copy the carrier bag: the in-memory ledger stores/returns clones, so a shared dictionary instance
        // would alias the captured context across jobs.
        c.AmbientCarrier = AmbientCarrier is null ? null : new Dictionary<string, string>(AmbientCarrier);
        return c;
    }
}
