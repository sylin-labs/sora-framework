using System.Collections.Frozen;
using System.Globalization;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sorting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Koan.Data.Connector.Mongo.Runtime;

internal sealed class MongoQueryCompiler<TEntity, TKey>(MongoEntityPlan<TEntity, TKey> entity,
    Func<CounterpartQueryTarget, string>? resolveCounterpart = null)
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    public MongoQueryPlan Compile(QueryDefinition query, int? hardLimit = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var relational = Filter.HasCounterpart(query.Filter);
        var prefix = relational ? new List<BsonDocument>
        {
            new("$replaceRoot", new BsonDocument("newRoot", new BsonDocument(Envelope, "$$ROOT")))
        } : null;
        if (relational && MandatoryRowPredicate(query.Filter!) is { } mandatory)
            prefix!.Insert(0, new BsonDocument("$match", Visit(mandatory)));
        var leaves = new Dictionary<BoundSameIdInFilter, string>();
        BsonDocument Counterpart(BoundSameIdInFilter bound)
        {
            if (Filter.HasCounterpart(bound.Predicate))
                throw new NotSupportedException("MongoDB nested counterpart predicates are unsupported.");
            if (!leaves.TryGetValue(bound, out var slot))
            {
                var collection = resolveCounterpart?.Invoke(bound.Target)
                    ?? throw new NotSupportedException("MongoDB counterpart target has not been bound by Data.");
                var inner = Visit(bound.Predicate);
                slot = CounterpartField + leaves.Count.ToString(CultureInfo.InvariantCulture);
                leaves.Add(bound, slot);
                prefix!.Add(new BsonDocument("$lookup", new BsonDocument
                {
                    ["from"] = collection,
                    ["let"] = new BsonDocument("identity", "$" + Envelope + "._id"),
                    ["pipeline"] = new BsonArray
                    {
                        new BsonDocument("$match", new BsonDocument("$expr",
                            new BsonDocument("$eq", new BsonArray { "$_id", "$$identity" }))),
                        new BsonDocument("$match", inner),
                        new BsonDocument("$limit", 1),
                        new BsonDocument("$project", new BsonDocument("_id", 1))
                    },
                    ["as"] = slot
                }));
            }
            return new BsonDocument(slot + ".0", new BsonDocument("$exists", true));
        }
        var filter = query.Filter is null ? new BsonDocument()
            : Visit(query.Filter, relational ? Envelope + "." : "", relational ? Counterpart : null);
        if (prefix is not null) prefix.Add(new BsonDocument("$match", filter));
        var handledSort = Sort(query.Sort, out var sort, out var computed, out var sortDocument, relational);
        var canPage = handledSort.Count == query.Sort.Count;
        if (relational && !canPage)
            throw new NotSupportedException("MongoDB counterpart queries require a fully native sort before reading candidates.");
        var limit = hardLimit ?? (query.HasPagination && canPage ? query.EffectivePageSize() : (int?)null);
        var skip = hardLimit is null && query.HasPagination && canPage ? query.EffectiveOffset() : 0;
        return new MongoQueryPlan(
            filter,
            sort,
            skip,
            limit,
            true,
            handledSort.ToFrozenSet(),
            query.HasPagination && canPage,
            query.CountStrategy is null ? CountExecutionKind.None : CountExecutionKind.Exact,
            filter,
            computed,
            sortDocument,
            prefix);
    }

    public FilterDefinition<BsonDocument> Predicate(Filter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Visit(filter);
    }

    private BsonDocument Visit(Filter filter, string prefix = "", Func<BoundSameIdInFilter, BsonDocument>? counterpart = null) => filter switch
    {
        AllOf all => Logical("$and", all.Operands, true, prefix, counterpart),
        AnyOf any => Logical("$or", any.Operands, false, prefix, counterpart),
        Not not => new BsonDocument("$nor", new BsonArray([Visit(not.Operand, prefix, counterpart)])),
        FieldFilter field => Field(field, prefix),
        BoundSameIdInFilter bound when counterpart is not null => counterpart(bound),
        SameIdInFilter or BoundSameIdInFilter => throw new NotSupportedException("MongoDB counterpart nodes require Data binding and a qualified query pipeline."),
        ClrFilter => throw new NotSupportedException("MongoDB received a CLR residual instead of a pushable filter."),
        _ => throw new NotSupportedException($"MongoDB does not support filter node '{filter.GetType().Name}'.")
    };

    private BsonDocument Logical(string operation, IReadOnlyList<Filter> operands, bool matchAll,
        string prefix, Func<BoundSameIdInFilter, BsonDocument>? counterpart)
    {
        if (operands.Count == 0)
            return matchAll ? new BsonDocument() : new BsonDocument("$expr", false);
        if (operands.Count == 1) return Visit(operands[0], prefix, counterpart);
        return new BsonDocument(operation, new BsonArray(operands.Select(operand => Visit(operand, prefix, counterpart))));
    }

    private BsonDocument Field(FieldFilter filter, string prefix)
    {
        if (filter.IgnoreCase)
            throw new NotSupportedException("MongoDB case-insensitive filter pushdown was not declared.");
        var resolved = FieldPathResolver.Resolve(typeof(TEntity), filter.Field);
        var logicalPath = resolved.CanonicalPath ?? filter.Field;
        var path = prefix + entity.Field(logicalPath, resolved, MappingConsumer.Filter);
        var scalar = filter.Value is FilterValue.Scalar one ? one.Value : null;
        var set = filter.Value switch
        {
            FilterValue.Set many => many.Values,
            FilterValue.Scalar single => [single.Value],
            _ => Array.Empty<object?>()
        };
        BsonValue Value(object? value) => entity.FilterValue(logicalPath, resolved, value);
        BsonArray Values() => new(set.Select(Value));

        if (entity.UsesEnumNames(logicalPath, resolved) && filter.Operator is
            FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte)
        {
            if (scalar is null) return new BsonDocument("$expr", false);
            var operation = filter.Operator switch
            {
                FilterOperator.Gt => "$gt", FilterOperator.Gte => "$gte",
                FilterOperator.Lt => "$lt", _ => "$lte"
            };
            var rank = MongoEntityPlan<TEntity, TKey>.EnumOrderExpression("$" + path, resolved.ComparableType);
            var value = FilterValueConverter.Convert(scalar, resolved.ComparableType);
            _ = EnumStorageEncoding.Format((Enum)value!);
            return new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$ne", new BsonArray { rank, BsonNull.Value }),
                new BsonDocument(operation, new BsonArray { rank,
                    new BsonDecimal128(Convert.ToDecimal(value, CultureInfo.InvariantCulture)) })
            }));
        }

        return filter.Operator switch
        {
            FilterOperator.Eq => new BsonDocument(path, Value(scalar)),
            FilterOperator.Ne when scalar is null => new BsonDocument("$and", new BsonArray([
                new BsonDocument(path, new BsonDocument("$exists", true)),
                new BsonDocument(path, new BsonDocument("$ne", BsonNull.Value))
            ])),
            FilterOperator.Ne => new BsonDocument(path, new BsonDocument("$ne", Value(scalar))),
            FilterOperator.Gt => Compare(path, "$gt", Value(scalar)),
            FilterOperator.Gte => Compare(path, "$gte", Value(scalar)),
            FilterOperator.Lt => Compare(path, "$lt", Value(scalar)),
            FilterOperator.Lte => Compare(path, "$lte", Value(scalar)),
            FilterOperator.In => Compare(path, "$in", Values()),
            FilterOperator.Nin when set.Any(static value => value is null) => new BsonDocument("$and", new BsonArray([
                new BsonDocument(path, new BsonDocument("$exists", true)),
                Compare(path, "$nin", Values())
            ])),
            FilterOperator.Nin => Compare(path, "$nin", Values()),
            FilterOperator.StartsWith => RegexFilter(path, $"^{System.Text.RegularExpressions.Regex.Escape((string)scalar!)}"),
            FilterOperator.EndsWith => RegexFilter(path, $"{System.Text.RegularExpressions.Regex.Escape((string)scalar!)}$"),
            FilterOperator.Contains => RegexFilter(path, System.Text.RegularExpressions.Regex.Escape((string)scalar!)),
            FilterOperator.Exists => Exists(path, scalar as bool? ?? true),
            FilterOperator.Has => new BsonDocument(path, Value(scalar)),
            FilterOperator.HasAny => Compare(path, "$in", Values()),
            FilterOperator.HasAll => Compare(path, "$all", Values()),
            FilterOperator.HasNone => Compare(path, "$nin", Values()),
            // Some string element matches the (regex-escaped, case-sensitive) literal — an element
            // substring, not a pattern. Null/missing/empty arrays fail $elemMatch, matching the floor.
            FilterOperator.HasContains => ElemMatch(path, System.Text.RegularExpressions.Regex.Escape((string)scalar!)),
            FilterOperator.Size => Compare(
                path,
                "$size",
                MongoValues.FromNeutral(Convert.ToInt32(scalar, CultureInfo.InvariantCulture))),
            _ => throw new NotSupportedException(
                $"MongoDB does not support '{filter.Operator}' for field '{filter.Field}'.")
        };
    }

    /// <summary>
    /// Resolves the requested order into something MongoDB can apply.
    ///
    /// <para>Most keys are fields, which <c>find</c> sorts directly. A key that reaches through a collection is
    /// an aggregate over a nested array, which <c>find</c> cannot sort by at all — so the compiler emits the
    /// expression as an added field and the repository runs the query as a pipeline instead. Either way the
    /// server does the ordering; the alternative is returning the whole collection for the framework to sort.
    /// </para>
    ///
    /// <para>All or nothing, as before: a partial order is discarded by the sort that follows it, and reporting
    /// it as handled would let a page be taken against an order never fully applied.</para>
    /// </summary>
    private IReadOnlyList<SortSpec> Sort(
        IReadOnlyList<SortSpec> requested,
        out SortDefinition<BsonDocument>? definition,
        out BsonDocument? computed,
        out BsonDocument? sortDocument,
        bool enveloped = false)
    {
        definition = null;
        computed = null;
        sortDocument = null;
        if (requested.Count == 0)
        {
            if (enveloped) sortDocument = new BsonDocument(Envelope + "._id", 1);
            return [];
        }

        var parts = new List<SortDefinition<BsonDocument>>(requested.Count);
        var order = new BsonDocument();
        var added = new BsonDocument();
        var handled = new List<SortSpec>(requested.Count);
        foreach (var sort in requested)
        {
            string name;
            var computedKey = false;
            if (sort.Path.TraversesCollection || sort.Aggregation != SortAggregation.None)
            {
                var expression = entity.CollectionOrderExpression(sort.Path, sort.Aggregation);
                if (expression is null) break;
                // Counterpart pipelines place computed slots beside the original document envelope.
                name = ComputedOrderField + handled.Count.ToString(CultureInfo.InvariantCulture);
                computedKey = true;
                added[name] = enveloped ? EnvelopeExpression(expression) : expression;
            }
            else
            {
                var path = FieldPath.Of(sort.Path.Members.Select(static member => member.Name).ToArray());
                var resolved = FieldPathResolver.Resolve(typeof(TEntity), path);
                name = entity.Field(path, resolved, MappingConsumer.Order);
                if (entity.UsesEnumNames(path, resolved, MappingConsumer.Order))
                {
                    var expression = MongoEntityPlan<TEntity, TKey>.EnumOrderExpression("$" + name, resolved.ComparableType);
                    name = ComputedOrderField + handled.Count.ToString(CultureInfo.InvariantCulture);
                    computedKey = true;
                    added[name] = enveloped ? EnvelopeExpression(expression) : expression;
                }
            }

            if (enveloped && !computedKey) name = Envelope + "." + name;
            parts.Add(sort.Desc
                ? Builders<BsonDocument>.Sort.Descending(name)
                : Builders<BsonDocument>.Sort.Ascending(name));
            order[name] = sort.Desc ? -1 : 1;
            handled.Add(sort);
        }

        if (handled.Count != requested.Count) return [];

        if (enveloped && !order.Contains(Envelope + "._id")) order[Envelope + "._id"] = 1;
        if (enveloped || added.ElementCount > 0)
        {
            computed = added.ElementCount > 0 ? added : null;
            sortDocument = order;
            return handled;
        }

        definition = Builders<BsonDocument>.Sort.Combine(parts);
        return handled;
    }

    /// <summary>Prefix for the fields a pipeline adds to hold a collection aggregate while it sorts.</summary>
    internal const string ComputedOrderField = "__koanOrder";
    internal const string Envelope = "__koanRow";
    internal const string CounterpartField = "__koanCounterpart";

    // Only a whole row-only subtree or an AND operand is mandatory. Pulling a relational OR/NOT arm
    // forward would change eligibility. Keep the complete final predicate even after this optimization.
    private static Filter? MandatoryRowPredicate(Filter filter)
    {
        if (!Filter.HasCounterpart(filter)) return filter;
        if (filter is not AllOf all) return null;
        Filter? result = null;
        foreach (var operand in all.Operands) result = Filter.And(result, MandatoryRowPredicate(operand));
        return result;
    }

    private static BsonValue EnvelopeExpression(BsonValue value) => value switch
    {
        BsonString text when text.Value.StartsWith("$", StringComparison.Ordinal) &&
                             !text.Value.StartsWith("$$", StringComparison.Ordinal) =>
            new BsonString("$" + Envelope + "." + text.Value[1..]),
        BsonDocument document => new BsonDocument(document.Elements.Select(element =>
            new BsonElement(element.Name, element.Name == "$literal" ? element.Value : EnvelopeExpression(element.Value)))),
        BsonArray array => new BsonArray(array.Select(EnvelopeExpression)),
        _ => value
    };

    private static BsonDocument Compare(string path, string operation, BsonValue value) =>
        new(path, new BsonDocument(operation, value));

    private static BsonDocument RegexFilter(string path, string pattern) =>
        new(path, new BsonRegularExpression(pattern));

    private static BsonDocument ElemMatch(string path, string pattern) =>
        new(path, new BsonDocument("$elemMatch", new BsonDocument("$regex", pattern)));

    private static BsonDocument Exists(string path, bool desired) => desired
        ? new BsonDocument("$and", new BsonArray([
            new BsonDocument(path, new BsonDocument("$exists", true)),
            new BsonDocument(path, new BsonDocument("$ne", BsonNull.Value))
        ]))
        : new BsonDocument("$or", new BsonArray([
            new BsonDocument(path, new BsonDocument("$exists", false)),
            new BsonDocument(path, BsonNull.Value)
        ]));
}

internal sealed record MongoQueryPlan(
    FilterDefinition<BsonDocument> Filter,
    SortDefinition<BsonDocument>? Sort,
    int Skip,
    int? Limit,
    bool FilterHandled,
    IReadOnlySet<SortSpec> SortHandled,
    bool PaginationHandled,
    CountExecutionKind CountExecution,
    // The same filter as a document, and the fields a collection order key needs computed before the sort.
    // When Computed is present the query runs as a pipeline, because find cannot sort by an expression.
    BsonDocument FilterDocument,
    BsonDocument? Computed,
    BsonDocument? SortDocument,
    IReadOnlyList<BsonDocument>? Prefix = null);
