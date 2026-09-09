namespace Koan.Jobs;

/// <summary>Retained throughput summaries for completed Jobs work.</summary>
public static class JobMetrics
{
    /// <summary>
    /// Summarizes terminal outcomes for a work type over <paramref name="from"/> through
    /// <paramref name="to"/>, inclusive, across every node shard and hourly bucket.
    /// </summary>
    /// <remarks>
    /// Reads the optional metrics rollup rather than the active Jobs ledger, so results survive
    /// <see cref="JobsOptions.ArchiveAfter"/> retention. Enable collection with
    /// <see cref="JobsOptions.MetricsEnabled"/>.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, long>> Summary(
        string workType,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        var fromBucket = JobMetric.BucketOf(from);
        var toBucket = JobMetric.BucketOf(to);
        // WorkType equality is pushed (indexed); range/grouping run over the small per-work-type rollup.
        var rows = await JobMetric.Query(metric => metric.WorkType == workType, ct);
        return rows
            .Where(metric => string.CompareOrdinal(metric.Bucket, fromBucket) >= 0
                && string.CompareOrdinal(metric.Bucket, toBucket) <= 0)
            .GroupBy(metric => metric.Outcome, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(metric => metric.Count),
                StringComparer.Ordinal);
    }
}
