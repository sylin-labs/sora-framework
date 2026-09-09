using System.Reflection;
using Koan.Core.Diagnostics;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Core.Querying;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Core.Relationships;

internal sealed class RelationshipQueryExecutor(IServiceProvider services, IDataService data) : IRelationshipQueryExecutor
{
    public async Task<RelationshipQueryResult<TChild, TKey>> LoadChildren<TParent, TChild, TKey>(
        IReadOnlyCollection<TKey> parentIds,
        string referenceProperty,
        Filter? additionalFilter = null,
        RelationshipQueryPolicy? policy = null,
        string? correlationId = null,
        CancellationToken ct = default)
        where TParent : class, IEntity<TKey>
        where TChild : class, IEntity<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(parentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceProperty);
        policy ??= RelationshipQueryPolicy.Strict;
        policy.Validate();
        ct.ThrowIfCancellationRequested();

        var property = typeof(TChild).GetProperty(referenceProperty, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException(
                $"Relationship reference property '{referenceProperty}' was not found on {typeof(TChild).FullName}.",
                nameof(referenceProperty));
        if (!property.CanRead)
            throw new ArgumentException(
                $"Relationship reference property '{referenceProperty}' on {typeof(TChild).FullName} is not readable.",
                nameof(referenceProperty));

        var ids = parentIds.Distinct().ToArray();
        var provider = AggregateConfigs.Get<TChild, TKey>(services).Provider;
        var subject = $"relationship:{typeof(TParent).Name}->{typeof(TChild).Name}.{referenceProperty}";
        var correlation = string.IsNullOrWhiteSpace(correlationId) ? subject : correlationId;
        var repo = data.GetRepository<TChild, TKey>();
        var queryRepo = repo as IQueryRepository<TChild, TKey>
            ?? throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                Infrastructure.Constants.Diagnostics.Reasons.MissingExecutionProfile,
                "Route the child entity to a query-capable data adapter.");

        var capabilities = DataCaps.Describe(repo, provider);
        var filterSupport = capabilities.Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;
        var execution = capabilities.Detail<FilterExecutionProfile>(DataCaps.Query.FilterExecution)
            ?? new FilterExecutionProfile(FilterExecutionKind.Unknown);

        if (ids.Length == 0)
        {
            var emptyMode = execution.Kind switch
            {
                FilterExecutionKind.Native => RelationshipExecutionMode.Native,
                FilterExecutionKind.InMemory => RelationshipExecutionMode.InMemory,
                FilterExecutionKind.Scan => RelationshipExecutionMode.BoundedScan,
                _ => RelationshipExecutionMode.BoundedFallback
            };
            return new RelationshipQueryResult<TChild, TKey>(
                new Dictionary<TKey, IReadOnlyList<TChild>>(),
                new RelationshipExecutionDecision(emptyMode, provider, 0, 0, 0, policy.MaxFallbackCandidates));
        }

        var relationshipFilter = ids.Length == 1
            ? Filter.Eq(referenceProperty, ids[0])
            : Filter.In(referenceProperty, ids.Cast<object?>().ToArray());
        var filter = additionalFilter is null
            ? relationshipFilter
            : Filter.All(relationshipFilter, additionalFilter);
        var query = QueryDefinition.All.Where(filter);
        var (adapterQuery, residual) = FilterPushdownCoordinator.Plan(query, filterSupport, typeof(TChild));

        IReadOnlyList<TChild> items;
        IQueryReadEvidence? readEvidence = null;
        RelationshipExecutionMode mode;
        int? candidatesExamined = null;

        switch (execution.Kind)
        {
            case FilterExecutionKind.Native when residual is null:
                mode = RelationshipExecutionMode.Native;
                if (policy.MaxResults is { } nativeLimit)
                {
                    var count = await queryRepo.Count(adapterQuery, ct).ConfigureAwait(false);
                    if (count.Value > nativeLimit)
                        throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                            Infrastructure.Constants.Diagnostics.Reasons.ResultLimit,
                            $"Narrow the relationship or raise its explicit result limit above {nativeLimit}.", nativeLimit);
                }
                var native = await DataQueryExecution<TChild, TKey>
                    .QueryCandidates(repo, queryRepo, adapterQuery, ct)
                    .ConfigureAwait(false);
                items = FilterPushdownCoordinator.Finalize(query, adapterQuery, residual, native).Page;
                readEvidence = native.ReadEvidence;
                break;

            case FilterExecutionKind.InMemory:
            {
                mode = RelationshipExecutionMode.InMemory;
                var raw = await DataQueryExecution<TChild, TKey>
                    .QueryCandidates(repo, queryRepo, adapterQuery, ct)
                    .ConfigureAwait(false);
                items = FilterPushdownCoordinator.Finalize(query, adapterQuery, residual, raw).Page;
                break;
            }

            case FilterExecutionKind.Scan:
            {
                if (policy.MaxFallbackCandidates is not { } scanLimit)
                    throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                        Infrastructure.Constants.Diagnostics.Reasons.UnboundedScan,
                        "Opt in with RelationshipQueryPolicy.Bounded(maxCandidates), or route the child entity to a native-filter provider.");

