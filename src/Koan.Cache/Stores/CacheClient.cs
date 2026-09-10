using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koan.Cache.Abstractions;
using Koan.Cache.Abstractions.Primitives;
using Koan.Cache.Abstractions.Serialization;
using Koan.Cache.Abstractions.Stores;
using Koan.Cache.Coherence;
using Koan.Cache.Diagnostics;
using Koan.Cache.Identity;
using Koan.Cache.Options;
using Koan.Cache.Scope;
using Koan.Cache.Stores.Internal;
using Koan.Cache.Topology;
using Koan.Core.Concurrency;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koan.Cache.Stores;

/// <summary>
/// Public typed surface over <see cref="LayeredCache"/>. Owns serializer resolution,
/// scope projection, singleflight gating, and developer-facing options shaping.
/// </summary>
internal sealed class CacheClient : ICacheClient, ICacheSubjectClient
{
    private const string LayeredProviderTag = "layered";

    private readonly LayeredCache _layered;
    private readonly IReadOnlyList<ICacheSerializer> _serializers;
    private readonly IKeyedLeaseGate _singleflight;
    private readonly ICacheScopeAccessor _scopeAccessor;
    private readonly CacheInstrumentation _instrumentation;
    private readonly CacheIdentityPlan _identity;
    private readonly CoherenceCoordinator _coherence;
    private readonly IOptionsMonitor<CacheOptions> _options;
    private readonly ILogger<CacheClient> _logger;
    private readonly ConcurrentDictionary<Type, ICacheSerializer> _serializerCache = new();

    public CacheClient(
        LayeredCache layered,
        IEnumerable<ICacheSerializer> serializers,
        IKeyedLeaseGate singleflight,
        ICacheScopeAccessor scopeAccessor,
        CacheInstrumentation instrumentation,
        CacheIdentityPlan identity,
        CoherenceCoordinator coherence,
        IOptionsMonitor<CacheOptions> options,
        ILogger<CacheClient> logger)
    {
        _layered = layered ?? throw new ArgumentNullException(nameof(layered));
        _serializers = serializers?.ToArray() ?? Array.Empty<ICacheSerializer>();
        if (_serializers.Count == 0)
            throw new InvalidOperationException("No cache serializers are registered. Reference Sylin.Koan.Cache and call AddKoan().");

        _singleflight = singleflight ?? throw new ArgumentNullException(nameof(singleflight));
        _coherence = coherence ?? throw new ArgumentNullException(nameof(coherence));
        _scopeAccessor = scopeAccessor;
        _instrumentation = instrumentation;
        _identity = identity;
        _options = options;
        _logger = logger;
    }

    public CacheScopeHandle BeginScope(string scopeId, string? region = null)
    {
        var context = _scopeAccessor.Push(scopeId, region ?? _options.CurrentValue.DefaultRegion);
        return new CacheScopeHandle(scopeId, region, () => _scopeAccessor.Pop(context));
    }

    public ICacheEntryBuilder<T> CreateEntry<T>(CacheKey key)
    {
        var defaults = _options.CurrentValue;
        var options = new CacheEntryOptions
        {
            AbsoluteTtl = defaults.DefaultTtlSeconds > 0
                ? TimeSpan.FromSeconds(defaults.DefaultTtlSeconds)
                : null,
            L1AbsoluteTtl = defaults.DefaultL1TtlSeconds is > 0
                ? TimeSpan.FromSeconds(defaults.DefaultL1TtlSeconds.Value)
                : null,
            Tier = defaults.DefaultTier,
            ForceCoherenceBroadcast = defaults.BroadcastInvalidationByDefault,
            SingleflightTimeout = defaults.DefaultSingleflightTimeout
        };

        var scope = _scopeAccessor.Current;
        if (scope.HasScope)
            options = options with { ScopeId = scope.ScopeId, Region = scope.Region ?? _options.CurrentValue.DefaultRegion };

        return new CacheEntryBuilder<T>(this, key, options);
    }

