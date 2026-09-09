namespace Koan.Jobs;

/// <summary>
/// Read-only view of THIS job's ledger entry, handed to the handler via <see cref="JobContext.State"/> so it
/// can make stateful decisions (e.g. "already rescheduled 3× → defer until tomorrow instead of +5m"). Immutable;
/// the mutable saga state is the work-item argument to <c>Execute</c> (JOBS-0005 §4.2).
/// </summary>
public sealed record JobState(
    JobStatus Status,
    string Action,
    int Attempt,
    int Reschedules,
    DateTimeOffset FirstSubmittedAt,
    DateTimeOffset? LastSettledAt,
    string? LastError,
    string? DeferReason,
    DateTimeOffset? Deadline,
    string? CorrelationId)
{
    /// <summary>
    /// The gate key captured from THIS execution's claimed ledger row: the declared (or runtime-resolved) gate
    /// for ordinary jobs, the pool-elected member for pooled jobs, null when the job carries no gate. It is the
    /// claim-time projection, not the work item's current mutable routing, not a later <c>Backoff</c> key
    /// override, and not proof of continuing ownership.
    /// </summary>
    public string? GateKey { get; init; }
}
