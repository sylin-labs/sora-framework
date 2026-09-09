using System.Collections.Concurrent;
using Koan.Data.Abstractions;

namespace Koan.Jobs;

/// <summary>
/// Per-node throughput accumulator (JOBS-0005 §20.2). Bumps an in-memory delta on each terminal settle; a periodic
/// <see cref="FlushAsync"/> folds the deltas into this node's own metric shard rows via read-add-write
/// — contention-free because only this node writes its shard, and restart-safe because it adds to (never overwrites)
/// the persisted count. Opt-in and lossy-tolerant: a delta lost to a crash mid-flush is acceptable (the ledger is the
/// source of truth). Lives inside the per-node <see cref="JobOrchestrator"/>; not a DI service.
/// </summary>
internal sealed class JobMetricsRecorder
{
    private readonly bool _enabled;
    private readonly string _shard;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(string Bucket, string WorkType, string Outcome), long> _deltas = new();

    public JobMetricsRecorder(bool enabled, string shard, TimeProvider clock)
    {
        _enabled = enabled;
        _shard = shard;
        _clock = clock;
    }

    public bool Enabled => _enabled;

    /// <summary>Bump the in-memory delta for a terminal outcome (free; no I/O).</summary>
    public void Record(string workType, JobStatus outcome, DateTimeOffset at)
    {
        if (!_enabled) return;
        _deltas.AddOrUpdate((JobMetric.BucketOf(at), workType, outcome.ToString()), 1L, static (_, c) => c + 1);
    }

    /// <summary>Fold accumulated deltas into this node's shard rows. Subtracts exactly what it flushed so a concurrent
    /// <see cref="Record"/> during the await is preserved.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        if (!_enabled || _deltas.IsEmpty) return;
        var now = _clock.GetUtcNow();
        foreach (var key in _deltas.Keys.ToArray())
        {
            if (!_deltas.TryGetValue(key, out var delta) || delta <= 0) continue;
            var id = JobMetric.KeyOf(key.Bucket, key.WorkType, key.Outcome, _shard);
            var row = await JobMetric.Get(id, ct) ?? new JobMetric
            {
                Id = id, Bucket = key.Bucket, WorkType = key.WorkType, Outcome = key.Outcome, NodeShard = _shard,
            };
            row.Count += delta;
            row.LastFlushedAt = now;
            await JobMetric.Upsert(row, ct);
            _deltas.AddOrUpdate(key, 0L, (_, c) => c - delta);   // keep any concurrent additions
        }
    }

    /// <summary>Drop rollup rows last flushed before the cutoff (bucket-age retention).</summary>
    public async Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        if (!_enabled) return 0;
        var stale = await JobMetric.Query(m => m.LastFlushedAt < olderThan, ct);
        foreach (var r in stale) await JobMetric.Remove(r.Id, ct);
        return stale.Count;
    }
}
