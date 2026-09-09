using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Koan.Data.Core.Direct;

internal static class JsonSettings
{
    public static readonly JsonSerializerSettings Default = new()
    {
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.DateTimeOffset,
        FloatParseHandling = FloatParseHandling.Decimal,
        Culture = System.Globalization.CultureInfo.InvariantCulture,
        Converters = { new StringEnumConverter { AllowIntegerValues = false } }
    };
}
