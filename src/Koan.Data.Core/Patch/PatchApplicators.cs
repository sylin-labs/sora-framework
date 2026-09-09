using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Koan.Core.Json;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Core.Polymorphism;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Koan.Data.Core.Patch;

/// <summary>Applies RFC 7386 merge-patch intent by restoring a merged Entity document, never by editing the live target.</summary>
public sealed class MergePatchApplicator<TEntity>
{
    private readonly JToken _patch;
    private readonly MergePatchNullPolicy _nulls;
    public MergePatchApplicator(JToken patch, MergePatchNullPolicy nulls) { _patch = patch; _nulls = nulls; }

    /// <summary>
    /// Returns a working copy of <paramref name="target"/> with the patch applied. The target is only read, so a
    /// refused, malformed, or non-convertible patch leaves it exactly as it was and throws before any save.
    /// </summary>
    public TEntity ApplyToCopy(TEntity target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (_patch.Type != JTokenType.Object) throw new ArgumentException("Merge-patch payload must be an object");
        return (TEntity)EntityPatchDocument.Apply(EntityPatchDocument.MergeNulls(_nulls), target, _patch);
    }
}

/// <summary>Applies partial-JSON intent by restoring a merged Entity document, never by editing the live target.</summary>
public sealed class PartialJsonApplicator<TEntity>
{
    private readonly JToken _patch;
    private readonly PartialJsonNullPolicy _nulls;
    public PartialJsonApplicator(JToken patch, PartialJsonNullPolicy nulls) { _patch = patch; _nulls = nulls; }

    /// <summary>
    /// Returns a working copy of <paramref name="target"/> with the patch applied. The target is only read, so a
    /// refused, malformed, or non-convertible patch leaves it exactly as it was and throws before any save.
    /// </summary>
    public TEntity ApplyToCopy(TEntity target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (_patch.Type != JTokenType.Object) throw new ArgumentException("Partial JSON payload must be an object");
        return (TEntity)EntityPatchDocument.Apply(EntityPatchDocument.PartialNulls(_nulls), target, _patch);
    }
}