    public ValueTask<CacheFetchResult> Get(CacheKey key, CacheEntryOptions options, CancellationToken ct)
        => Get(key, options, subject: null, ct);

    internal async ValueTask<CacheFetchResult> Get(
        CacheKey key,
        CacheEntryOptions options,
        Type? subject,
        CancellationToken ct)
    {
        var normalized = ApplyScope(options);
        var physical = _identity.Bind(key, normalized.Tags, subject, "cache read");
        var result = await _layered.Read(physical.Key, normalized.ToReadOptions(), normalized.Tier, ct).ConfigureAwait(false);
        if (result.Hit) _instrumentation.RecordHit(physical.Key.Value, LayeredProviderTag);
        else _instrumentation.RecordMiss(physical.Key.Value, LayeredProviderTag);
        return result;
    }

    public ValueTask<T?> GetAsync<T>(CacheKey key, CacheEntryOptions options, CancellationToken ct)
        => GetAsync<T>(key, options, subject: null, ct);

    internal async ValueTask<T?> GetAsync<T>(
        CacheKey key,
        CacheEntryOptions options,
        Type? subject,
        CancellationToken ct)
    {
        var normalized = ApplyScope(options);
        var physical = _identity.Bind(key, normalized.Tags, subject, "cache read");
        var (found, value) = await TryGetPhysicalValueAsync<T>(physical.Key, normalized, ct).ConfigureAwait(false);
        return found ? value : default;
    }

    public ValueTask<T?> GetOrAddAsync<T>(CacheKey key, Func<CancellationToken, ValueTask<T?>> valueFactory, CacheEntryOptions options, CancellationToken ct)
        => GetOrAddAsync(key, valueFactory, options, subject: null, ct);

    internal ValueTask<T?> GetOrAddAsync<T>(
        CacheKey key,
        Func<CancellationToken, ValueTask<T?>> valueFactory,
        CacheEntryOptions options,
        Type? subject,
        CancellationToken ct)
    {
        if (valueFactory is null) throw new ArgumentNullException(nameof(valueFactory));
        return ExecuteGetOrAddAsync(key, valueFactory, options, subject, ct);
    }

    public ValueTask<bool> Exists(CacheKey key, CacheEntryOptions options, CancellationToken ct)
        => Exists(key, options, subject: null, ct);

    internal ValueTask<bool> Exists(
        CacheKey key,
        CacheEntryOptions options,
        Type? subject,
        CancellationToken ct)
    {
        var normalized = ApplyScope(options);
        var physical = _identity.Bind(key, normalized.Tags, subject, "cache exists");
        return _layered.Exists(physical.Key, normalized.Tier, ct);
    }

