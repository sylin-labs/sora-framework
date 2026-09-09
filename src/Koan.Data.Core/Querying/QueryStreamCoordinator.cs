using System.Reflection;
using System.Runtime.CompilerServices;
using Koan.Core.Diagnostics;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Core.Sorting;

namespace Koan.Data.Core.Querying;

/// <summary>
/// Composes one provider-bounded query page at a time into the public async sequence. This is the
/// only Data.Core owner of streaming pagination, ordering, residual evaluation, and rejection facts.
/// </summary>
internal static class QueryStreamCoordinator
{
    public static async IAsyncEnumerable<TEntity> Execute<TEntity, TKey>(
        IDataRepository<TEntity, TKey> repository,
        QueryDefinition query,
        string provider,
        int? requestedBatchSize,
        IKoanRuntimeFactRecorder? facts,
        Func<IDisposable> enterContext,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(enterContext);

        var batchSize = requestedBatchSize ?? Infrastructure.Constants.Defaults.UnboundedLoopPageSize;
        if (batchSize <= 0)
            throw Reject<TEntity>(facts, provider,
                Infrastructure.Constants.Diagnostics.Reasons.InvalidStreamBatchSize,
                "Use a positive batch size, or omit it to use Koan's bounded default.", batchSize);

        ct.ThrowIfCancellationRequested();

        var capabilities = DataCaps.Describe(repository, provider);
        if (!capabilities.Has(DataCaps.Query.ProviderBoundedPaging))
            throw Reject<TEntity>(facts, provider,
                Infrastructure.Constants.Diagnostics.Reasons.MissingProviderBoundedPaging,
                "Route this Entity to an adapter that advertises provider-bounded paging, or materialize the query explicitly.",
                batchSize);

        if (repository is not IQueryRepository<TEntity, TKey> queryRepository)
            throw Reject<TEntity>(facts, provider,
                Infrastructure.Constants.Diagnostics.Reasons.MissingProviderBoundedPaging,
                "Route this Entity to a query-capable adapter that implements provider-bounded paging.", batchSize);

        query = query.WithoutPagination().WithCountStrategy(null);

        // Whether this provider can order this key is the provider's answer, taken below from its receipt,
        // not a guess made here from the CLR type. Refusing up front held every provider to what the weakest
        // one could do: ordering a stream by a string was refused on stores that order strings perfectly well,
        // and the caller was told to materialize — to load the whole set, which is the one thing a stream
        // exists to avoid. Paging integrity does not depend on the key's type either; it comes from a total
        // order, which the Entity Id tie-breaker below guarantees.
        //
        // What does vary by backend is what an ordering *means*: a string orders by the store's collation, a
        // null by the store's placement. That is worth saying, and it is said — see the fact recorded
        // below — rather than used as grounds for refusal.
        // Captured before the Id tie-breaker is appended, so the fact below reports the keys the caller chose
        // rather than the one Koan adds to every stream.
        var requestedSort = query.Sort;
        query = EnsureTotalOrder<TEntity, TKey>(query);
        if (query.Sort.Any(spec =>
                IsEntityIdentifier<TEntity, TKey>(spec.Path) &&
                !IsProviderStableIdentifier<TEntity, TKey>(spec.Path)))
            throw Reject<TEntity>(facts, provider,
                Infrastructure.Constants.Diagnostics.Reasons.UnsupportedStreamSort,
                "Use an Entity identifier shape with a proven provider-stable stream tie-breaker, or materialize the query explicitly.",
                batchSize);

        // Say once, per stream, which keys the store rather than Koan defines the order of. The ordering is
        // stable and complete either way; what changes with the backend is its meaning, and a caller comparing
        // runs across two stores deserves to find that in the facts instead of inferring it.
        RecordProviderDefinedOrder<TEntity, TKey>(facts, provider, requestedSort);

        var filterSupport = capabilities.Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;
        var (adapterBase, residual) = FilterPushdownCoordinator.Plan(query, filterSupport, typeof(TEntity));
        var residualPredicate = residual is null ? null : InMemoryFilterEvaluator.Compile<TEntity>(residual);

        var pageNumber = 1;
        var selectedRecorded = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var adapterPage = adapterBase
                .WithPagination(pageNumber, batchSize)
                .WithCountStrategy(null);
            RepositoryQueryResult<TEntity> result;
            using (enterContext())
                result = await DataQueryExecution<TEntity, TKey>
                    .QueryCandidates(repository, queryRepository, adapterPage, ct)
                    .ConfigureAwait(false);

            // Validate the complete candidate page before yielding from it. A provider cannot emit a
            // trustworthy prefix and then reveal that it ignored the requested bound or total order.
            if (!result.PaginationHandled)
                throw Reject<TEntity>(facts, provider,
                    Infrastructure.Constants.Diagnostics.Reasons.PaginationNotHandled,
                    "Use an adapter that applies the requested page in the provider before materialization.", batchSize);
            if (result.Items.Count > batchSize)
                throw Reject<TEntity>(facts, provider,
                    Infrastructure.Constants.Diagnostics.Reasons.StreamPageLimitExceeded,
                    $"The provider returned more than the requested {batchSize} candidates; correct or replace the adapter.",
                    batchSize);
            if (!result.SortFullyHandled(adapterPage))
                throw Reject<TEntity>(facts, provider,
                    Infrastructure.Constants.Diagnostics.Reasons.StreamSortNotHandled,
                    DescribeUnorderedKeys(provider, adapterPage.Sort
                        .Where(spec => !result.SortHandled.Contains(spec))
                        .ToArray()),
                    batchSize);
            try
            {
                QueryReceiptValidator.Validate(adapterPage, result);
            }
            catch (QueryReceiptRejectedException receipt)
            {
                throw Reject<TEntity>(facts, provider,
                    Infrastructure.Constants.Diagnostics.Reasons.InvalidStreamReceipt,
                    receipt.Correction,
                    batchSize);
            }

            if (!selectedRecorded)
            {
                Record<TEntity>(facts, provider, KoanFactState.Selected,
                    $"Selected provider-bounded paging for {typeof(TEntity).Name} with a maximum candidate page of {batchSize}.",
                    Infrastructure.Constants.Diagnostics.Reasons.ProviderBoundedPaging, null);
                selectedRecorded = true;
            }

            var candidateCount = result.Items.Count;
            foreach (var item in result.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (residualPredicate is null || residualPredicate(item))
                {
                    using (enterContext())
                    {
                        await DataQueryExecution<TEntity, TKey>
                            .MaterializeVisible(repository, item, ct)
                            .ConfigureAwait(false);
                        DataQueryExecution<TEntity, TKey>.ValidateEvidence(result.ReadEvidence, [item]);
                    }
                    yield return item;
                }
            }

            if (candidateCount < batchSize) yield break;

            // Qualified adapters currently express OFFSET/Skip as Int32. The next provider request
            // would calculate pageNumber * batchSize, so refuse before that multiplication can wrap.
            if (pageNumber == int.MaxValue || (long)pageNumber * batchSize > int.MaxValue)
                throw Reject<TEntity>(facts, provider,
                    Infrastructure.Constants.Diagnostics.Reasons.StreamPageLimitExceeded,
                    "Narrow the query; its numbered-page range exceeded the supported limit.", batchSize);
            pageNumber++;
        }
    }

    /// <summary>
    /// Explains, in the caller's terms, why the query could not be streamed in the order they asked for.
    ///
    /// <para>Streaming a sorted query is otherwise unremarkable — page after page of
    /// <c>ORDER BY … LIMIT … OFFSET …</c> — so what this message owes the reader is the specific reason this
    /// key was not among them, and it leads with that.</para>
    ///
    /// <para>The two reasons are not the same shape, so they do not get the same answer. A key naming a whole
    /// object has no ordering anywhere — no store can order by it and neither can the framework, whose sorter
    /// would fall back to comparing <c>ToString()</c> and produce a stable-looking nonsense. Recommending a
    /// materializing read there would be recommending that nonsense, so it is not offered; the fix is to name
    /// a value inside the object, and the message names one that exists. When instead the provider simply did
    /// not apply an otherwise orderable key, a materializing read genuinely does finish that ordering, and
    /// saying so beats telling the caller to sort it themselves.</para>
    /// </summary>
    private static string DescribeUnorderedKeys(string provider, IReadOnlyList<SortSpec> declined)
    {
        var unordered = declined.FirstOrDefault(static spec => !TypeClassification.IsSimple(spec.Path.ValueType));
        if (unordered is not null)
        {
            var type = unordered.Path.ValueType;
            var example = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.GetIndexParameters().Length == 0)
                .FirstOrDefault(static property => TypeClassification.IsSimple(property.PropertyType));
            var suggestion = example is null
                ? "a single value rather than a whole object"
                : $"{unordered.Path.DotPath}.{example.Name}";
            return $"'{unordered.Path.DotPath}' is a {type.Name}, which has no ordering of its own — no store " +
                   $"can order by a whole object, and neither can Koan. Order by {suggestion} instead.";
        }

        var names = string.Join("', '", declined.Select(static spec => spec.Path.DotPath));
        return $"'{provider}' did not apply the order '{names}', most often because it does not keep that value " +
               "somewhere it can compare. A stream is read one provider page at a time, so the provider has to " +
               "be the one that orders it — Koan never holds the whole result, and sorting a single page would " +
               "not put the sequence in order. Order by a value this provider can apply, route the Entity to " +
               "one that can, or read the query with All() or Page(), where Koan finishes the ordering itself " +
               "at the cost of materializing the result.";
    }

    /// <summary>
    /// Records which of the caller's order keys the store, rather than Koan, defines the ordering of.
    ///
    /// <para>Every key here still produces a stable, complete stream on the store it runs against. What it
    /// does not carry is a promise that the same key yields the same sequence somewhere else: a string orders
    /// by collation, a nullable column by the provider's null placement. Koan explains that instead of
    /// refusing it, so an application that genuinely needs cross-backend agreement can see which keys to
    /// avoid, and one that does not gets to stream by the key it actually wanted.</para>
    /// </summary>
    private static void RecordProviderDefinedOrder<TEntity, TKey>(
        IKoanRuntimeFactRecorder? facts,
        string provider,
        IReadOnlyList<SortSpec> sort)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        if (facts is null) return;
        var storeDefined = sort
            .Where(static spec => !TypeClassification.IsPortableStreamSortScalar(spec.Path.ValueType))
            .Select(static spec => spec.Path.DotPath)
            .ToArray();
        if (storeDefined.Length == 0) return;

        Record<TEntity>(facts, provider, KoanFactState.Selected,
            "Streaming " + typeof(TEntity).Name + " ordered by " + string.Join(", ", storeDefined) +
            ", whose comparison " + provider + " defines rather than Koan.",
            Infrastructure.Constants.Diagnostics.Reasons.StreamOrderIsProviderDefined,
            "The sequence is stable and complete on this store. Order by a number, enum, date or time if the " +
            "same sequence must hold on a different backend.");
    }

    private static QueryDefinition EnsureTotalOrder<TEntity, TKey>(QueryDefinition query)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        if (!query.HasSort)
            return query.WithSort<TEntity>(sort => sort.OrderBy(entity => entity.Id));

        var interfaceId = ExpressionMemberPath.From<TEntity, TKey>(entity => entity.Id).Members[0];
        var concreteId = AggregateMetadata.GetIdSpec(typeof(TEntity))?.Prop;
        var hasId = query.Sort.Any(spec =>
            !spec.Path.TraversesCollection &&
            spec.Path.Members.Count == 1 &&
            (spec.Path.Members[0].Equals(interfaceId) || spec.Path.Members[0].Equals(concreteId)));
        return hasId ? query : query.ThenBy<TEntity, TKey>(entity => entity.Id);
    }

    private static bool IsProviderStableIdentifier<TEntity, TKey>(MemberPath path)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        if (!IsEntityIdentifier<TEntity, TKey>(path)) return false;

        return IsProviderStableIdentifierType(path.ValueType);
    }

    // This floor is intentionally separate from caller-sort admission. The shared six-adapter
    // corpus proves Koan's normal string key as an opaque page tie-breaker; no custom key shape is
    // inferred merely because business fields of that CLR type have portable ordering.
    internal static bool IsProviderStableIdentifierType(Type type)
        => type == typeof(string);

    private static bool IsEntityIdentifier<TEntity, TKey>(MemberPath path)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        if (path.TraversesCollection || path.Members.Count != 1) return false;

        var member = path.Members[0];
        var interfaceId = ExpressionMemberPath.From<TEntity, TKey>(entity => entity.Id).Members[0];
        var concreteId = AggregateMetadata.GetIdSpec(typeof(TEntity))?.Prop;
        return member.Equals(interfaceId) || member.Equals(concreteId);
    }

    /// <summary>
    /// Record that a bulk consumer read this Entity with an explicitly materialized query because the
    /// provider advertises no bounded paging (DATA-0113). It is a selected strategy, not a rejection —
    /// but it is recorded on the same fact code as streaming so "how was this read?" has one answer.
    /// </summary>
    internal static void RecordMaterializedBulkRead<TEntity>(IKoanRuntimeFactRecorder? facts, string provider)
        => Record<TEntity>(facts, provider, KoanFactState.Selected,
            $"Materialized bulk read for {typeof(TEntity).Name}; the provider advertises no bounded paging.",
            Infrastructure.Constants.Diagnostics.Reasons.MaterializedBulkRead,
            "Route this Entity to an adapter that advertises provider-bounded paging to stream it instead.");

    private static QueryStreamRejectedException Reject<TEntity>(
        IKoanRuntimeFactRecorder? facts,
        string provider,
        string reason,
        string correction,
        int? batchSize)
    {
        Record<TEntity>(facts, provider, KoanFactState.Rejected,
            $"Rejected unbounded stream execution for {typeof(TEntity).Name}.", reason, correction);
        return new QueryStreamRejectedException(
            typeof(TEntity).FullName ?? typeof(TEntity).Name,
            provider,
            reason,
            correction,
            batchSize);
    }

    private static void Record<TEntity>(
        IKoanRuntimeFactRecorder? facts,
        string provider,
        KoanFactState state,
        string summary,
        string reason,
        string? correction)
    {
        if (facts is null) return;
        var entity = typeof(TEntity).FullName ?? typeof(TEntity).Name;
        var subject = $"stream:{entity}";
        facts.Record(new KoanFactDescriptor(
            Infrastructure.Constants.Diagnostics.Codes.StreamExecution,
            KoanFactKind.Capability,
            state,
            subject,
            summary,
            reason,
            correction,
            "Koan.Data.Core.Querying",
            $"{provider}:{entity}"));
    }
}
