using System.Globalization;

namespace Koan.Data.Core.Mapping.Runtime;

internal static class MappingValueConversion
{
    public static object? To(object? value, Type targetType)
    {
        var nullable = Nullable.GetUnderlyingType(targetType);
        var effective = nullable ?? targetType;
        if (value is null)
        {
            if (!targetType.IsValueType || nullable is not null) return null;
            throw new InvalidCastException($"Null cannot be assigned to '{targetType.FullName}'.");
        }
        if (effective.IsInstanceOfType(value)) return value;
        if (effective.IsEnum)
        {
            if (value is string text) return Koan.Data.Abstractions.EnumStorageEncoding.Parse(text, effective);
            return Enum.ToObject(effective, Convert.ChangeType(value, Enum.GetUnderlyingType(effective), CultureInfo.InvariantCulture)!);
        }
        if (effective == typeof(Guid) && value is string guid) return Guid.Parse(guid);
        if (effective == typeof(DateOnly) && value is string date) return DateOnly.Parse(date, CultureInfo.InvariantCulture);
        if (effective == typeof(TimeOnly) && value is string time) return TimeOnly.Parse(time, CultureInfo.InvariantCulture);
        if (effective == typeof(DateTimeOffset) && value is string offset)
            return DateTimeOffset.Parse(offset, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (effective == typeof(TimeSpan))
        {
            if (value is string span) return TimeSpan.Parse(span, CultureInfo.InvariantCulture);
            if (value is IConvertible ticks) return TimeSpan.FromTicks(ticks.ToInt64(CultureInfo.InvariantCulture));
        }
        if (effective == typeof(byte[]) && value is string binary) return Convert.FromBase64String(binary);
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(effective))
            return Convert.ChangeType(value, effective, CultureInfo.InvariantCulture);
        throw new InvalidCastException($"Value type '{value.GetType().FullName}' cannot be assigned to '{targetType.FullName}'.");
    }
}