/// <summary>
/// One typed merge engine behind both applicators (AE-16): every supplied member (including every member of a
/// newly created object, dictionary entry, or array element) is admitted against native Newtonsoft writable
/// contracts with the Entity document's wire names, merged into the target's own serialized document, and restored
/// through <see cref="EntityJsonSerialization"/>, the same owner persistence reads with. Additive collections,
/// private stored state, siblings, identity and family shape survive; and patch admission stays distinct from
/// persistence restoration, so a private setter the document restores is not permission for a caller to assign it.
/// </summary>
internal static class EntityPatchDocument
{
    private static readonly KoanJsonContractResolver Admission = new()
    {
        // Aligned with EntityJsonSerialization's document settings: camel property names, exact dictionary keys.
        NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true },
    };

    public static object Apply(NullPolicy nulls, object target, JToken patch)
    {
        var document = EntityJsonSerialization.SerializeDocumentToken(target) as JObject
            ?? throw new ArgumentException($"'{target.GetType().FullName}' does not serialize to a JSON object document.");
        MergeObject(document, (JObject)patch, target.GetType(), nulls);
        return EntityJsonSerialization.MaterializeDocument(document, target.GetType());
    }

    internal static NullPolicy MergeNulls(MergePatchNullPolicy policy) => NullPolicy.ForMerge(policy);
    internal static NullPolicy PartialNulls(PartialJsonNullPolicy policy) => NullPolicy.ForPartial(policy);

    private static void MergeObject(JObject document, JObject patch, Type objectType, NullPolicy nulls)
    {
        var family = EntityRootDescriptor.TryFor(objectType, out _);
        var contract = Admission.ResolveContract(objectType) as JsonObjectContract
            ?? throw new ArgumentException($"'{objectType.FullName}' does not accept object patch members.");
        var identity = family
            ? objectType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in patch.Properties())
        {
            var property = Resolve(contract, member.Name);
            if (family && (IsReservedName(member.Name) ||
                           (property is not null && identity is not null &&
                            string.Equals(property.UnderlyingName, identity.Name, StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    $"Patch member '{member.Name}' changes Entity identity or family shape. Remove it; the route id and " +
                    $"'{EntityFamilyStorage.TypeField}' are immutable under patch.");
            }

            if (property is not null && !seen.Add(property.UnderlyingName ?? property.PropertyName!))
            {
                throw new InvalidOperationException(
                    $"Patch member '{member.Name}' repeats an already-patched member of '{objectType.Name}'. " +
                    "Members match case-insensitively; send one unambiguous name.");
            }

            if (property is null)
            {
                if (contract.ExtensionDataSetter is not null) document[member.Name] = member.Value.DeepClone();
                continue;
            }

            if (property.Ignored || !CallerWritable(property)) continue;
            MergeMember(document, property.PropertyName!, member.Value, property.PropertyType!, nulls);
        }
    }

    private static void MergeMember(JObject owner, string name, JToken value, Type memberType, NullPolicy nulls)
    {
        if (value.Type == JTokenType.Null)
        {
            nulls.ApplyMember(owner, name, memberType);
            return;
        }

        MergeInto(owner, name, value, memberType, nulls);
    }

    private static void MergeInto(JObject owner, string name, JToken value, Type declaredType, NullPolicy nulls)
    {
        if (IsRawData(declaredType))
        {
            owner[name] = value.DeepClone();
            return;
        }

        switch (Admission.ResolveContract(declaredType))
        {
            case JsonDictionaryContract dictionary when value.Type == JTokenType.Object:
                var entries = owner[name] as JObject;
                if (entries is null)
                {
                    owner[name] = AdmitDictionary((JObject)value, dictionary, nulls);
                    return;
                }

                var valueType = dictionary.DictionaryValueType ?? typeof(object);
                foreach (var entry in ((JObject)value).Properties())
                {
                    if (entry.Value.Type == JTokenType.Null)
                    {
                        nulls.ApplyDictionaryEntry(entries, entry.Name, valueType);
                        continue;
                    }

                    MergeInto(entries, entry.Name, entry.Value, valueType, nulls);
                }

                return;

            case JsonArrayContract array when value.Type == JTokenType.Array:
                owner[name] = AdmitArray((JArray)value, array, nulls);
                return;

            case JsonObjectContract when value.Type == JTokenType.Object:
                var child = owner[name] as JObject;
                if (child is not null)
                {
                    MergeObject(child, (JObject)value, GoverningType(declaredType, child), nulls);
                    return;
                }

                owner[name] = AdmitObject((JObject)value, declaredType, nulls);
                return;

            default:
                owner[name] = value.DeepClone();
                return;
        }
    }

    /// <summary>Builds an admitted subtree for a position with no stored value: nothing is restored that the caller could not assign.</summary>
    private static JToken AdmitValue(JToken value, Type declaredType, NullPolicy nulls)
    {
        if (IsRawData(declaredType))
        {
            return value.DeepClone();
        }

        switch (Admission.ResolveContract(declaredType))
        {
            case JsonDictionaryContract dictionary when value.Type == JTokenType.Object:
                return AdmitDictionary((JObject)value, dictionary, nulls);
            case JsonArrayContract array when value.Type == JTokenType.Array:
                return AdmitArray((JArray)value, array, nulls);
            case JsonObjectContract when value.Type == JTokenType.Object:
                return AdmitObject((JObject)value, declaredType, nulls);
            default:
                return value.DeepClone();
        }
    }

    private static JToken AdmitDictionary(JObject patch, JsonDictionaryContract dictionary, NullPolicy nulls)
    {
        var admitted = new JObject();
        var valueType = dictionary.DictionaryValueType ?? typeof(object);
        foreach (var entry in patch.Properties())
        {
            if (entry.Value.Type == JTokenType.Null)
            {
                nulls.ApplyDictionaryEntry(admitted, entry.Name, valueType);
                continue;
            }

            MergeInto(admitted, entry.Name, entry.Value, valueType, nulls);
        }

        return admitted;
    }

    private static JArray AdmitArray(JArray patch, JsonArrayContract array, NullPolicy nulls)
    {
        var itemType = array.CollectionItemType;
        var admitted = new JArray();
        foreach (var item in patch)
        {
            admitted.Add(item.Type == JTokenType.Null || itemType is null
                ? item.DeepClone()
                : AdmitValue(item, itemType, nulls));
        }

        return admitted;
    }

    private static JObject AdmitObject(JObject patch, Type objectType, NullPolicy nulls)
    {
        if (objectType.IsAbstract || objectType.IsInterface)
        {
            throw new InvalidOperationException(
                $"Patch creates a '{objectType.FullName}' value, but no stored value exists to resolve a concrete type. " +
                "Patch the concrete value where it is already stored; a caller cannot choose the runtime shape.");
        }

        var admitted = new JObject();
        MergeObject(admitted, patch, objectType, nulls);
        return admitted;
    }

    private static JsonProperty? Resolve(JsonObjectContract contract, string name)
    {
        JsonProperty? insensitive = null;
        foreach (var property in contract.Properties)
        {
            if (string.Equals(property.PropertyName, name, StringComparison.Ordinal)) return property;
            if (insensitive is null && string.Equals(property.PropertyName, name, StringComparison.OrdinalIgnoreCase))
                insensitive = property;
        }
        return insensitive;
    }

    /// <summary>Native Newtonsoft admission: a member is patchable only when public code could assign it.</summary>
    private static bool CallerWritable(JsonProperty property)
    {
        if (!property.Writable) return false;
        if (property.UnderlyingName is null || property.DeclaringType is null) return true;
        var declared = property.DeclaringType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(info => string.Equals(info.Name, property.UnderlyingName, StringComparison.Ordinal));
        return declared is null || declared.GetSetMethod() is not null;
    }

    private static bool IsReservedName(string name)
        => string.Equals(name, "Id", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, EntityFamilyStorage.TypeField, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored document governs a family member: its own discriminator resolves the admitting shape, never caller input.</summary>
    private static Type GoverningType(Type memberType, JObject child)
    {
        if (!EntityRootDescriptor.TryFor(memberType, out var descriptor) ||
            !child.TryGetValue(EntityFamilyStorage.TypeField, StringComparison.Ordinal, out var hint) ||
            hint.Type != JTokenType.String)
        {
            return memberType;
        }

        return EntityTypeCatalog.Resolve(descriptor.RootType, hint.Value<string>()!);
    }

    private static bool IsRawData(Type type)
        => type == typeof(object) || typeof(JToken).IsAssignableFrom(type);

    /// <summary>
    /// Null semantics per patch kind. Typed members default explicitly (merge) or assign null (partial); dictionary
    /// entries are removed: data keys carry removal natively, so member defaulting never rewrites them.
    /// </summary>
    internal readonly struct NullPolicy
    {
        private readonly MergePatchNullPolicy? _merge;
        private readonly PartialJsonNullPolicy? _partial;

        private NullPolicy(MergePatchNullPolicy? merge, PartialJsonNullPolicy? partial)
        {
            _merge = merge;
            _partial = partial;
        }

        public static NullPolicy ForMerge(MergePatchNullPolicy policy) => new(policy, null);
        public static NullPolicy ForPartial(PartialJsonNullPolicy policy) => new(null, policy);

        public void ApplyMember(JObject owner, string name, Type memberType)
        {
            if (_partial is { } partial)
            {
                switch (partial)
                {
                    case PartialJsonNullPolicy.Ignore:
                        return;
                    case PartialJsonNullPolicy.Reject:
                        throw new InvalidOperationException($"Null not allowed for property '{name}' in partial JSON mode.");
                    default:
                        owner[name] = JValue.CreateNull();
                        return;
                }
            }

            var nonNullable = memberType.IsValueType && Nullable.GetUnderlyingType(memberType) is null;
            if (nonNullable && _merge == MergePatchNullPolicy.Reject)
            {
                throw new InvalidOperationException(
                    $"Null not allowed for non-nullable property '{name}' in merge-patch mode. Remove the member, or make the property nullable.");
            }

            // RFC 7386 removal must not resurrect constructor seeds: non-nullable members take an explicit CLR
            // default; nullable and reference members take an explicit null.
            owner[name] = nonNullable ? ClrDefault(memberType) : JValue.CreateNull();
        }

        public void ApplyDictionaryEntry(JObject entries, string key, Type valueType)
        {
            if (_partial is { } partial)
            {
                switch (partial)
                {
                    case PartialJsonNullPolicy.Ignore:
                        return;
                    case PartialJsonNullPolicy.Reject:
                        throw new InvalidOperationException($"Null not allowed for key '{key}' in partial JSON mode.");
                    default:
                        entries[key] = JValue.CreateNull();
                        return;
                }
            }

            if (valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null &&
                _merge == MergePatchNullPolicy.Reject)
            {
                throw new InvalidOperationException(
                    $"Null not allowed for non-nullable dictionary value '{key}' in merge-patch mode. Remove the entry, or make the value nullable.");
            }

            entries.Remove(key);
        }

        private static JToken ClrDefault(Type type)
        {
            if (type.IsEnum)
            {
                var zero = Enum.ToObject(type, 0);
                return Enum.IsDefined(type, zero) ? new JValue(zero.ToString()) : new JValue(0);
            }

            return JToken.FromObject(Activator.CreateInstance(type)!);
        }
    }
}
