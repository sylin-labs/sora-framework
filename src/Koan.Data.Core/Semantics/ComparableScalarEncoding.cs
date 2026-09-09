using System.Globalization;
using Koan.Data.Core.Polymorphism;
using Koan.Data.Core.Semantics;
using Newtonsoft.Json;

namespace Koan.Data.Core;

/// <summary>
/// Canonical, order-preserving encodings for the composite scalars governed by the comparable-encoding
/// contract (DATA-0100), for any adapter that persists an entity as a JSON document and resolves a filter by
/// extracting JSON text/number. BOTH the write path (the <see cref="JsonConverters"/> here) and the filter
/// comparand (<see cref="EncodeComparand"/>) use the SAME canonical form, so a pushed comparison compares
/// like-for-like:
/// <list type="bullet">
///   <item><see cref="DateTimeOffset"/> → UTC-normalised fixed-width ISO-8601 TEXT (<c>…Z</c>). The
///   offset is NOT persisted (DATA-0100); two values with the same instant collapse to the same text, so
///   equality and range comparison are instant-correct and the text sorts chronologically.</item>
///   <item><see cref="TimeSpan"/> → <see cref="long"/> ticks (a JSON number); the column is cast to a
///   numeric type for comparison so it sorts by duration (not by the broken <c>"1.00:00:00"</c> string).</item>
///   <item><see cref="DateOnly"/> → <c>yyyy-MM-dd</c> TEXT, <see cref="TimeOnly"/> → <c>HH:mm:ss.fffffff</c>
///   TEXT — both fixed-width and monotonic. Also required because the ADO.NET drivers cannot bind a CLR
///   <see cref="DateOnly"/>/<see cref="TimeOnly"/> as a parameter at all (they throw); encoding the
///   comparand to text sidesteps that and matches the stored text.</item>
/// </list>
/// </summary>
public static class ComparableScalarEncoding
{
    // Fixed-width, invariant, UTC. 7 fractional digits == DateTime tick resolution -> lossless round-trip.
    private const string DateTimeOffsetUtcFormat = "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";
    private const string DateOnlyFormat = "yyyy-MM-dd";
    private const string TimeOnlyFormat = "HH:mm:ss.fffffff";

    public static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString(DateTimeOffsetUtcFormat, CultureInfo.InvariantCulture);

