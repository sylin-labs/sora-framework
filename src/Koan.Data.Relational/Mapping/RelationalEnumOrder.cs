using System.Globalization;
using Koan.Data.Abstractions;
using Koan.Data.Core;

namespace Koan.Data.Relational.Mapping;

/// <summary>Preserves CLR enum order while keeping the stored value a string.</summary>
public static class RelationalEnumOrder
{
    public static string Read(IRelationalMappingDialect dialect, MappingBindingPlan binding)
    {
        var expression = dialect.Read(binding.PhysicalPath, binding.Shape, binding.PhysicalType);
        var type = Nullable.GetUnderlyingType(binding.LogicalType) ?? binding.LogicalType;
        return type.IsEnum && binding.Descriptor.Codec is null ? Rank(expression, type) : expression;
    }

    public static string Rank(string expression, Type enumType)
    {
        var cases = EnumStorageEncoding.OrderedValues(enumType).Select(pair =>
            $"WHEN '{pair.Key.Replace("'", "''", StringComparison.Ordinal)}' THEN {pair.Value.ToString(CultureInfo.InvariantCulture)}");
        return $"(CASE {expression} {string.Join(" ", cases)} ELSE NULL END)";
    }
}
