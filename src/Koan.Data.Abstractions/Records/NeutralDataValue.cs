using System.Globalization;

namespace Koan.Data.Abstractions;

internal static class NeutralDataValue
{
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        bool or sbyte or byte or short or ushort or int or uint or long or ulong or
            float or double or decimal or string or Guid or DateOnly or TimeOnly or DateTime or
            DateTimeOffset or TimeSpan or DataObject or DataArray => value,
        byte[] bytes => bytes.ToArray(),
        Enum enumeration => EnumStorageEncoding.Format(enumeration),
        _ => throw new NeutralDataValueException(value.GetType())
    };

    public static object? ConvertTo(object? value, Type targetType, DataField field)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        var effective = underlying ?? targetType;
        if (value is null)
        {
            if (!targetType.IsValueType || underlying is not null) return null;
            throw new RecordValueConversionException(field, targetType, "The provider value is null.");
        }

        if (effective.IsInstanceOfType(value)) return value;
        try
        {
            if (effective.IsEnum)
            {
                if (value is string name) return EnumStorageEncoding.Parse(name, effective);
                if (IsInteger(value.GetType())) return Enum.ToObject(effective, value);
            }
            if (effective == typeof(Guid) && value is string guid) return Guid.Parse(guid);
            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(effective))
                return Convert.ChangeType(value, effective, CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new RecordValueConversionException(field, targetType, error.Message, error);
        }

        throw new RecordValueConversionException(
            field,
            targetType,
            $"Neutral value type '{value.GetType().FullName}' is not convertible.");
    }

    private static bool IsInteger(Type type) =>
        type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) ||
        type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong);
}
