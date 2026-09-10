using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Koan.Cache.Abstractions.Capabilities;
using Koan.Cache.Abstractions.Primitives;
using Koan.Cache.Abstractions.Stores;
using Microsoft.Extensions.Logging;

namespace Koan.Cache.Topology;

/// <summary>
/// L1/L2 read/write orchestrator. <strong>Composition over inheritance</strong> — does NOT
/// implement <see cref="ICacheStore"/>; exposes a focused verb set (<see cref="Read"/>,
/// <see cref="Write"/>, <see cref="Evict"/>, <see cref="EvictLocal"/>).
/// </summary>
/// <remarks>
/// <para>
/// Read path: L1 hit → return; L1 miss → L2 hit → backfill L1 → return; both miss → return Miss.
/// </para>
/// <para>
/// Write path: writes to L1 and L2 in parallel (writer write-through). Coherence broadcast is
/// the responsibility of <c>CoherenceCoordinator</c>, called separately by the cache client
/// after a successful write. M3 will wire that integration; M2 leaves coherence inactive.
/// </para>
/// <para>
/// <see cref="EvictLocal"/> is the receiver-side entry point invoked by
/// <c>CoherenceCoordinator</c>. It touches L1 ONLY — never L2, never republishes.
/// </para>
/// </remarks>
internal sealed class LayeredCache
{
    private readonly CacheTopology _topology;
    private readonly ILogger<LayeredCache> _logger;

