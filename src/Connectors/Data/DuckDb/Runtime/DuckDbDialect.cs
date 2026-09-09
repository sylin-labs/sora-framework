using Koan.Data.Abstractions;
using Koan.Data.Relational.Mapping;

namespace Koan.Data.Connector.DuckDb.Runtime;

/// <summary>
/// DuckDB's SQL is Postgres-shaped: quoted identifiers, <c>$name</c> parameters, and NULLS LAST on
/// ascending order — the opposite of SQLite's NULLS FIRST, which the framework's sorter assumes, so
/// every ordered term this dialect produces states its null placement explicitly.
/// </summary>
internal sealed class DuckDbDialect : IRelationalMappingDialect
{
    public string QuoteIdent(string ident) => Quote(ident);
    public string EscapeLike(string fragment) => fragment.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
    public string Parameter(int index) => $"$p{index}";
    public string JsonArrayContains(string columnSql, string parameter) =>
        $"EXISTS (SELECT 1 FROM json_each({columnSql}, '$') AS koan_element WHERE koan_element.value = to_json({parameter}))";
    public string JsonArrayLength(string columnSql) => $"json_array_length({columnSql})";
    // json_each yields JSON values, so the element is extracted to text (->>) first; DuckDB's LIKE is
    // case-sensitive, so the escaped pattern rides as-is.
    public string JsonArrayElementLike(string columnSql, string patternParameter, string literalParameter) =>
        $"EXISTS (SELECT 1 FROM json_each({columnSql}, '$') AS koan_element WHERE (koan_element.value ->> '$') LIKE {patternParameter} ESCAPE '\\')";

    public string Read(PhysicalPath path, MappingValueShape shape, Type physicalType)
    {
        var root = $"koan_row.{Quote(path.Name)}";
        if (!path.IsNested) return root;
        var pathText = JsonPath(path.Segments);
        var type = Nullable.GetUnderlyingType(physicalType) ?? physicalType;
        // json_extract answers JSON; ->> answers text. Numeric, enum and boolean reads cast the JSON value
        // so a comparison with an encoded comparand happens between like kinds — and an Object-shaped read
        // (a collection path handed back for array aggregation) must stay JSON, because json_each walks it.
        return shape == MappingValueShape.Object
            ? $"({root} -> '{pathText}')"
            : IsNumeric(type) || type == typeof(TimeSpan)
                ? $"CAST({root} -> '{pathText}' AS DOUBLE)"
                : type == typeof(bool)
                    ? $"CAST({root} -> '{pathText}' AS BOOLEAN)"
                    : $"({root} ->> '{pathText}')";
    }

    public string? JsonArrayOrderTerm(
        string arraySql,
        IReadOnlyList<string> elementSegments,
        bool max,
        bool descending,
        Type elementValueType)
    {
        var pathText = JsonPath(elementSegments);
        var type = Nullable.GetUnderlyingType(elementValueType) ?? elementValueType;
        var extracted = $"json_extract(koan_element.value, '{pathText}')";
        var value = IsNumeric(type) || type == typeof(TimeSpan)
            ? $"CAST({extracted} AS DOUBLE)"
            : type == typeof(bool)
                ? $"CAST({extracted} AS BOOLEAN)"
                : $"json_extract_string(koan_element.value, '{pathText}')";
        if (type.IsEnum) value = RelationalEnumOrder.Rank(value, type);
        // json_each rejects a scalar, and a document may hold no array at that path at all, so the type is
        // checked rather than assumed. No rows means NULL, which the framework's sorter puts first on an
        // ascending read — DuckDB's default is the opposite, so the placement is spelled out.
        var array = $"CASE WHEN json_type({arraySql}) = 'ARRAY' THEN {arraySql} ELSE '[]' END";
        var aggregate = $"(SELECT {(max ? "MAX" : "MIN")}({value}) FROM json_each({array}, '$') AS koan_element)";
        return descending ? $"{aggregate} DESC NULLS LAST" : $"{aggregate} ASC NULLS FIRST";
    }

    internal static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static string JsonPath(IEnumerable<string> segments) => "$" + string.Concat(segments.Select(segment =>
        ".\"" + segment.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));

    private static bool IsNumeric(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);
}
