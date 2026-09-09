using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sorting;

namespace Koan.Data.Connector.Couchbase.Runtime;

internal sealed class CouchbaseQueryCompiler<TEntity, TKey>(CouchbaseDocumentPlan<TEntity, TKey> entity)
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    internal CouchbaseQueryPlan Compile(QueryDefinition query, CouchbaseContainer container, int? forcedLimit = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var context = new ParameterContext();
        var where = query.Filter is null ? null : Filter(query.Filter, context);
        var order = Order(query.Sort);

        // A page is only a page of an ordered set. On the rare key SQL++ cannot express, Data finishes the
        // ordering over the whole result, so the window has to be taken there too — slicing here would hand
        // back an arbitrary few rows for Data to sort among themselves.
        var ordered = order.Handled.Count == query.Sort.Count;
        var limit = forcedLimit ?? (query.HasPagination && ordered ? query.EffectivePageSize() : null);
        var offset = query.HasPagination && ordered ? query.EffectiveOffset() : 0;
        return new CouchbaseQueryPlan(where, order.Sql, order.Handled, context.Parameters, limit, offset, ordered);
    }

    private string Filter(Filter filter, ParameterContext context) => filter switch
    {
        AllOf all when all.Operands.Count == 0 => "TRUE",
        AllOf all => Join("AND", all.Operands, context),
        AnyOf any when any.Operands.Count == 0 => "FALSE",
        AnyOf any => Join("OR", any.Operands, context),
        Not not => $"NOT ({Filter(not.Operand, context)})",
        FieldFilter field => Field(field, context),
        ClrFilter => throw new NotSupportedException("Couchbase received a CLR-only residual filter for native execution."),
        _ => throw new NotSupportedException($"Couchbase does not support filter node '{filter.GetType().Name}'.")
    };

    private string Join(
        string operation,
        IReadOnlyList<Filter> values,
        ParameterContext context)
    {
        var translated = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
            translated[index] = Filter(values[index], context);
        return "(" + string.Join($" {operation} ", translated) + ")";
    }

    private string Field(FieldFilter filter, ParameterContext context)
    {
        if (filter.IgnoreCase)
            throw new NotSupportedException("Couchbase case-insensitive filters are not claimed.");
        var resolved = FieldPathResolver.Resolve(typeof(TEntity), filter.Field);
        var path = "doc." + entity.Field(filter.Field, resolved, MappingConsumer.Filter);
        var values = filter.Value switch
        {
            FilterValue.Scalar scalar when filter.Operator == FilterOperator.Size =>
                new object?[] { Convert.ToInt32(scalar.Value, System.Globalization.CultureInfo.InvariantCulture) },
            FilterValue.Scalar scalar when filter.Operator == FilterOperator.Exists =>
                new object?[] { Convert.ToBoolean(scalar.Value, System.Globalization.CultureInfo.InvariantCulture) },
            FilterValue.Scalar scalar => new[] { entity.FilterValue(filter.Field, resolved, scalar.Value) },
            FilterValue.Set set => set.Values.Select(value => entity.FilterValue(filter.Field, resolved, value)).ToArray(),
            FilterValue.None => [],
            _ => throw new NotSupportedException("Couchbase received an unknown filter value shape.")
        };
        if (entity.UsesEnumNames(filter.Field, resolved, MappingConsumer.Filter) && filter.Operator is
            FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte)
        {
            if (values[0] is null) return "FALSE";
            path = CouchbaseDocumentPlan<TEntity, TKey>.EnumOrderValue(path, resolved.ComparableType);
            var enumeration = EnumStorageEncoding.Parse((string)values[0]!,
                Nullable.GetUnderlyingType(resolved.ComparableType) ?? resolved.ComparableType);
            values[0] = Convert.ToDecimal(enumeration, System.Globalization.CultureInfo.InvariantCulture);
        }
        return filter.Operator switch
        {
            FilterOperator.Eq when values[0] is null => $"{path} IS NULL",
            FilterOperator.Eq => $"{path} = {context.Add(values[0])}",
            FilterOperator.Ne when values[0] is null => $"{path} IS NOT NULL",
            FilterOperator.Ne => $"({path} IS NULL OR {path} != {context.Add(values[0])})",
            FilterOperator.Gt => $"{path} > {context.Add(values[0])}",
            FilterOperator.Gte => $"{path} >= {context.Add(values[0])}",
            FilterOperator.Lt => $"{path} < {context.Add(values[0])}",
            FilterOperator.Lte => $"{path} <= {context.Add(values[0])}",
            FilterOperator.In => $"{path} IN {context.Add(values)}",
            FilterOperator.Nin => $"({path} IS NULL OR {path} NOT IN {context.Add(values)})",
            FilterOperator.StartsWith => $"{path} LIKE {context.Add(Escape(values[0]) + "%")}",
            FilterOperator.EndsWith => $"{path} LIKE {context.Add("%" + Escape(values[0]))}",
            FilterOperator.Contains => $"{path} LIKE {context.Add("%" + Escape(values[0]) + "%")}",
            FilterOperator.Exists => Convert.ToBoolean(values[0]) ? $"{path} IS VALUED" : $"{path} IS NOT VALUED",
            FilterOperator.Has => $"{context.Add(values[0])} IN {path}",
            FilterOperator.HasAny => $"ANY elementValue IN {path} SATISFIES elementValue IN {context.Add(values)} END",
            FilterOperator.HasAll => $"EVERY elementValue IN {context.Add(values)} SATISFIES elementValue IN {path} END",
            FilterOperator.HasNone => $"EVERY elementValue IN {context.Add(values)} SATISFIES elementValue NOT IN {path} END",
            // Some element LIKE the escaped %value% pattern; N1QL's default LIKE escape is backslash,
            // the same escape the shared Escape helper emits. IFMISSINGORNULL coalesces a null/missing
            // array to an empty one — ANY over NULL yields NULL in N1QL's three-valued logic, which a
            // NOT() would not restore, so the null array must answer false before negation.
            FilterOperator.HasContains => $"ANY elementValue IN IFMISSINGORNULL({path}, []) SATISFIES elementValue LIKE {context.Add("%" + Escape(values[0]) + "%")} END",
            FilterOperator.Size => $"ARRAY_LENGTH({path}) = {context.Add(values[0])}",
            _ => throw new NotSupportedException($"Couchbase does not support operator '{filter.Operator}'.")
        };
    }

    /// <summary>
    /// Builds the ORDER BY, including keys that reach through a collection.
    ///
    /// <para>Such a key is an aggregate over a nested array, which SQL++ expresses directly, so Couchbase
    /// orders and pages the query rather than returning the collection for the framework to sort. Where the
    /// plan cannot express one — an explicit map, a second collection inside the path, positional
    /// First/Last — the whole ordering is declined instead of refused, and the framework completes it.
    /// Refusing was the earlier behavior, and it turned a query every other adapter answers into a 400.</para>
    ///
    /// <para>Declining is all or nothing: a partial ORDER BY is discarded by the sort that follows it, and
    /// reporting it as handled would let a page be taken against an ordering never fully applied.</para>
    /// </summary>
    private (string Sql, IReadOnlySet<SortSpec> Handled) Order(IReadOnlyList<SortSpec> sorts)
    {
        var values = new List<string>(sorts.Count + 1);
        foreach (var sort in sorts)
        {
            string value;
            if (sort.Path.TraversesCollection || sort.Aggregation != SortAggregation.None)
            {
                var aggregate = entity.CollectionOrderValue(sort.Path, sort.Aggregation);
                // META(doc).id alone still gives the scan a stable, repeatable order for the framework to sort.
                if (aggregate is null) return ("META(doc).id ASC", RepositoryQueryResult<TEntity>.NoSortHandled);
                value = aggregate;
            }
            else
            {
                var path = FieldPath.Of(sort.Path.Members.Select(static member => member.Name).ToArray());
                var resolved = FieldPathResolver.Resolve(typeof(TEntity), path);
                value = "doc." + entity.Field(path, resolved, MappingConsumer.Order);
                if (entity.UsesEnumNames(path, resolved, MappingConsumer.Order))
                    value = CouchbaseDocumentPlan<TEntity, TKey>.EnumOrderValue(value, resolved.ComparableType);
            }

            values.Add(value + (sort.Desc ? " DESC" : " ASC"));
        }

        values.Add("META(doc).id ASC");
        return (string.Join(", ", values), sorts.ToHashSet());
    }

    private static string Escape(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class ParameterContext
    {
        private int _ordinal;
        internal Dictionary<string, object?> Parameters { get; } = new();

        internal string Add(object? value)
        {
            var name = "p" + _ordinal++;
            Parameters.Add(name, value);
            return "$" + name;
        }
    }
}

internal sealed record CouchbaseQueryPlan(
    string? Where,
    string Order,
    IReadOnlySet<SortSpec> SortHandled,
    IReadOnlyDictionary<string, object?> Parameters,
    int? Limit,
    int Offset,
    // Whether SQL++ applied the caller's whole ordering, and so whether a page below it is real.
    bool Ordered = true);

internal readonly record struct CouchbaseContainer(string Scope, string Collection)
{
    internal string Qualified(string bucket) =>
        $"`{Escape(bucket)}`.`{Escape(Scope)}`.`{Escape(Collection)}`";

    private static string Escape(string value) => value.Replace("`", "``", StringComparison.Ordinal);
}