    public static string Format(DateOnly value) => value.ToString(DateOnlyFormat, CultureInfo.InvariantCulture);
    public static string Format(TimeOnly value) => value.ToString(TimeOnlyFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps a filter comparand to the same canonical store form the write path produces, so a pushed
    /// comparison is like-for-like. Types the contract does not govern pass through unchanged.
    /// </summary>
    public static object? EncodeComparand(object? value) => value switch
    {
        DateTimeOffset dto => Format(dto),
        TimeSpan ts => ts.Ticks,
        DateOnly d => Format(d),
        TimeOnly t => Format(t),
        Enum enumeration => Koan.Data.Abstractions.EnumStorageEncoding.Format(enumeration),
        _ => value,
    };

    // The converters are stateless and thread-safe, so they are shared singletons — re-allocating them on
    // every settings build would be per-repository-instance waste on the (hot) serialization path. Each
    // handles BOTH the value type and its Nullable<T> form: a JsonConverter<T> for a value type is NOT
    // applied to T? members by Newtonsoft, which would silently leave nullable fields on the default
    // (non-comparable) encoding.
    private static readonly JsonConverter _dateTimeOffset = new DateTimeOffsetUtcConverter();
    private static readonly JsonConverter _timeSpan = new TimeSpanTicksConverter();
    private static readonly JsonConverter _dateOnly = new DateOnlyTextConverter();
    private static readonly JsonConverter _timeOnly = new TimeOnlyTextConverter();

    /// <summary>The shared canonical converters relational adapters add to their JSON settings.</summary>
    public static JsonConverter[] JsonConverters() => new[] { _dateTimeOffset, _timeSpan, _dateOnly, _timeOnly };

    /// <summary>
    /// The canonical encoding and nothing else: the converters, and the parsing posture they need.
    ///
    /// <para>This is what a store needs to write and read the contract's form. <see cref="Apply"/> adds the
    /// managed-field wiring a store with its own mapping plan also needs; a store that stamps managed fields
    /// onto the document itself takes this entry point instead, so that neither rule is written twice.</para>
    /// </summary>
    public static JsonSerializerSettings ApplyConverters(JsonSerializerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Stop Newtonsoft from pre-parsing ISO strings to DateTime BEFORE our converters run. Its default
        // (DateParseHandling.DateTime) would (a) make DateTimeOffset round-trip depend on the ambient
        // DateTimeZoneHandling rather than on our explicit UTC parse, and (b) silently coerce string-typed
        // members whose value merely looks like a date. With None, our converters receive raw string/number
        // tokens and own all temporal parsing.
        settings.DateParseHandling = DateParseHandling.None;
        settings.Converters.Add(_dateTimeOffset);
        settings.Converters.Add(_timeSpan);
        settings.Converters.Add(_dateOnly);
        settings.Converters.Add(_timeOnly);
        return settings;
    }

    /// <summary>Registers the canonical converters onto an adapter's <see cref="JsonSerializerSettings"/>
    /// (used for both serialize and deserialize), leaving the naming strategy and other settings intact.</summary>
    public static JsonSerializerSettings Apply(
        JsonSerializerSettings settings,
        IEnumerable<DataSegmentationField>? segmentationFields = null)
    {
        ApplyConverters(settings);

        // Serialize-stage managed-field hook (DATA-0105 §3b, Seam 2). Wrap the adapter's existing contract
        // resolver in the shared ManagedFieldJsonInjector (Koan.Data.Core, ARCH-0103 §9 — lifted here so the JSON-text
        // KeyValueStore family stamps the same managed keys), preserving its naming strategy (CamelCase on SqlServer;
        // PascalCase default on SQLite/PG). When no module registers a managed field, the resolver is a pure
        // pass-through, so real-property serialization stays byte-identical. One shared wiring point for the
        // whole relational trio (all three call Apply).
        var naming = (settings.ContractResolver as Newtonsoft.Json.Serialization.DefaultContractResolver)?.NamingStrategy;
        settings.ContractResolver = new ManagedFieldJsonInjector(segmentationFields) { NamingStrategy = naming };
        return EntityJsonSerialization.Apply(settings);
    }

    // Non-generic base so CanConvert matches both T and T? (Newtonsoft does not route T? through a
    // JsonConverter<T>). Null is written/read as JSON null for the nullable form.
    private abstract class StructConverter<T> : JsonConverter where T : struct
    {
        public override bool CanConvert(Type objectType) => (Nullable.GetUnderlyingType(objectType) ?? objectType) == typeof(T);

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is null) { writer.WriteNull(); return; }
            WriteValue(writer, (T)value);
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            => reader.TokenType == JsonToken.Null ? null : ReadValue(reader);

        protected abstract void WriteValue(JsonWriter writer, T value);
        protected abstract T ReadValue(JsonReader reader);
    }

    private sealed class DateTimeOffsetUtcConverter : StructConverter<DateTimeOffset>
    {
        protected override void WriteValue(JsonWriter writer, DateTimeOffset value) => writer.WriteValue(Format(value));
        protected override DateTimeOffset ReadValue(JsonReader reader) => reader.Value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            var v => DateTimeOffset.Parse(v!.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
        };
    }

    private sealed class TimeSpanTicksConverter : StructConverter<TimeSpan>
    {
        protected override void WriteValue(JsonWriter writer, TimeSpan value) => writer.WriteValue(value.Ticks);
        protected override TimeSpan ReadValue(JsonReader reader) => reader.Value switch
        {
            long ticks => new TimeSpan(ticks),
            int ticks => new TimeSpan(ticks),
            string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture), // tolerate legacy "1.00:00:00"
            var v => new TimeSpan(System.Convert.ToInt64(v, CultureInfo.InvariantCulture)),
        };
    }

    private sealed class DateOnlyTextConverter : StructConverter<DateOnly>
    {
        protected override void WriteValue(JsonWriter writer, DateOnly value) => writer.WriteValue(Format(value));
        protected override DateOnly ReadValue(JsonReader reader) => reader.Value switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            DateTimeOffset dto => DateOnly.FromDateTime(dto.Date),
            var v => DateOnly.Parse(v!.ToString()!, CultureInfo.InvariantCulture), // tolerant of legacy formats
        };
    }

    private sealed class TimeOnlyTextConverter : StructConverter<TimeOnly>
    {
        protected override void WriteValue(JsonWriter writer, TimeOnly value) => writer.WriteValue(Format(value));
        protected override TimeOnly ReadValue(JsonReader reader) => reader.Value switch
        {
            DateTime dt => TimeOnly.FromDateTime(dt),
            TimeSpan ts => TimeOnly.FromTimeSpan(ts),
            var v => TimeOnly.Parse(v!.ToString()!, CultureInfo.InvariantCulture),
        };
    }
}
