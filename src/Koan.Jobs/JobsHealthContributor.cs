using Koan.Core;
using Koan.Core.Observability.Health;
using Microsoft.Extensions.Options;

namespace Koan.Jobs;

/// <summary>
/// Self-reporting for the Jobs pillar (JOBS-0008) — the signal whose absence let the lane-starvation incident run
/// undetected for hours (a stalled queue presents as a perfectly healthy process). Publishes queued/running depth,
/// reclaim backlog, and the oldest-due-queued age as health facts, and flips to <see cref="HealthState.Degraded"/>
/// when the oldest job has waited past <see cref="JobsOptions.QueueAgeWarning"/> (opt-in). Non-critical and cheap:
/// the probe is a few index-served counts + one oldest-due seek (NOT a per-lane scan — a health check the framework
/// auto-runs at boot must stay bounded), mirroring <c>AiSourcesHealthContributor</c>.
/// </summary>
internal sealed class JobsHealthContributor(IJobLedger ledger, IOptions<JobsOptions> options, TimeProvider clock) : IHealthContributor
{
    public string Name => "Koan.Jobs";
    public bool IsCritical => false;

    public async Task<HealthReport> Check(CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        try
        {
            var now = clock.GetUtcNow();
            var s = await ledger.HealthSnapshot(now, ct);
            var workersOnline = (await WorkerNode.Query(w => w.LastSeenAt >= now - TimeSpan.FromMinutes(5), ct)).Count;
            var data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["queued"] = s.Queued,
                ["running"] = s.Running,
                ["reclaimBacklog"] = s.ReclaimBacklog,
                ["oldestQueuedAgeSeconds"] = (long)s.OldestQueuedAge.TotalSeconds,
                ["workersOnline"] = workersOnline,
            };

            var budget = options.Value.QueueAgeWarning;
            if (budget > TimeSpan.Zero && s.OldestQueuedAge > budget)
                return new HealthReport(Name, HealthState.Degraded,
                    $"Oldest queued job has waited {s.OldestQueuedAge.TotalSeconds:N0}s " +
                    $"(> {budget.TotalSeconds:N0}s budget) — possible lane starvation/stall.", null, data);

            return new HealthReport(Name, HealthState.Healthy,
                $"{s.Queued} queued, {s.Running} running, {workersOnline} workers online", null, data);
        }
        catch (Exception ex)
        {
            return new HealthReport(Name, HealthState.Unhealthy, ex.Message, null, null);
        }
    }
}