                var bounded = repo as IBoundedQueryRepository<TChild, TKey>;
                if (bounded is null || !execution.SupportsBoundedCandidates)
                    throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                        Infrastructure.Constants.Diagnostics.Reasons.MissingExecutionProfile,
                        "Use an adapter that advertises provider-enforced bounded candidates or native filtering.");

                var result = await DataQueryExecution<TChild, TKey>
                    .QueryBoundedCandidates(repo, bounded, adapterQuery, scanLimit, ct)
                    .ConfigureAwait(false);
                candidatesExamined = result.CandidatesExamined;
                if (result.CandidateLimitExceeded)
                    throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                        Infrastructure.Constants.Diagnostics.Reasons.FallbackLimit,
                        $"Narrow the data set, raise the explicit candidate limit above {scanLimit}, or use native filtering.", scanLimit);

                var raw = AsRepositoryResult(result.Items, adapterQuery.Filter is not null);
                items = FilterPushdownCoordinator.Finalize(query, adapterQuery, residual, raw).Page;
                mode = RelationshipExecutionMode.BoundedScan;
                break;
            }

            case FilterExecutionKind.Native:
            {
                var fallbackLimit = policy.MaxFallbackCandidates;
                if (fallbackLimit is null)
                    throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                        Infrastructure.Constants.Diagnostics.Reasons.UnboundedScan,
                        "The adapter cannot push the complete relationship predicate. Opt in to a bounded fallback or use a natively supported predicate.");

                var count = await queryRepo.Count(adapterQuery, ct).ConfigureAwait(false);
                candidatesExamined = checked((int)Math.Min(count.Value, int.MaxValue));
                if (count.Value > fallbackLimit.Value)
                    throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                        Infrastructure.Constants.Diagnostics.Reasons.FallbackLimit,
                        $"Narrow the predicate, raise the explicit candidate limit above {fallbackLimit.Value}, or use native filtering.", fallbackLimit);

                var raw = await DataQueryExecution<TChild, TKey>
                    .QueryCandidates(repo, queryRepo, adapterQuery, ct)
                    .ConfigureAwait(false);
                items = FilterPushdownCoordinator.Finalize(query, adapterQuery, residual, raw).Page;
                mode = RelationshipExecutionMode.BoundedFallback;
                break;
            }

            default:
                throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                    Infrastructure.Constants.Diagnostics.Reasons.MissingExecutionProfile,
                    "Use an adapter that declares whether relationship filters are native, in-memory, or safely bounded scans.");
        }

        if (policy.MaxResults is { } resultLimit && items.Count > resultLimit)
            throw Reject<TChild, TKey>(provider, subject, correlation, referenceProperty,
                Infrastructure.Constants.Diagnostics.Reasons.ResultLimit,
                $"Narrow the relationship or raise its explicit result limit above {resultLimit}.", resultLimit);

        await DataQueryExecution<TChild, TKey>.MaterializeVisible(repo, items, ct).ConfigureAwait(false);
        DataQueryExecution<TChild, TKey>.ValidateEvidence(readEvidence, items);

        var buckets = ids.ToDictionary(id => id, _ => new List<TChild>());
        foreach (var item in items)
        {
            if (property.GetValue(item) is TKey key && buckets.TryGetValue(key, out var bucket))
                bucket.Add(item);
        }
        var grouped = buckets.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<TChild>)pair.Value.ToArray());

        var reason = mode switch
        {
            RelationshipExecutionMode.Native => Infrastructure.Constants.Diagnostics.Reasons.NativeFilter,
            RelationshipExecutionMode.InMemory => Infrastructure.Constants.Diagnostics.Reasons.InMemoryFilter,
            RelationshipExecutionMode.BoundedScan => Infrastructure.Constants.Diagnostics.Reasons.BoundedScan,
            _ => Infrastructure.Constants.Diagnostics.Reasons.BoundedFallback
        };
        Record(subject, correlation, KoanFactState.Selected,
            $"Selected {mode} relationship execution for {typeof(TParent).Name} to {typeof(TChild).Name}.",
            reason, null);

        return new RelationshipQueryResult<TChild, TKey>(
            grouped,
            new RelationshipExecutionDecision(mode, provider, ids.Length, items.Count, candidatesExamined,
                policy.MaxFallbackCandidates)) { ReadEvidence = readEvidence };
    }

    private RelationshipQueryRejectedException Reject<TChild, TKey>(
        string provider,
        string subject,
        string correlation,
        string referenceProperty,
        string reason,
        string correction,
        int? limit = null)
        where TChild : class, IEntity<TKey>
        where TKey : notnull
    {
        Record(subject, correlation, KoanFactState.Rejected,
            $"Rejected unsafe relationship execution for {subject["relationship:".Length..]}.", reason, correction);
        return new RelationshipQueryRejectedException(
            subject["relationship:".Length..].Split("->", 2)[0],
            typeof(TChild).Name,
            referenceProperty,
            provider,
            reason,
            correction,
            limit);
    }

    private void Record(
        string subject,
        string correlation,
        KoanFactState state,
        string summary,
        string reason,
        string? correction)
        => services.GetService<IKoanRuntimeFactRecorder>()?.Record(new KoanFactDescriptor(
            Infrastructure.Constants.Diagnostics.Codes.RelationshipExecution,
            KoanFactKind.Capability,
            state,
            subject,
            summary,
            reason,
            correction,
            "Koan.Data.Core.Relationships",
            correlation));

    private static RepositoryQueryResult<TChild> AsRepositoryResult<TChild>(
        IReadOnlyList<TChild> items,
        bool filterHandled)
        => new()
        {
            Items = items,
            FilterHandled = filterHandled,
            PaginationHandled = false
        };

}
