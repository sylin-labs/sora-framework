using Koan.Data.Abstractions;
using Koan.Data.Relational.Mapping;

namespace Koan.Data.Connector.SqlServer.Runtime;

internal sealed class SqlServerDialect : IRelationalMappingDialect
{
    public string Read(PhysicalPath path, MappingValueShape shape, Type physicalType)
    {
        var root = Quote(path.Name);
        if (!path.IsNested) return root;
        var jsonPath = JsonPath(path.Segments);
        if (shape == MappingValueShape.Object || IsCollection(physicalType))
            return $"JSON_QUERY({root}, '{jsonPath}')";
        return Cast($"JSON_VALUE({root}, '{jsonPath}')", physicalType);
    }

    public string? JsonArrayOrderTerm(
        string arraySql,
        IReadOnlyList<string> elementSegments,
        bool max,
        bool descending,
        Type elementValueType)
    {
        var value = Cast($"JSON_VALUE(koan_element.[value], '{JsonPath(elementSegments)}')", elementValueType);
        if ((Nullable.GetUnderlyingType(elementValueType) ?? elementValueType).IsEnum)
            value = RelationalEnumOrder.Rank(value, elementValueType);
        // JSON_QUERY already returns NULL for anything that is not an object or array, so an absent path
        // becomes an empty array rather than an error. No rows means NULL, which sorts first.
        var aggregate = $"(SELECT {(max ? "MAX" : "MIN")}({value}) FROM OPENJSON(ISNULL({arraySql}, N'[]')) AS koan_element)";
        // SQL Server sorts NULL first ascending and last descending, which is where the framework's sorter
        // puts it, so the direction alone is enough.
        return descending ? $"{aggregate} DESC" : $"{aggregate} ASC";
    }

    public string QuoteIdent(string ident) => Quote(ident);
    public string EscapeLike(string fragment) => fragment.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    public string Parameter(int index) => $"@p{index}";
    public string JsonArrayContains(string columnSql, string parameter) =>
        $"EXISTS (SELECT 1 FROM OPENJSON({columnSql}) item WHERE item.[value] = {parameter})";
    public string JsonArrayLength(string columnSql) => $"(SELECT COUNT(1) FROM OPENJSON({columnSql}))";
    public string JsonArrayElementLike(string columnSql, string patternParameter, string literalParameter) =>
        // This dialect escapes LIKE metacharacters by bracket-quoting ([%], [_], [[]), which needs no
        // ESCAPE declaration — and declaring one would make the escape swallow a literal backslash in
        // the pattern. The default server collation folds case, so the comparison is forced binary to
        // answer what the case-sensitive floor answers; ISNULL mirrors JsonArrayOrderTerm's null guard.
        $"EXISTS (SELECT 1 FROM OPENJSON(ISNULL({columnSql}, N'[]')) item WHERE item.[value] COLLATE Latin1_General_BIN2 LIKE {patternParameter})";

    public static string Quote(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    public static string JsonPath(IEnumerable<string> segments) =>
        "$" + string.Concat(segments.Select(segment => ".\"" + segment.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));

    private static string Cast(string expression, Type type)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (value.IsEnum) value = typeof(string);
        if (value == typeof(bool)) return $"TRY_CONVERT(bit, {expression})";
        if (value == typeof(byte) || value == typeof(sbyte) || value == typeof(short) || value == typeof(ushort) ||
            value == typeof(int) || value == typeof(uint) || value == typeof(long) || value == typeof(ulong) ||
            value == typeof(TimeSpan)) return $"TRY_CONVERT(bigint, {expression})";
        if (value == typeof(float) || value == typeof(double) || value == typeof(decimal))
            return $"TRY_CONVERT(decimal(38, 10), {expression})";
        return expression;
    }

    private static bool IsCollection(Type type) =>
        type != typeof(string) && type != typeof(byte[]) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
}
