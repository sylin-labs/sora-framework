using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Koan.Data.Connector.Couchbase.Runtime;

internal sealed class CouchbaseDocumentPlan<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    /// <summary>
    /// Couchbase's own document settings: the framework's, plus DATA-0100's canonical encodings.
    ///
    /// <para>This store orders and filters by reading back what it wrote, so the stored form has to be the
    /// comparable one. Without it a <see cref="TimeSpan"/> is written as .NET spells it, and N1QL then orders
    /// <c>"1.00:00:00"</c> before <c>"23:00:00"</c> — a day ahead of twenty-three hours, which is the exact
    /// inversion the contract exists to close.</para>
    ///
    /// <para>The settings are Couchbase's rather than the framework's shared document settings because those
    /// are also the on-disk form for the Json and Redis stores, backup archives and cutover evidence. The
    /// contract governs a store that compares what it stored; changing it for stores that do not would rewrite
    /// files nobody asked to migrate.</para>
    /// </summary>
    private static readonly JsonSerializerSettings DocumentSettings =
        EntityJsonSerialization.Apply(ComparableScalarEncoding.ApplyConverters(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include
        }));

    private readonly MappingPlan? _mapping;
    private readonly string _identityName;

    internal CouchbaseDocumentPlan(IServiceProvider services, string source, MappingPlan? mapping)
    {
        _mapping = mapping;
        _identityName = services.GetStorageOptimization<TEntity, TKey>().IdPropertyName;
        var segmentation = services.GetRequiredService<DataSegmentationPlan>().For(typeof(TEntity));
        if (mapping?.Identity.IsGenerated == true)
            throw new MappingCompilationException(source, typeof(TEntity),
                "Couchbase document keys are application-assigned. Remove Generated() or assign the entity key before Save().");
        if (mapping is not null &&
            (!segmentation.IsEmpty || ManagedFieldRegistry.ForType(typeof(TEntity)).Count != 0))
            throw new MappingCompilationException(source, typeof(TEntity),
                "An explicit Couchbase map cannot preserve framework-managed record fields. Use managed storage or a separate source/container axis.");
    }

    internal MappingPlan? Mapping => _mapping;
    internal StorageAddress? MappedContainer => _mapping?.Container;

    internal JObject Write(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_mapping is null)
        {
            var document = JObject.Parse(JsonConvert.SerializeObject(entity, entity.GetType(), DocumentSettings));
            ManagedFieldJsonInjector.InjectManaged(document, ManagedFieldWriteScope.Effective);
            return document;
        }

        var mapped = new JObject();
        foreach (var value in _mapping.Write(entity).Values)
            Set(mapped, value.Path, ToJson(value.Value));
        return mapped;
    }

    internal void ApplyMappedWrite(JObject target, TEntity entity)
    {
        if (_mapping is null) throw new InvalidOperationException("Managed Couchbase documents use replacement writes.");
        foreach (var value in _mapping.Write(entity, MappingWriteOperation.Update).Values)
            Set(target, value.Path, ToJson(value.Value));
    }

    internal TEntity Read(JObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_mapping is null)
            return (TEntity)(JsonConvert.DeserializeObject(
                document.ToString(Formatting.None), typeof(TEntity), DocumentSettings)
                ?? throw new InvalidDataException(
                    $"Entity JSON could not materialize '{typeof(TEntity).FullName}'."));

        var values = new List<MappedValue>(_mapping.Bindings.Count);
        foreach (var binding in _mapping.Bindings)
            if (TryGet(document, binding.PhysicalPath, out var token))
                values.Add(new MappedValue(
                    binding.Id,
                    binding.PhysicalPath,
                    binding.Shape,
                    ToNeutral(token)));
        return _mapping.Hydrate<TEntity>(values);
    }

    internal string Key(TKey id)
    {
        var text = id switch
        {
            string value => value,
            Guid value => value.ToString("D"),
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonConvert.SerializeObject(id, Formatting.None)
        };
        if (!string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text) <= Infrastructure.Constants.MaximumKeyBytes)
            return text;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "null"));
        return "koan:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal string Field(FieldPath path, ResolvedField resolved, MappingConsumer consumer)
    {
        if (resolved.IsManaged) return CouchbasePath.Quote([resolved.StorageName!]);
        var logical = resolved.CanonicalPath ?? path;
        if (_mapping is not null)
        {
            var use = _mapping.Use(MappingPath.Of(logical.Segments.ToArray()), consumer);
            var binding = use.Bindings.Count == 1
                ? use.Bindings[0]
                : throw new NotSupportedException(
                    $"Couchbase cannot use multi-binding logical path '{path}' as one SQL++ value.");
            return CouchbasePath.Quote([binding.PhysicalPath.Name, .. binding.PhysicalPath.Segments]);
        }
        if (logical.Segments.Count == 1 && string.Equals(logical.Leaf, _identityName, StringComparison.Ordinal))
            return CouchbasePath.Quote([Infrastructure.Constants.Storage.Identity]);
        return CouchbasePath.Quote(logical.Segments.Select(Camel));
    }

    /// <summary>
    /// The indexes this entity declared, in the terms this store will build them from.
    ///
    /// <para>A compiled mapping gives the physical path directly. Without one, the same conventional spelling
    /// filters use is applied, so the index covers the reads it exists for rather than a path nothing emits.
    /// TTL is document expiry here rather than an index, so a TTL declaration is not turned into one.</para>
    /// </summary>
    internal IReadOnlyList<CouchbaseDeclaredIndex> DeclaredIndexes()
    {
        if (_mapping is not null)
            return _mapping.Indexes
                .Where(static index => !index.Primary && !index.Ttl)
                .Select(index => new CouchbaseDeclaredIndex(
                    index.Name,
                    index.Bindings.Select(static binding => CouchbasePath.Quote(
                        [binding.PhysicalPath.Name, .. binding.PhysicalPath.Segments])).ToArray(),
                    index.Unique))
                .ToArray();

        return IndexMetadata.GetIndexes(typeof(TEntity))
            .Where(static index => !index.IsPrimaryKey && !index.Ttl && !string.IsNullOrWhiteSpace(index.Name))
            .Select(index => new CouchbaseDeclaredIndex(
                index.Name!,
                index.Properties.Select(property =>
                {
                    var path = FieldPath.Of(property.Name);
                    return Field(path, FieldPathResolver.Resolve(typeof(TEntity), path), MappingConsumer.Index);
                }).ToArray(),
                index.Unique))
            .ToArray();
    }

    /// <summary>
    /// The SQL++ value an order key reaches for when its path runs through a collection.
    ///
    /// <para><c>-Sightings.LastChangedAt</c> means "by the latest sighting of each widget", which is an
    /// aggregate over the nested array rather than a field. SQL++ says that directly —
    /// <c>ARRAY_MAX(ARRAY element.`lastChangedAt` FOR element IN doc.`sightings` END)</c> — so Couchbase
    /// orders and pages the query itself instead of handing the whole collection back to be sorted in
    /// memory. Ascending takes the minimum, which is the same convention the framework's own sorter uses:
    /// order by the earliest when ascending, the latest when descending.</para>
    ///
    /// <para>An empty array yields NULL, and SQL++ sorts NULL before every value, matching what the
    /// in-memory sorter does with a widget that has no sightings at all.</para>
    ///
    /// <para>Returns <see langword="null"/> when this plan cannot express the key, so the caller can decline
    /// it and let the framework finish the ordering: an explicit map may bind one logical path to several
    /// physical ones, a second collection inside the path needs an aggregate of aggregates, and positional
    /// First/Last depend on an element order a document store does not promise.</para>
    /// </summary>
    internal string? CollectionOrderValue(MemberPath path, SortAggregation aggregation)
    {
        ArgumentNullException.ThrowIfNull(path);
        var function = aggregation switch
        {
            // None reaching a collection leaf means the same thing it means to the in-memory sorter: take the
            // maximum, which is direction-agnostic and never invents an element order.
            SortAggregation.Max or SortAggregation.None => "ARRAY_MAX",
            SortAggregation.Min => "ARRAY_MIN",
            _ => null
        };
        if (function is null || _mapping is not null) return null;

        var boundary = path.CollectionSegmentIndex;
        if (boundary <= 0 || boundary >= path.Members.Count) return null;
        for (var index = boundary; index < path.Members.Count; index++)
            if (IsCollection(MemberValueType(path.Members[index]))) return null;

        var collection = CouchbasePath.Quote(path.Members.Take(boundary).Select(static member => Camel(member.Name)));
        var leaf = CouchbasePath.Quote(path.Members.Skip(boundary).Select(static member => Camel(member.Name)));
        var value = $"{OrderElement}.{leaf}";
        if ((Nullable.GetUnderlyingType(path.ValueType) ?? path.ValueType).IsEnum)
            value = EnumOrderValue(value, path.ValueType);
        return $"{function}(ARRAY {value} FOR {OrderElement} IN doc.{collection} END)";
    }

    internal bool UsesEnumNames(FieldPath path, ResolvedField resolved, MappingConsumer consumer)
        => (Nullable.GetUnderlyingType(resolved.ComparableType) ?? resolved.ComparableType).IsEnum
            && (_mapping is null || _mapping.Use(MappingPath.Of((resolved.CanonicalPath ?? path).Segments.ToArray()), consumer)
                .Bindings.Single().Descriptor.Codec is null);

    internal static string EnumOrderValue(string value, Type enumType)
        => $"(CASE {value} {string.Join(" ", EnumStorageEncoding.OrderedValues(enumType).Select(pair =>
            $"WHEN {JsonConvert.SerializeObject(pair.Key)} THEN {pair.Value.ToString(CultureInfo.InvariantCulture)}"))} ELSE NULL END)";

    /// <summary>Binding name for the array comprehension; prefixed so it cannot collide with a document field.</summary>
    private const string OrderElement = "koan_order_element";

    private static Type MemberValueType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(object)
    };

    private static bool IsCollection(Type type) =>
        type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    internal object? FilterValue(FieldPath path, ResolvedField resolved, object? value)
    {
        var converted = FilterValueConverter.Convert(value, resolved.ComparableType);
        if (_mapping is not null)
        {
            var logical = resolved.CanonicalPath ?? path;
            var binding = _mapping.Use(MappingPath.Of(logical.Segments.ToArray()), MappingConsumer.Filter)
                .Bindings.Single();
            if (!resolved.TargetsCollection) converted = binding.Encode(converted);
        }
        // The comparand has to be spelled the way the document spells it, or the comparison is between two
        // different encodings of the same value.
        return ComparableScalarEncoding.EncodeComparand(converted);
    }

    private static string Camel(string value) =>
        string.IsNullOrEmpty(value) || char.IsLower(value[0]) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static void Set(JObject root, PhysicalPath path, JToken value)
    {
        var names = new[] { path.Name }.Concat(path.Segments).ToArray();
        JObject current = root;
        for (var index = 0; index < names.Length - 1; index++)
        {
            if (current[names[index]] is not JObject next)
            {
                next = new JObject();
                current[names[index]] = next;
            }
            current = next;
        }
        current[names[^1]] = value;
    }

    private static bool TryGet(JObject root, PhysicalPath path, out JToken value)
    {
        JToken? current = root[path.Name];
        foreach (var segment in path.Segments)
            current = (current as JObject)?[segment];
        value = current!;
        return current is not null;
    }

    private static JToken ToJson(object? value) => value switch
    {
        null => JValue.CreateNull(),
        DataObject data => new JObject(data.Properties.Select(property =>
            new JProperty(property.Name, ToJson(property.Value)))),
        DataArray data => new JArray(data.Items.Select(ToJson)),
        byte[] bytes => new JValue(Convert.ToBase64String(bytes)),
        // A mapped write reaches here after its binding has encoded the value. EncodeComparand transforms only
        // the four types DATA-0100 governs, so a binding that already produced the canonical form passes
        // through untouched and one that left the CLR value is corrected here.
        _ => JToken.FromObject(ComparableScalarEncoding.EncodeComparand(value)!)
    };

    private static object? ToNeutral(JToken value) => CouchbaseNeutralReader.Neutral(value);
}
