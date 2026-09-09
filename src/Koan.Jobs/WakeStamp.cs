using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Core.Model;

namespace Koan.Jobs;

/// <summary>
/// The framework-owned wake carriage (JOBS-0009): one fixed row per durable store. Submission bumps it inside the
/// same ambient transaction as the ledger records, so a rolled-back submission never moves it. Nodes probe this
/// single indexed row at short cadence and run the full claim scan only when the stamp moved — or on the slow
/// fallback timer regardless. A missed bump costs one <c>PollInterval</c>, never work: polling remains the
/// complete correctness mechanism.
/// </summary>
public sealed class WakeStamp : Entity<WakeStamp>, IAmbientExempt
{
    public const string SingletonId = "koan-jobs-wake";

    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Move the stamp once per durable append batch. A failed bump is swallowed by design — it is a
    /// latency hint, not a correctness write; the probe's slow fallback drains on <c>PollInterval</c> anyway.</summary>
    internal static async Task TryBump(CancellationToken ct)
    {
        using var storageScope = JobsStorageScope.Enter();
        try
        {
            var stamp = await WakeStamp.Get(SingletonId, ct);
            if (stamp is null)
                await WakeStamp.Upsert(new WakeStamp { Id = SingletonId, Version = 1, UpdatedAt = DateTimeOffset.UtcNow }, ct);
            else
            {
                stamp.Version++;
                stamp.UpdatedAt = DateTimeOffset.UtcNow;
                await WakeStamp.Upsert(stamp, ct);
            }
        }
        catch (Exception)
        {
            // Deliberately narrow in purpose even though broad in type: any store failure here means the same
            // thing — peers discover work at their next full pass. Swallowing keeps the hint off the submit path.
        }
    }

    /// <summary>Current stamp version, or -1 when the read fails (callers treat unknown as no-signal).</summary>
    internal static async Task<long> ReadVersion(CancellationToken ct)
    {
        using var storageScope = JobsStorageScope.Enter();
        try
        {
            return (await WakeStamp.Get(SingletonId, ct))?.Version ?? 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
