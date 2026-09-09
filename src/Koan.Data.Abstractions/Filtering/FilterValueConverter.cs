using System.Globalization;

namespace Koan.Data.Abstractions.Filtering;

/// <summary>
/// Coerces a raw filter value (a CLR primitive already extracted from the wire by the
/// front-end parser) to a target leaf type: enum-by-name, Guid, DateTime/DateTimeOffset
/// (ISO/round-trip), bool, and numeric widening. Fails loud — an un-coercible value throws
/// <see cref="FormatException"/> rather than silently degrading (the old
/// <c>Convert.ChangeType(s, …)</c> catch-all masked the original bug behind a cast error).
/// </summary>
public static class FilterValueConverter
{
    public static object? Convert(object? raw, Type targetType)
    {
        var target = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (raw is null) return null;
        if (target.IsInstanceOfType(raw)) return raw;

        if (raw is string s)
        {
            if (target.IsEnum) return EnumStorageEncoding.Parse(s, target);
            if (target == typeof(Guid)) return Guid.Parse(s);
            if (target == typeof(DateTime)) return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (target == typeof(DateTimeOffset)) return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (target == typeof(TimeSpan)) return TimeSpan.Parse(s, CultureInfo.InvariantCulture);
            if (target == typeof(bool)) return bool.Parse(s);
            if (typeof(IConvertible).IsAssignableFrom(target))
                return System.Convert.ChangeType(s, target, CultureInfo.InvariantCulture);
        }

        if (target.IsEnum && raw is IConvertible) return Enum.ToObject(target, raw);

        if (raw is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
            return System.Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);

        throw new FormatException(
            $"Cannot coerce filter value '{raw}' ({raw.GetType().Name}) to '{target.Name}'.");
    }
}
