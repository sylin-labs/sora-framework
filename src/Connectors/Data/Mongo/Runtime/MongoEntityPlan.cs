using System.Globalization;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using System.Reflection;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Core;
using Koan.Data.Core.Optimization;
using Koan.Data.Core.Polymorphism;
using Koan.Data.Core.Semantics;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Koan.Data.Connector.Mongo.Runtime;

internal sealed class MongoEntityPlan<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly MappingPlan? _mapping;
    private readonly JsonSerializerSettings _json;
    private readonly string _identityJsonName;

    public MongoEntityPlan(IServiceProvider services, string source, MappingPlan? mapping)
    {
        _mapping = mapping;
        Optimization = services.GetStorageOptimization<TEntity, TKey>();
        IdentityName = Optimization.IdPropertyName;
        var segmentation = services.GetRequiredService<DataSegmentationPlan>().For(typeof(TEntity));
        if (mapping is not null &&
            (!segmentation.IsEmpty || ManagedFieldRegistry.ForType(typeof(TEntity)).Count != 0))
            throw new MappingCompilationException(source, typeof(TEntity),
                "Explicit MongoDB maps cannot preserve framework-managed row fields. Use a managed map or a separate source/container axis.");

        var naming = new CamelCaseNamingStrategy();
        _identityJsonName = naming.GetPropertyName(IdentityName, hasSpecifiedName: false);
        _json = EntityJsonSerialization.Apply(new JsonSerializerSettings
        {
            ContractResolver = new ManagedFieldJsonInjector(segmentation.Fields) { NamingStrategy = naming },
            DateParseHandling = DateParseHandling.None,
            NullValueHandling = NullValueHandling.Include,
            Converters =
            {
                new DateTimeOffsetConverter(),
                new TimeSpanConverter(),
                new DateOnlyConverter(),
                new TimeOnlyConverter()
            }
        });
    }

    public StorageOptimizationInfo Optimization { get; }
    public string IdentityName { get; }
    public bool IsMapped => _mapping is not null;
    public MappingPlan? Mapping => _mapping;
    public StorageAddress? MappedContainer => _mapping?.Container;

    public BsonDocument Write(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_mapping is not null)
        {
            var mappedDocument = new BsonDocument();
            foreach (var value in _mapping.Write(entity).Values)
                MongoValues.Set(mappedDocument, value.Path, MongoValues.FromNeutral(value.Value));
            return mappedDocument;
        }

        var json = JsonConvert.SerializeObject(entity, entity.GetType(), _json);
        var payload = JObject.Parse(json);
        var identity = payload.Property(_identityJsonName, StringComparison.OrdinalIgnoreCase)
            ?? throw new InvalidDataException(
                $"MongoDB could not locate identity '{IdentityName}' while serializing '{typeof(TEntity).FullName}'.");
        var document = MongoValues.FromJson(payload).AsBsonDocument;
        document.Remove(identity.Name);
        document[Infrastructure.Constants.Storage.Identity] = MongoValues.FromJson(identity.Value);
        return document;
    }

    public UpdateDefinition<BsonDocument> Update(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_mapping is null)
            throw new InvalidOperationException("Managed MongoDB documents use replacement writes.");
        var updates = _mapping.Write(entity).Values.Select(value =>
        {
            var path = MongoValues.Path(value.Path);
            var encoded = MongoValues.FromNeutral(value.Value);
            return string.Equals(path, Infrastructure.Constants.Storage.Identity, StringComparison.Ordinal)
                ? Builders<BsonDocument>.Update.SetOnInsert(path, encoded)
                : Builders<BsonDocument>.Update.Set(path, encoded);
        });
        return Builders<BsonDocument>.Update.Combine(updates);
    }

    public TEntity Read(BsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_mapping is not null)
        {
            var values = new List<MappedValue>(_mapping.Bindings.Count);
            foreach (var binding in _mapping.Bindings)
                if (MongoValues.TryGet(document, binding.PhysicalPath, out var value))
                    values.Add(new MappedValue(
                        binding.Id,
                        binding.PhysicalPath,
                        binding.Shape,
                        MongoValues.ToNeutral(value)));
            return _mapping.Hydrate<TEntity>(values);
        }

        var payload = (JObject)MongoValues.ToJson(document);
        if (payload.TryGetValue(Infrastructure.Constants.Storage.Identity, out var identity))
        {
            payload.Remove(Infrastructure.Constants.Storage.Identity);
            payload[_identityJsonName] = identity;
        }
        return (TEntity)(JsonConvert.DeserializeObject(payload.ToString(Formatting.None), typeof(TEntity), _json)
            ?? throw new InvalidDataException($"MongoDB returned an empty document for '{typeof(TEntity).FullName}'."));
    }

    public FilterDefinition<BsonDocument> Identity(TKey id)
    {
        if (_mapping is null)
            return Builders<BsonDocument>.Filter.Eq(
                Infrastructure.Constants.Storage.Identity,
                MongoValues.FromNeutral(id));
        return Identity(_mapping.WriteIdentity(id));
    }

    public FilterDefinition<BsonDocument> Identity(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_mapping is null) return Identity(entity.Id);
        var values = _mapping.Identity.Parts.Select(part =>
        {
            var binding = _mapping.Bindings.Single(candidate => candidate.Id == part.Id);
            return new MappedValue(binding.Id, binding.PhysicalPath, binding.Shape, binding.Encode(binding.Read(entity)));
        });
        return Identity(new MappedRecord(values, new MappingReceipt(
            _mapping.Id,
            MappingConsumer.Filter,
            _mapping.Identity.Parts.Select(static part => part.Id))));
    }

    public FilterDefinition<BsonDocument> WriteGuard()
    {
        var guard = ManagedFieldWriteScope.Current;
        if (guard is null || guard.Count == 0) return FilterDefinition<BsonDocument>.Empty;
        var filters = guard.Select(value => Builders<BsonDocument>.Filter.Eq(
            value.Key,
            MongoValues.FromNeutral(value.Value)));
        return Builders<BsonDocument>.Filter.And(filters);
    }

    public string Field(FieldPath path, ResolvedField resolved, MappingConsumer consumer)
    {
        if (resolved.IsManaged) return resolved.StorageName!;
        if (_mapping is not null)
        {
            var use = _mapping.Use(MappingPath.Of(path.Segments.ToArray()), consumer);
            return MongoValues.Path(use.Bindings.Single().PhysicalPath);
        }
        if (path.Segments.Count == 1 && string.Equals(path.Leaf, IdentityName, StringComparison.Ordinal))
            return Infrastructure.Constants.Storage.Identity;
        return string.Join('.', path.Segments.Select(Camel));
    }

    /// <summary>
    /// The aggregation expression an order key reaches for when its path runs through a collection.
    ///
    /// <para><c>-Sightings.LastChangedAt</c> orders widgets by their latest sighting, which is an aggregate
    /// over a nested array rather than a field. <c>find</c> cannot sort by one, but the aggregation language
    /// states it plainly — <c>$max</c> over a <c>$map</c> of the array — so MongoDB orders and pages the query
    /// itself instead of returning the whole collection for the framework to sort.</para>
    ///
    /// <para>An absent or empty array maps to nothing and aggregates to null, and BSON sorts null before every
    /// value, so a widget with no sightings lands first ascending and last descending — where the framework's
    /// own sorter puts it.</para>
    ///
    /// <para>Returns <see langword="null"/> where this plan cannot express the key: an explicit map may bind
    /// one logical path across several physical ones, a second collection inside the path would need an
    /// aggregate of aggregates, and positional First/Last depend on an element order a document store does not
    /// promise.</para>
    /// </summary>
    public BsonDocument? CollectionOrderExpression(MemberPath path, SortAggregation aggregation)
    {
        ArgumentNullException.ThrowIfNull(path);
        var accumulator = aggregation switch
        {
            // None over a collection leaf means what it means to the in-memory sorter: take the maximum.
            SortAggregation.Max or SortAggregation.None => "$max",
            SortAggregation.Min => "$min",
            _ => null
        };
        if (accumulator is null || _mapping is not null) return null;

        var boundary = path.CollectionSegmentIndex;
        if (boundary <= 0 || boundary >= path.Members.Count) return null;
        for (var index = boundary; index < path.Members.Count; index++)
            if (IsCollectionMember(path.Members[index])) return null;

        var array = string.Join('.', path.Members.Take(boundary).Select(static member => Camel(member.Name)));
        var leaf = string.Join('.', path.Members.Skip(boundary).Select(static member => Camel(member.Name)));
        return new BsonDocument(accumulator, new BsonDocument("$map", new BsonDocument
        {
            ["input"] = new BsonDocument("$ifNull", new BsonArray { "$" + array, new BsonArray() }),
            ["as"] = "koanElement",
            ["in"] = (Nullable.GetUnderlyingType(path.ValueType) ?? path.ValueType).IsEnum
                ? EnumOrderExpression("$$koanElement." + leaf, path.ValueType)
                : new BsonString("$$koanElement." + leaf)
        }));
    }

    private static bool IsCollectionMember(MemberInfo member)
    {
        var type = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(object)
        };
        return type != typeof(string) && type != typeof(byte[]) &&
               typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    public BsonValue FilterValue(FieldPath path, ResolvedField resolved, object? value)
    {
        var converted = FilterValueConverter.Convert(value, resolved.ComparableType);
        if (_mapping is not null)
        {
            var binding = _mapping.Use(MappingPath.Of(path.Segments.ToArray()), MappingConsumer.Filter)
                .Bindings.Single();
            if (!resolved.TargetsCollection) converted = binding.Encode(converted);
        }
        return MongoValues.FromNeutral(converted);
    }

    public bool UsesEnumNames(FieldPath path, ResolvedField resolved, MappingConsumer consumer = MappingConsumer.Filter)
        => (Nullable.GetUnderlyingType(resolved.ComparableType) ?? resolved.ComparableType).IsEnum
            && (_mapping is null || _mapping.Use(MappingPath.Of(path.Segments.ToArray()), consumer)
                .Bindings.Single().Descriptor.Codec is null);

    public static BsonDocument EnumOrderExpression(string value, Type enumType)
        => new("$switch", new BsonDocument
        {
            ["branches"] = new BsonArray(EnumStorageEncoding.OrderedValues(enumType).Select(pair => new BsonDocument
            {
                ["case"] = new BsonDocument("$eq", new BsonArray { value, new BsonDocument("$literal", pair.Key) }),
                ["then"] = new BsonDecimal128(pair.Value)
            })),
            ["default"] = BsonNull.Value
        });

    private static FilterDefinition<BsonDocument> Identity(MappedRecord record)
    {
        var filters = record.Values.Select(value => Builders<BsonDocument>.Filter.Eq(
            MongoValues.Path(value.Path),
            MongoValues.FromNeutral(value.Value)));
        return Builders<BsonDocument>.Filter.And(filters);
    }

    private static string Camel(string value) =>
        string.IsNullOrEmpty(value) || char.IsLower(value[0])
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    private abstract class StructConverter<T> : JsonConverter where T : struct
    {
        public override bool CanConvert(Type objectType) =>
            (Nullable.GetUnderlyingType(objectType) ?? objectType) == typeof(T);

        public override void WriteJson(JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (value is null) { writer.WriteNull(); return; }
            Write(writer, (T)value);
        }

        public override object? ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            Newtonsoft.Json.JsonSerializer serializer) =>
            reader.TokenType == JsonToken.Null ? null : Read(reader.Value);

        protected abstract void Write(JsonWriter writer, T value);
        protected abstract T Read(object? value);
    }

    private sealed class DateTimeOffsetConverter : StructConverter<DateTimeOffset>
    {
        protected override void Write(JsonWriter writer, DateTimeOffset value) =>
            writer.WriteValue(value.UtcDateTime);

        protected override DateTimeOffset Read(object? value) => value switch
        {
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime()),
            _ => DateTimeOffset.Parse(value!.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToUniversalTime()
        };
    }

    private sealed class TimeSpanConverter : StructConverter<TimeSpan>
    {
        protected override void Write(JsonWriter writer, TimeSpan value) => writer.WriteValue(value.Ticks);
        protected override TimeSpan Read(object? value) =>
            TimeSpan.FromTicks(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private sealed class DateOnlyConverter : StructConverter<DateOnly>
    {
        protected override void Write(JsonWriter writer, DateOnly value) =>
            writer.WriteValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        protected override DateOnly Read(object? value) =>
            DateOnly.ParseExact(value!.ToString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private sealed class TimeOnlyConverter : StructConverter<TimeOnly>
    {
        protected override void Write(JsonWriter writer, TimeOnly value) =>
            writer.WriteValue(value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        protected override TimeOnly Read(object? value) =>
            TimeOnly.ParseExact(value!.ToString()!, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    }
}
