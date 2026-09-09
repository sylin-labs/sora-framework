using Koan.Data.Abstractions;
using Koan.Data.Relational.Mapping;

namespace Koan.Data.Connector.MySql.Runtime;

internal sealed class MySqlDialect : IRelationalMappingDialect
{
    public string Read(PhysicalPath path, MappingValueShape shape, Type physicalType)
    {
        var root = Quote(path.Name);
        if (!path.IsNested) return root;
        var extracted = $"JSON_EXTRACT({root}, '{JsonPath(path.Segments)}')";
        if (shape == MappingValueShape.Object || IsCollection(physicalType)) return extracted;
        return Cast(Unquote(extracted), physicalType);
    }

    public string? JsonArrayOrderTerm(
        string arraySql,
        IReadOnlyList<string> elementSegments,
        bool max,
        bool descending,
        Type elementValueType)
    {
        var value = Cast(
            Unquote($"JSON_EXTRACT(koan_element.koan_value, '{JsonPath(elementSegments)}')"),
            elementValueType);
        if ((Nullable.GetUnderlyingType(elementValueType) ?? elementValueType).IsEnum)
            value = RelationalEnumOrder.Rank(value, elementValueType);
        // JSON_TABLE refuses anything but an array, and a document may hold no array at that path at all, so
        // the type is checked rather than assumed. No rows means NULL, which sorts first — where the
        // in-memory sorter puts a widget with no sightings.
        var array = $"CASE WHEN JSON_TYPE({arraySql}) = 'ARRAY' THEN {arraySql} ELSE JSON_ARRAY() END";
        var aggregate = $"(SELECT {(max ? "MAX" : "MIN")}({value}) " +
                        $"FROM JSON_TABLE({array}, '$[*]' COLUMNS (koan_value JSON PATH '$')) AS koan_element)";
        // MySQL sorts NULL first ascending and last descending, which is where the framework's sorter puts
        // it, so the direction alone is enough.
        return descending ? $"{aggregate} DESC" : $"{aggregate} ASC";
    }

    public string QuoteIdent(string ident) => Quote(ident);
    public string EscapeLike(string fragment) => fragment.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
    public string Parameter(int index) => $"@p{index}";
    public string JsonArrayContains(string columnSql, string parameter) =>
        $"COALESCE(JSON_CONTAINS({columnSql}, JSON_ARRAY({parameter})), 0)";
    public string JsonArrayLength(string columnSql) => $"COALESCE(JSON_LENGTH({columnSql}), 0)";
    public string JsonArrayElementLike(string columnSql, string patternParameter, string literalParameter) =>
        // JSON_SEARCH walks the whole document with LIKE semantics (% and _ wildcards, backslash escape
        // by default — the same escape EscapeLike emits) and compares with JSON's binary collation, so
        // the case-sensitive posture holds without coercing the column. NULL (no match, null column,
        // or non-array document) reads as no match.
        $"(JSON_SEARCH({columnSql}, 'one', {patternParameter}) IS NOT NULL)";

    public static string Quote(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";

    public static string JsonPath(IEnumerable<string> segments)
    {
        var path = "$" + string.Concat(segments.Select(segment => ".\"" +
            segment.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
        return path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a JSON scalar as SQL, keeping a JSON null a null.
    ///
    /// <para><c>JSON_UNQUOTE</c> renders a JSON null as the four-character string <c>null</c>. Cast to a number
    /// that raises "Truncated incorrect INTEGER value: 'null'", which made this store unable to write an entity
    /// whose nullable scalar was null - the value reaches a generated column on every insert - and unable to
    /// filter or order across one. <c>JSON_TYPE</c> separates a JSON null from a string that happens to read
    /// "null", so a real value spelled that way still round-trips (PMC-038).</para>
    /// </summary>
    internal static string Unquote(string extracted) =>
        $"CASE WHEN JSON_TYPE({extracted}) = 'NULL' THEN NULL ELSE JSON_UNQUOTE({extracted}) END";

    internal static string Cast(string expression, Type type)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (value.IsEnum) value = typeof(string);
        if (value == typeof(bool))
            return $"CASE LOWER({expression}) WHEN 'true' THEN 1 WHEN 'false' THEN 0 ELSE CAST({expression} AS UNSIGNED) END";
        if (value == typeof(byte) || value == typeof(ushort) ||
            value == typeof(uint) || value == typeof(ulong)) return $"CAST({expression} AS UNSIGNED)";
        if (value == typeof(sbyte) || value == typeof(short) || value == typeof(int) ||
            value == typeof(long) || value == typeof(TimeSpan)) return $"CAST({expression} AS SIGNED)";
        if (value == typeof(float) || value == typeof(double) || value == typeof(decimal))
            return $"CAST({expression} AS DECIMAL(65, 30))";
        return expression;
    }

    private static bool IsCollection(Type type) =>
        type != typeof(string) && type != typeof(byte[]) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
}
