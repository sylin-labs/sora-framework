using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Koan.Data.Abstractions;

/// <summary>String representation shared by Entity persistence, mapping and query comparands.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class EnumStorageEncoding
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,
        IReadOnlyList<KeyValuePair<string, decimal>>> Orders = new();
    private static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new StringEnumConverter { AllowIntegerValues = false } }
    };

    /// <summary>Preserves declared names, EnumMember aliases and named flag combinations; rejects unnamed values.</summary>
    public static string Format(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JToken.FromObject(value, JsonSerializer.Create(Settings)).Value<string>()!;
    }

    /// <summary>Reads the same names used by persistence without treating a numeric string as an enum name.</summary>
    public static object Parse(string value, Type enumType)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(enumType);
        if (!enumType.IsEnum) throw new ArgumentException("An enum type is required.", nameof(enumType));
        return new JValue(value).ToObject(enumType, JsonSerializer.Create(Settings))!;
    }

    /// <summary>Declared name-to-rank mapping for native ordering without changing stored strings.</summary>
    public static IReadOnlyList<KeyValuePair<string, decimal>> OrderedValues(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        enumType = Nullable.GetUnderlyingType(enumType) ?? enumType;
        if (!enumType.IsEnum) throw new ArgumentException("An enum type is required.", nameof(enumType));
        if (enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
            throw new NotSupportedException("Native ordering of string-encoded flag combinations is not supported. Order by a separate numeric business field.");
        return Orders.GetOrAdd(enumType, static type => Array.AsReadOnly(Enum.GetValues(type).Cast<Enum>()
            .Select(value => new KeyValuePair<string, decimal>(Format(value),
                Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)))
            .DistinctBy(static pair => pair.Key).ToArray()));
    }
}
