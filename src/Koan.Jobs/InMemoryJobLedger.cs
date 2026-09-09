using Microsoft.Extensions.Options;

namespace Koan.Jobs;

/// <summary>
/// In-memory <see cref="IJobLedger"/> for the Local tier and dockerless tests (JOBS-0005 §8). Stores and returns
/// <em>clones</em> so behavior converges with the durable tier (which round-trips through a store), and serializes
/// all mutations under one lock so <see cref="ClaimNext"/> is an atomic CAS. Non-durable: lost on restart.
/// </summary>
internal sealed class InMemoryJobLedger : IJobLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JobRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobGateRecord> _gates = new(StringComparer.Ordinal);
    // JOBS-0008: per-lane WFQ virtual time. The Local tier is single-process, so this in-process dict IS the whole
    // fairness state — the identical LaneFairSelector runs over it (the durable tier supplies LaneCursor rows instead).
    private readonly Dictionary<string, double> _virtual = new(StringComparer.Ordinal);
    private readonly Func<string, double> _weight;

    public InMemoryJobLedger(IOptions<JobsOptions>? options = null)
    {
        var weights = options?.Value.LaneWeights;
        _weight = weights is null ? _ => 1.0 : lane => weights.GetValueOrDefault(lane, 1.0);
    }

    public Task Append(JobRecord record, CancellationToken ct)
    {
        lock (_gate) _records[record.Id] = record.Clone();
        return Task.CompletedTask;
    }

    public Task AppendMany(IReadOnlyCollection<JobRecord> records, CancellationToken ct)
    {
        lock (_gate)
            foreach (var r in records) _records[r.Id] = r.Clone();
        return Task.CompletedTask;
    }

    public Task<JobRecord?> Get(string jobId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_records.TryGetValue(jobId, out var r) ? r.Clone() : null);
    }

    public Task<JobRecord?> FindActiveByCoalesceKey(string workType, string coalesceKey, CancellationToken ct)
    {
        lock (_gate)
        {
            // Queued-only: a Running job does not block a new submit — the submit queues a trailing execution
            // (at most 1 running + 1 queued per coalesce key, the debounce / trailing-edge pattern).
            var hit = _records.Values.FirstOrDefault(r =>
                r.Status == JobStatus.Queued && r.WorkType == workType && r.CoalesceKey == coalesceKey);
            return Task.FromResult(hit?.Clone());
        }
    }

    public Task<JobRecord?> ClaimNext(string owner, DateTimeOffset now, DateTimeOffset leaseUntil,
        IReadOnlyCollection<string> saturatedLanes, CancellationToken ct,
        IReadOnlyDictionary<string, PoolDispatchContext>? pools = null)
    {
        lock (_gate)
        {
            // Per-entity serialization (§17.2): an exclusive job can't be claimed while another job for the same
            // (WorkType, WorkId) is already running.
            var busy = _records.Values
                .Where(r => r.Status == JobStatus.Running)
                .Select(JobDataRoute.Identity)
                .ToHashSet();

            // Build member slot counts for pool dispatch (JOBS-0007): member key -> running count.
            Dictionary<string, int>? memberSlots = null;
            if (pools is { Count: > 0 })
            {
                memberSlots = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var ctx in pools.Values)
                    foreach (var member in ctx.Members)
                        memberSlots.TryAdd(member, 0);
                foreach (var r in _records.Values)
                    if (r.Status == JobStatus.Running && r.GateKey is not null && memberSlots.ContainsKey(r.GateKey))
                        memberSlots[r.GateKey]++;
            }

            // JOBS-0008: gather each lane's oldest claimable head, then pick the fairest lane (weighted fair queuing
            // over per-lane virtual time). This replaces "take the globally-oldest claimable row", which let a
            // continuously-fed / older upstream lane monopolize dispatch and starve a downstream lane. Within a lane,
            // the head stays the oldest claimable row (gated / pool-exhausted older rows are skipped to the next).
            var heads = new Dictionary<string, JobRecord>(StringComparer.Ordinal);
            foreach (var r in _records.Values
                .Where(r => r.Status == JobStatus.Queued
                            && r.VisibleAt <= now
                            && r.CancelRequestedAt is null
                            && OpenTo(r, owner, now)
                            && !saturatedLanes.Contains(r.Lane)
                            && !(r.Exclusive && busy.Contains(JobDataRoute.Identity(r))))
                .OrderBy(r => r.VisibleAt)
                .ThenBy(r => r.FirstSubmittedAt))
            {
                if (heads.ContainsKey(r.Lane)) continue;   // already hold this lane's oldest claimable head
                if (r.PoolKey is not null)
                {
                    if (pools is null || !pools.TryGetValue(r.PoolKey, out var ctx)) continue;
                    if (!ctx.Members.Any(m => memberSlots!.GetValueOrDefault(m) < ctx.CapacityPerMember)) continue;
                }
                else if (IsGated(r.GateKey, now)) continue;
                heads[r.Lane] = r;
            }

            if (heads.Count == 0) return Task.FromResult<JobRecord?>(null);

            var lane = LaneFairSelector.Pick(heads.Keys, _virtual)!;
            var candidate = heads[lane];

            // Stamp the elected pool member atomically with the claim transition.
            if (candidate.PoolKey is not null)
            {
                var ctx = pools![candidate.PoolKey];
                candidate.GateKey = ctx.Members.First(m => memberSlots!.GetValueOrDefault(m) < ctx.CapacityPerMember);
            }

            candidate.Attempt++;
            Transition(candidate, JobStatus.Running, now, $"claimed by {owner}");
            candidate.Owner = owner;
            candidate.LeaseUntil = leaseUntil;
            // PMC-056: the claim consumes the reservation cookie (if this hand held one).
            candidate.ReservedFor = null;
            candidate.ReservedUntil = null;
            _virtual[lane] = LaneFairSelector.Charged(_virtual.GetValueOrDefault(lane), _weight(lane));
            return Task.FromResult<JobRecord?>(candidate.Clone());
        }
    }

    public Task Update(JobRecord record, CancellationToken ct)
    {
        lock (_gate) _records[record.Id] = record.Clone();
        return Task.CompletedTask;
    }

    public Task Progress(string jobId, double fraction, string? message, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(jobId, out var r))
            {
                r.ProgressFraction = fraction;
                r.ProgressMessage = message;
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryRenewLease(string jobId, string owner, DateTimeOffset leaseUntil, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(jobId, out var r)
                || r.Status != JobStatus.Running
                || !string.Equals(r.Owner, owner, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }
            r.LeaseUntil = leaseUntil;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TrySettle(JobRecord record, string expectedOwner, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(record.Id, out var stored)
                || stored.Status != JobStatus.Running
                || !string.Equals(stored.Owner, expectedOwner, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }
            _records[record.Id] = record.Clone();
            return Task.FromResult(true);
        }
    }

    // --- reservation dispatch (PMC-056) ---

    public Task<bool> TryReserve(string jobId, string hand, DateTimeOffset reservedUntil, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(jobId, out var r)
                || r.Status != JobStatus.Queued
                || r.ReservedFor is not null)
            {
                return Task.FromResult(false);
            }
            r.ReservedFor = hand;
            r.ReservedUntil = reservedUntil;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<JobRecord>> ReservationCandidates(DateTimeOffset now, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            var list = limit <= 0
                ? new List<JobRecord>()
                : _records.Values
                    .Where(r => r.Status == JobStatus.Queued
                                && r.VisibleAt <= now
                                && r.CancelRequestedAt is null
                                && r.ReservedFor is null)
                    .OrderBy(r => r.VisibleAt)
                    .ThenBy(r => r.FirstSubmittedAt)
                    .Take(limit)
                    .Select(r => r.Clone())
                    .ToList();
            return Task.FromResult<IReadOnlyList<JobRecord>>(list);
        }
    }

    public Task<IReadOnlyList<JobRecord>> Reservations(CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _records.Values
                .Where(r => !r.IsTerminal && r.ReservedFor is not null)
                .Select(r => r.Clone()).ToList();
            return Task.FromResult<IReadOnlyList<JobRecord>>(list);
        }
    }

    public Task<IReadOnlyList<JobRecord>> Stuck(DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _records.Values
                .Where(r => r.Status == JobStatus.Running && r.LeaseUntil is { } l && l < now)
                .Select(r => r.Clone()).ToList();
            return Task.FromResult<IReadOnlyList<JobRecord>>(list);
        }
    }

    public Task<IReadOnlyList<JobRecord>> Running(CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _records.Values
                .Where(r => r.Status == JobStatus.Running)
                .Select(r => r.Clone()).ToList();
            return Task.FromResult<IReadOnlyList<JobRecord>>(list);
        }
    }

    public Task<IReadOnlyList<JobRecord>> NonTerminal(CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _records.Values.Where(r => !r.IsTerminal).Select(r => r.Clone()).ToList();
            return Task.FromResult<IReadOnlyList<JobRecord>>(list);
        }
    }

    public Task<IReadOnlyList<JobRecord>> InStage(string workType, string action, CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _records.Values
                .Where(r => r.WorkType == workType && r.Action == action && r.Status == JobStatus.Queued)
                .Select(r => r.Clone()).ToList();
            return Task.FromResult<IReadOnlyList<JobRecord>>(list);
        }
    }

    public Task<IReadOnlyList<JobRecord>> Query(JobQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _records.Values
                .Where(JobLedgerPredicates.ForQuery(query).Compile())
                .Select(r => r.Clone()).ToList();
            return Task.FromResult<IReadOnlyList<JobRecord>>(list);
        }
    }

    public Task SetGate(string gateKey, DateTimeOffset releaseAt, string? reason, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_gates.TryGetValue(gateKey, out var existing) && existing.ReleaseAt >= releaseAt) return Task.CompletedTask;
            _gates[gateKey] = new JobGateRecord { GateKey = gateKey, ReleaseAt = releaseAt, Reason = reason };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobGateRecord>> ActiveGates(DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _gates.Values.Where(g => g.ReleaseAt > now)
                .Select(g => new JobGateRecord { GateKey = g.GateKey, ReleaseAt = g.ReleaseAt, Reason = g.Reason }).ToList();
            return Task.FromResult<IReadOnlyList<JobGateRecord>>(list);
        }
    }

    public Task<int> PurgeArchivable(DateTimeOffset olderThan, CancellationToken ct)
    {
        lock (_gate)
        {
            var stale = _records.Values
                .Where(r => r.Status is JobStatus.Completed or JobStatus.Cancelled
                            && r.LastSettledAt is { } s && s < olderThan)
                .Select(r => r.Id).ToList();
            foreach (var id in stale) _records.Remove(id);
            return Task.FromResult(stale.Count);
        }
    }

    public Task<int> PurgeFailed(DateTimeOffset olderThan, CancellationToken ct)
    {
        lock (_gate)
        {
            var stale = _records.Values
                .Where(r => r.Status is JobStatus.Failed or JobStatus.Dead
                            && r.LastSettledAt is { } s && s < olderThan)
                .Select(r => r.Id).ToList();
            foreach (var id in stale) _records.Remove(id);
            return Task.FromResult(stale.Count);
        }
    }

    public Task<int> TrimTerminal(string workType, int keep, CancellationToken ct)
    {
        if (keep <= 0) return Task.FromResult(0);
        lock (_gate)
        {
            var terminal = _records.Values
                .Where(r => r.WorkType == workType && r.IsTerminal)
                .OrderByDescending(r => r.LastSettledAt)
                .ToList();
            if (terminal.Count <= keep) return Task.FromResult(0);
            var excess = terminal.Skip(keep).Select(r => r.Id).ToList();
            foreach (var id in excess) _records.Remove(id);
            return Task.FromResult(excess.Count);
        }
    }

    public Task<long> CountActive(string workType, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult((long)_records.Values.Count(r => r.WorkType == workType && !r.IsTerminal));
    }

    public Task<JobsHealthSnapshot> HealthSnapshot(DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            long queued = 0, running = 0, reclaim = 0;
            DateTimeOffset? oldestDue = null;
            foreach (var r in _records.Values)
            {
                if (r.Status == JobStatus.Queued)
                {
                    queued++;
                    if (r.VisibleAt <= now && (oldestDue is null || r.VisibleAt < oldestDue)) oldestDue = r.VisibleAt;
                }
                else if (r.Status == JobStatus.Running)
                {
                    running++;
                    if (r.LeaseUntil is { } l && l < now) reclaim++;
                }
            }
            var age = oldestDue is { } od && now > od ? now - od : TimeSpan.Zero;
            return Task.FromResult(new JobsHealthSnapshot(queued, running, reclaim, age));
        }
    }

    private bool IsGated(string? gateKey, DateTimeOffset now)
        => gateKey is not null && _gates.TryGetValue(gateKey, out var g) && g.ReleaseAt > now;

    /// <summary>PMC-056 claim eligibility: a fresh reservation binds its named hand; unreserved or lapsed rows
    /// are open to anyone. Mirrored by both durable claim predicates so the tiers converge (ARCH-0079).</summary>
    private static bool OpenTo(JobRecord r, string owner, DateTimeOffset now)
        => r.ReservedFor is null
           || string.Equals(r.ReservedFor, owner, StringComparison.Ordinal)
           || (r.ReservedUntil is { } until && until < now);

    private static void Transition(JobRecord r, JobStatus to, DateTimeOffset at, string? note)
    {
        r.Transitions.Add(new JobTransition { At = at, From = r.Status, To = to, Note = note });
        r.Status = to;
    }
}
