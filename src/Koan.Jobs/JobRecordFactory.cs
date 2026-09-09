namespace Koan.Jobs;

/// <summary>Builds a fresh <see cref="JobRecord"/> for a (work-item × action) — used by submit and by chain-advance,
/// so both compute lane / coalesce key / gate key / deadline identically.</summary>
internal static class JobRecordFactory
{
    public static JobRecord Create(
        JobTypeBinding binding, ResolvedActionPolicy policy, object workItem,
        string workId, string action, DateTimeOffset now, TimeSpan? after, string? correlationId, string? gateKey,
        IReadOnlyDictionary<string, string>? ambientCarrier = null, JobDataRoute route = default)
    {
        // Scheduling is an initiator concern (the scheduler submits on a cadence), not a job state — every job is
        // visible now (or after an explicit delay). No parking.
        var visibleAt = after is { } d ? now + d : now;
        var rec = new JobRecord
        {
            WorkType = binding.WorkType,
            WorkId = workId,
            WorkSource = route.Source,
            WorkAdapter = route.Adapter,
            WorkPartition = route.Partition,
            Action = action,
            Status = JobStatus.Queued,
            VisibleAt = visibleAt,
            FirstSubmittedAt = now,
            Lane = policy.Lane,
            // The stored coalesce key folds in the captured context, so two tenants' idempotent submits never
            // collide. Matches the coordinator's dedup lookup (same fold), keeping dedup context-scoped.
            CoalesceKey = JobCoalesce.FoldAmbient(binding.CoalesceKey(workItem, action), ambientCarrier, route),
            PoolKey = binding.PoolName,           // pool name stamped at submit; GateKey resolved at claim (JOBS-0007)
            GateKey = binding.PoolName is not null ? null : gateKey,  // pool jobs: gate unset until claim-time election
            Exclusive = !binding.ParallelSafe,   // per-entity serialization unless the type opts out
            Deadline = now + policy.Deadline,
            CorrelationId = correlationId,
            // Defensive copy: at chain-advance the parent's bag is passed in — the successor must not alias it.
            AmbientCarrier = ambientCarrier is null ? null : new Dictionary<string, string>(ambientCarrier),
        };
        rec.Transitions.Add(new JobTransition { At = now, From = JobStatus.Created, To = JobStatus.Queued, Note = "submitted" });
        return rec;
    }
}