    public LayeredCache(CacheTopology topology, ILogger<LayeredCache> logger)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _logger = logger;
    }

    public CacheTopology Topology => _topology;

    public async ValueTask<CacheFetchResult> Read(CacheKey key, CacheReadOptions options, CacheTier tier, CancellationToken ct)
    {
        _topology.Require(tier, "read");

        if (tier != CacheTier.RemoteOnly && _topology.LocalRoute is { } local)
        {
            RequireReadCapabilities(local, options);
            var l1Result = await local.Store.Fetch(key, options, ct).ConfigureAwait(false);
            if (l1Result.Hit) return l1Result;
        }

        if (tier != CacheTier.LocalOnly && _topology.RemoteRoute is { } remote)
        {
            RequireReadCapabilities(remote, options);
            var l2Result = await remote.Store.Fetch(key, options, ct).ConfigureAwait(false);
            if (l2Result.Hit)
            {
                if (tier == CacheTier.Layered && _topology.LocalRoute is { } backfillTarget && l2Result.Value is not null)
                {
                    var backfillWrite = BuildBackfillWriteOptions(l2Result);
                    try
                    {
                        RequireWriteCapabilities(backfillTarget, l2Result.Value, backfillWrite);
                        await backfillTarget.Store.Set(key, l2Result.Value, backfillWrite, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Koan.Cache: L1 backfill failed for {Key}; serving L2 value.", key.Value);
                    }
                }
                return l2Result;
            }
        }

        return CacheFetchResult.Miss(new CacheEntryOptions());
    }

    public async ValueTask Write(CacheKey key, CacheValue value, CacheWriteOptions options, CacheTier tier, CancellationToken ct)
    {
        _topology.Require(tier, "write");
        var writes = new List<ValueTask>(2);
        Exception? failure = null;

        try
        {
            if (tier != CacheTier.RemoteOnly && _topology.LocalRoute is { } local)
            {
                var localOptions = ApplyL1Ttl(options);
                RequireWriteCapabilities(local, value, localOptions);
                writes.Add(local.Store.Set(key, value, localOptions, ct));
            }

            if (tier != CacheTier.LocalOnly && _topology.RemoteRoute is { } remote)
            {
                RequireWriteCapabilities(remote, value, options);
                writes.Add(remote.Store.Set(key, value, options, ct));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        foreach (var write in writes)
        {
            try
            {
                await write.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async ValueTask<bool> Evict(CacheKey key, CancellationToken ct)
    {
        var anyRemoved = false;

        if (_topology.Local is { } l1)
            anyRemoved |= await l1.Remove(key, ct).ConfigureAwait(false);

        if (_topology.Remote is { } l2)
            anyRemoved |= await l2.Remove(key, ct).ConfigureAwait(false);

        return anyRemoved;
    }

    public async ValueTask Touch(CacheKey key, TimeSpan? newAbsoluteTtl, CacheTier tier, CancellationToken ct)
    {
        _topology.Require(tier, "touch");
        if (tier != CacheTier.RemoteOnly && _topology.Local is { } l1)
            await l1.Touch(key, newAbsoluteTtl, ct).ConfigureAwait(false);

        if (tier != CacheTier.LocalOnly && _topology.Remote is { } l2)
            await l2.Touch(key, newAbsoluteTtl, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> Exists(CacheKey key, CacheTier tier, CancellationToken ct)
    {
        _topology.Require(tier, "exists");
        if (tier != CacheTier.RemoteOnly && _topology.Local is { } l1 && await l1.Exists(key, ct).ConfigureAwait(false))
            return true;
        if (tier != CacheTier.LocalOnly && _topology.Remote is { } l2 && await l2.Exists(key, ct).ConfigureAwait(false))
            return true;
        return false;
    }

    /// <summary>
    /// Enumerate keys carrying the given tag. Prefers L2 (shared truth across nodes);
    /// falls back to L1 if no remote is configured. Used by tag-flush operations.
    /// </summary>
    public IAsyncEnumerable<TaggedCacheKey> EnumerateByTag(string tag, CancellationToken ct)
    {
        var source = _topology.Remote ?? _topology.Local;
        return source is not null
            ? source.EnumerateByTag(tag, ct)
            : EmptyAsync<TaggedCacheKey>();
    }

    private static async IAsyncEnumerable<T> EmptyAsync<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Apply a remote invalidation to L1 ONLY. Never touches L2 (shared and already evicted
    /// by the writer); never republishes (would create feedback loops).
    /// </summary>
    public ValueTask<bool> EvictLocal(CacheKey key, CancellationToken ct)
        => _topology.Local is { } l1
            ? l1.Remove(key, ct)
            : ValueTask.FromResult(false);

    private static CacheWriteOptions ApplyL1Ttl(CacheWriteOptions options)
    {
        // Substitute the L1-effective TTL into AbsoluteTtl for the L1 store's write.
        // L1 stores see only one TTL; L1AbsoluteTtl is a hint we resolve here.
        var effective = options.GetEffectiveL1Ttl();
        if (effective is null || effective == options.AbsoluteTtl) return options;
        return options with { AbsoluteTtl = effective };
    }

    private static CacheWriteOptions BuildBackfillWriteOptions(CacheFetchResult l2Result)
    {
        // Backfill uses the L1 TTL derivation; do NOT broadcast a coherence invalidation
        // because no data change occurred — this is a cache populate from a cold read.
        var remoteAbs = l2Result.AbsoluteExpiration;
        var ttl = remoteAbs.HasValue ? remoteAbs.Value - DateTimeOffset.UtcNow : (TimeSpan?)null;
        return new CacheWriteOptions(
            AbsoluteTtl: ttl,
            L1AbsoluteTtl: null,            // derive in GetEffectiveL1Ttl
            SlidingTtl: null,
            AllowStaleFor: null,
            Tags: new HashSet<string>(),
            Region: null,
            ScopeId: null,
            ForceCoherenceBroadcast: false);
    }

    private static void RequireReadCapabilities(CacheStoreRoute route, CacheReadOptions options)
    {
        if (options.AllowStaleFor is not null)
            route.Capabilities.Require(CacheCaps.BoundedStaleServing);
    }

    private static void RequireWriteCapabilities(CacheStoreRoute route, CacheValue value, CacheWriteOptions options)
    {
        if (options.Tags.Count > 0)
            route.Capabilities.Require(CacheCaps.Tags);
        if (options.SlidingTtl is not null)
            route.Capabilities.Require(CacheCaps.SlidingExpiration);
        if (value.ContentKind == CacheContentKind.Binary)
            route.Capabilities.Require(CacheCaps.BinaryPayload);
    }

}