    public ValueTask SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken ct)
        => SetAsync(key, value, options, subject: null, ct);

    internal async ValueTask SetAsync<T>(
        CacheKey key,
        T value,
        CacheEntryOptions options,
        Type? subject,
        CancellationToken ct)
    {
        if (value is null)
        {
            await Remove(key, subject, ct).ConfigureAwait(false);
            return;
        }

        var normalized = ApplyScope(options);
        var physical = _identity.Bind(key, normalized.Tags, subject, "cache write");
        normalized = WithTags(normalized, physical.Tags);
        await SetPhysicalAsync(physical.Key, value, normalized, ct).ConfigureAwait(false);
    }

    private async ValueTask SetPhysicalAsync<T>(
        CacheKey key,
        T value,
        CacheEntryOptions normalized,
        CancellationToken ct)
    {
        var serializer = ResolveSerializer(typeof(T), normalized.ContentKind);
        var cacheValue = await serializer.Serialize(value!, typeof(T), normalized, ct).ConfigureAwait(false);
        await _layered.Write(key, cacheValue, normalized.ToWriteOptions(), normalized.Tier, ct).ConfigureAwait(false);
        _instrumentation.RecordSet(key.Value, LayeredProviderTag);

        if (normalized.ForceCoherenceBroadcast)
        {
            await _coherence.BroadcastEvict(key, ct).ConfigureAwait(false);
            _instrumentation.RecordInvalidation(key.Value, LayeredProviderTag, "write-broadcast");
        }
    }

    public ValueTask<bool> Remove(CacheKey key, CancellationToken ct)
        => Remove(key, subject: null, ct);

    public async ValueTask<bool> Remove(CacheKey key, Type? subject, CancellationToken ct)
    {
        var physical = _identity.Bind(key, tags: null, subject, "cache remove");
        return await RemovePhysical(physical.Key, ct).ConfigureAwait(false);
    }

    private async ValueTask<bool> RemovePhysical(CacheKey key, CancellationToken ct)
    {
        var timeout = _options.CurrentValue.DefaultSingleflightTimeout;
        return await _singleflight.RunAsync(key.Value, timeout, async innerCt =>
        {
            var success = await _layered.Evict(key, innerCt).ConfigureAwait(false);
            _instrumentation.RecordRemove(key.Value, LayeredProviderTag, success);
            // Removes always broadcast; peers must drop their L1 entries even if the key wasn't present locally.
            await _coherence.BroadcastEvict(key, innerCt).ConfigureAwait(false);
            return success;
        }, ct).ConfigureAwait(false);
    }

    public ValueTask Touch(CacheKey key, CacheEntryOptions options, CancellationToken ct)
        => Touch(key, options, subject: null, ct);

    internal ValueTask Touch(
        CacheKey key,
        CacheEntryOptions options,
        Type? subject,
        CancellationToken ct)
    {
        var normalized = ApplyScope(options);
        var physical = _identity.Bind(key, normalized.Tags, subject, "cache touch");
        return _layered.Touch(physical.Key, normalized.AbsoluteTtl, normalized.Tier, ct);
    }

    public async ValueTask<long> FlushTags(IReadOnlyCollection<string> tags, CancellationToken ct)
        => await FlushTags(tags, subject: null, ct).ConfigureAwait(false);

    public async ValueTask<long> FlushTags(
        IReadOnlyCollection<string> tags,
        Type? subject,
        CancellationToken ct)
    {
        if (tags is null) throw new ArgumentNullException(nameof(tags));

        var normalized = NormalizeTags(tags);
        if (normalized.Count == 0) return 0;
        normalized = _identity.BindTags(normalized, subject, "cache tag flush");

        ct.ThrowIfCancellationRequested();

        var keys = new HashSet<CacheKey>();
        var expiredKeys = new HashSet<CacheKey>();
        var now = DateTimeOffset.UtcNow;

        foreach (var tag in normalized)
        {
            ct.ThrowIfCancellationRequested();

            await foreach (var entry in _layered.EnumerateByTag(tag, ct).ConfigureAwait(false))
            {
                if (entry.IsExpired(now))
                {
                    expiredKeys.Add(entry.Key);
                    continue;
                }

                keys.Add(entry.Key);
            }
        }

        foreach (var expiredKey in expiredKeys)
        {
            ct.ThrowIfCancellationRequested();
            await RemovePhysical(expiredKey, ct).ConfigureAwait(false);
        }

        var removed = 0L;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            if (await RemovePhysical(key, ct).ConfigureAwait(false))
                removed++;
        }

        return removed;
    }

    public async ValueTask<long> CountTags(IReadOnlyCollection<string> tags, CancellationToken ct)
        => await CountTags(tags, subject: null, ct).ConfigureAwait(false);

    public async ValueTask<long> CountTags(
        IReadOnlyCollection<string> tags,
        Type? subject,
        CancellationToken ct)
    {
        if (tags is null) throw new ArgumentNullException(nameof(tags));

        var normalized = NormalizeTags(tags);
        if (normalized.Count == 0) return 0;
        normalized = _identity.BindTags(normalized, subject, "cache tag count");

        ct.ThrowIfCancellationRequested();

        var keys = new HashSet<CacheKey>();
        var expiredKeys = new HashSet<CacheKey>();
        var now = DateTimeOffset.UtcNow;

        foreach (var tag in normalized)
        {
            ct.ThrowIfCancellationRequested();

            await foreach (var entry in _layered.EnumerateByTag(tag, ct).ConfigureAwait(false))
            {
                if (entry.IsExpired(now))
                {
                    expiredKeys.Add(entry.Key);
                    continue;
                }

                keys.Add(entry.Key);
            }
        }

        foreach (var expiredKey in expiredKeys)
        {
            ct.ThrowIfCancellationRequested();
            await RemovePhysical(expiredKey, ct).ConfigureAwait(false);
        }

        return keys.Count;
    }

    private async ValueTask<(bool Found, T? Value)> TryGetPhysicalValueAsync<T>(
        CacheKey key,
        CacheEntryOptions normalized,
        CancellationToken ct)
    {
        var result = await _layered.Read(key, normalized.ToReadOptions(), normalized.Tier, ct).ConfigureAwait(false);
        if (result.Hit) _instrumentation.RecordHit(key.Value, LayeredProviderTag);
        else _instrumentation.RecordMiss(key.Value, LayeredProviderTag);
        if (!result.Hit || result.Value is null)
            return (false, default);

        var serializer = ResolveSerializer(typeof(T), result.Options.ContentKind);
        var value = await serializer.DeserializeAsync<T>(result.Value, ct).ConfigureAwait(false);
        return (true, value);
    }

    private CacheEntryOptions ApplyScope(CacheEntryOptions options)
    {
        var scope = _scopeAccessor.Current;
        if (!scope.HasScope)
        {
            if (string.IsNullOrWhiteSpace(options.Region))
                return options with { Region = _options.CurrentValue.DefaultRegion };
        }
        else
        {
            return options with
            {
                ScopeId = options.ScopeId ?? scope.ScopeId,
                Region = options.Region ?? scope.Region ?? _options.CurrentValue.DefaultRegion
            };
        }

        return options;
    }

    private ICacheSerializer ResolveSerializer(Type type, CacheContentKind kind)
    {
        return _serializerCache.GetOrAdd(type, t =>
        {
            ICacheSerializer? candidate = null;
            if (kind == CacheContentKind.String)
                candidate = _serializers.FirstOrDefault(s => string.Equals(s.ContentType, CacheConstants.ContentTypes.String, StringComparison.OrdinalIgnoreCase));
            else if (kind == CacheContentKind.Binary)
                candidate = _serializers.FirstOrDefault(s => string.Equals(s.ContentType, CacheConstants.ContentTypes.Binary, StringComparison.OrdinalIgnoreCase));

            candidate ??= _serializers.FirstOrDefault(s => s.CanHandle(t));
            candidate ??= _serializers.First();
            return candidate;
        });
    }

    private async ValueTask<T?> ExecuteGetOrAddAsync<T>(
        CacheKey key,
        Func<CancellationToken, ValueTask<T?>> valueFactory,
        CacheEntryOptions options,
        Type? subject,
        CancellationToken ct)
    {
        var normalized = ApplyScope(options);
        var physical = _identity.Bind(key, normalized.Tags, subject, "cache get-or-add");
        normalized = WithTags(normalized, physical.Tags);

        var timeout = normalized.SingleflightTimeout ?? _options.CurrentValue.DefaultSingleflightTimeout;
        return await _singleflight.RunAsync<T?>(physical.Key.Value, timeout, async innerCt =>
        {
            var (found, existing) = await TryGetPhysicalValueAsync<T>(physical.Key, normalized, innerCt).ConfigureAwait(false);
            if (found) return existing;

            var created = await valueFactory(innerCt).ConfigureAwait(false);
            if (created is null) return default;

            await SetPhysicalAsync(physical.Key, created, normalized, innerCt).ConfigureAwait(false);
            return created;
        }, ct).ConfigureAwait(false);
    }

    private static CacheEntryOptions WithTags(
        CacheEntryOptions options,
        IReadOnlyCollection<string> tags)
        => options with
        {
            Tags = tags.Count == 0
                ? new HashSet<string>()
                : new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase)
        };

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            set.Add(tag.Trim());
        }
        return set.Count == 0 ? Array.Empty<string>() : set.ToArray();
    }
}
