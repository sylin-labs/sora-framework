using Koan.Data.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Koan.Data.Core.Polymorphism;

/// <summary>Shared Json.NET wiring and safe Entity-family materialization (DATA-0109).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class EntityJsonSerialization
{
    private static readonly JsonSerializerSettings DocumentSettings = Apply(
        new JsonSerializerSettings
        {
            // Property names keep the camel wire shape; dictionary KEYS are data and keep their exact casing
            // (AE-16): a stored document must not recase them.
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = true,
                },
            },
            NullValueHandling = NullValueHandling.Include,
            // Same hydration law as the relational codec (PMC-061): the stored document is authoritative —
            // Replace, never populate-into constructor-seeded collections.
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        });

    public static JsonSerializerSettings Apply(JsonSerializerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Stored collections replace constructor defaults; appending them duplicates state on every reload.
        settings.ObjectCreationHandling = ObjectCreationHandling.Replace;

        if (!settings.Converters.Any(static converter => converter is Newtonsoft.Json.Converters.StringEnumConverter))
            settings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter { AllowIntegerValues = false });

        if (settings.ContractResolver is not EntityJsonContractResolver)
        {
            var naming = (settings.ContractResolver as DefaultContractResolver)?.NamingStrategy;
            settings.ContractResolver = new EntityJsonContractResolver { NamingStrategy = naming };
        }

        if (!settings.Converters.Any(static converter => converter is EntityJsonConverter))
        {
            settings.Converters.Insert(0, EntityJsonConverter.Instance);
        }

        return settings;
    }

    public static object Materialize(
        JObject document,
        Type nominalType,
        JsonSerializer serializer)
        => Materialize(document, nominalType, serializer, useOperationTarget: true);

    /// <summary>
    /// Materializes a record while hydrating an eager store. Bulk hydration must classify every row from storage
    /// alone; one ambient point-read target cannot be applied to unrelated rows in the same file.
    /// </summary>
    public static object MaterializeStored(
        JObject document,
        Type nominalType,
        JsonSerializer serializer)
        => Materialize(document, nominalType, serializer, useOperationTarget: false);

    /// <summary>
    /// Serializes an Entity document for framework persistence adjuncts such as backup archives. Runtime shape and
    /// Entity-family identity are always retained.
    /// </summary>
    public static string SerializeDocument(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return JsonConvert.SerializeObject(entity, entity.GetType(), DocumentSettings);
    }

    /// <summary>
    /// Serializes an Entity into a typed token tree without a JSON text round-trip, preserving date and binary token
    /// kinds for canonical framework evidence.
    /// </summary>
    public static JToken SerializeDocumentToken(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var serializer = JsonSerializer.Create(DocumentSettings);
        var writer = new JTokenWriter();
        serializer.Serialize(writer, entity, entity.GetType());
        return writer.Token
            ?? throw new InvalidDataException(
                $"Entity token serialization produced no document for '{entity.GetType().FullName}'.");
    }

    /// <summary>
    /// Materializes a document produced by <see cref="SerializeDocumentToken"/> back into an Entity through the
    /// same document serializer and stored-only family classification that persistence restores with (AE-16).
    /// </summary>
    internal static object MaterializeDocument(JObject document, Type nominalType)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(nominalType);

        var serializer = JsonSerializer.Create(DocumentSettings);
        if (!EntityRootDescriptor.TryFor(nominalType, out _))
        {
            return document.ToObject(nominalType, serializer)
                ?? throw new InvalidDataException(
                    $"Entity JSON could not materialize '{nominalType.FullName}'.");
        }

        return MaterializeStored(document, nominalType, serializer);
    }

    /// <summary>Materializes a framework Entity document through the same safe family catalog as adapters.</summary>
    public static object DeserializeDocument(string json, Type nominalType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(nominalType);
        return JsonConvert.DeserializeObject(json, nominalType, DocumentSettings)
            ?? throw new InvalidDataException(
                $"Entity JSON could not materialize '{nominalType.FullName}'.");
    }

    private static object Materialize(
        JObject document,
        Type nominalType,
        JsonSerializer serializer,
        bool useOperationTarget)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(nominalType);
        ArgumentNullException.ThrowIfNull(serializer);

        var descriptor = EntityRootDescriptor.For(nominalType);
        var explicitTarget = descriptor.IsVariant
            ? nominalType
            : useOperationTarget
                ? EntityMaterializationScope.TargetFor(descriptor.RootType)
                : null;

        Type? storedType = null;
        if (document.TryGetValue(
                EntityFamilyStorage.TypeField,
                StringComparison.Ordinal,
                out var token))
        {
            if (token.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace(token.Value<string>()))
            {
                throw new InvalidDataException(
                    $"Entity field '{EntityFamilyStorage.TypeField}' must contain a non-empty string.");
            }

            storedType = EntityTypeCatalog.Resolve(descriptor.RootType, token.Value<string>()!);
        }

        if (descriptor.IsVariant && storedType is not null && nominalType != storedType)
        {
            throw new InvalidDataException(
                $"Entity JSON was declared as '{nominalType.FullName}' but storage identifies it as " +
                $"'{storedType.FullName}'. Refuse a cross-variant materialization.");
        }

        // A stored, allowlisted identity is authoritative for this document. The operation target classifies only
        // legacy hintless top-level rows; choosing the stored type here also lets nested members of the same family
        // restore their own sibling variants while a typed repository performs its final top-level type check.
        var actualType = storedType ?? explicitTarget ?? descriptor.RootType;
        if (!descriptor.RootType.IsAssignableFrom(actualType) ||
            descriptor.IsVariant && !nominalType.IsAssignableFrom(actualType))
        {
            throw new InvalidDataException(
                $"Resolved Entity type '{actualType.FullName}' is not assignable to '{descriptor.RootType.FullName}'.");
        }

        using var _ = EntityJsonConverter.BypassOnce(actualType);
        return document.ToObject(actualType, serializer)
            ?? throw new InvalidDataException(
                $"Entity JSON could not materialize '{actualType.FullName}'.");
    }
}
