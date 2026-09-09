using System.Reflection;
using Koan.Data.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Koan.Data.Core.Polymorphism;

/// <summary>
/// Shapes how Koan reads and writes an Entity without changing domain models: it adds the serialize-only
/// Entity-family type hint, and it makes the round trip symmetric, so state Koan persists is state Koan restores.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public class EntityJsonContractResolver : DefaultContractResolver
{
    /// <summary>Restore additive domain collections that Json.NET serializes as arrays.</summary>
    protected override JsonArrayContract CreateArrayContract(Type objectType)
    {
        var contract = base.CreateArrayContract(objectType);
        var itemType = contract.CollectionItemType;
        if (itemType is null || objectType.IsAbstract || objectType.IsInterface
            || contract.OverrideCreator is not null || contract.HasParameterizedCreator
            || typeof(System.Collections.IList).IsAssignableFrom(objectType)
            || typeof(ICollection<>).MakeGenericType(itemType).IsAssignableFrom(objectType))
            return contract;
        var constructor = objectType.GetConstructor(Type.EmptyTypes);
        var add = objectType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public, [itemType]);
        if (constructor is null || add is null) return contract;
        var clear = objectType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
        contract.OverrideCreator = arguments =>
        {
            var collection = constructor.Invoke(null);
            if (((System.Collections.IEnumerable)collection).Cast<object?>().Any())
            {
                if (clear is null)
                    throw new JsonSerializationException($"Collection '{objectType.FullName}' seeds constructor values but has no public Clear(). Use an empty constructor or a JSON constructor that accepts the stored values.");
                clear.Invoke(collection, null);
            }
            if (arguments.Length > 0 && arguments[0] is System.Collections.IEnumerable values)
                foreach (var value in values) add.Invoke(collection, [value]);
            return collection;
        };
        contract.HasParameterizedCreator = true;
        return contract;
    }

    protected override JsonObjectContract CreateObjectContract(Type objectType)
    {
        var contract = base.CreateObjectContract(objectType);
        if (!EntityRootDescriptor.TryFor(objectType, out _) ||
            contract.ExtensionDataGetter is not { } extensionData)
        {
            return contract;
        }

        contract.ExtensionDataGetter = value =>
            RejectReservedExtensionData(objectType, extensionData(value) ?? []);
        return contract;
    }

    /// <summary>
    /// Restores state whose setter is not public, so an Entity round-trips through storage as the same Entity.
    ///
    /// <para>Json.NET writes a property whenever it can read it, but refuses to fill one whose setter is not public.
    /// That asymmetry is silent and lossy: the value reaches storage and is dropped on the way back, leaving a
    /// default in its place long after the write was reported as successful. Encapsulation governs what domain code
    /// may assign — a canonical id that only its own <c>Update</c> may set — and persistence restoring what
    /// persistence itself wrote does not weaken it.</para>
    ///
    /// <para>A property with no setter at all stays unwritable. It is computed, so it needs no restoring.</para>
    /// </summary>
    protected override JsonProperty CreateProperty(
        MemberInfo member,
        MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (property.Writable ||
            property.Ignored ||
            member is not PropertyInfo declared ||
            declared.GetSetMethod(nonPublic: true) is null)
        {
            return property;
        }

        property.Writable = true;
        return property;
    }

    protected override IList<JsonProperty> CreateProperties(
        Type type,
        MemberSerialization memberSerialization)
    {
        var properties = base.CreateProperties(type, memberSerialization);
        if (!EntityRootDescriptor.TryFor(type, out var descriptor))
        {
            return properties;
        }

        var reserved = properties.FirstOrDefault(static property =>
            string.Equals(
                property.PropertyName,
                EntityFamilyStorage.TypeField,
                StringComparison.OrdinalIgnoreCase));
        if (reserved is not null)
        {
            throw new InvalidOperationException(
                $"Entity '{type.FullName}' maps member '{reserved.UnderlyingName ?? reserved.PropertyName}' to reserved " +
                $"persistence field '{EntityFamilyStorage.TypeField}'. Rename or remap that member.");
        }

        if (!descriptor.IsVariant &&
            !EntityTypeCatalog.HasVariants(descriptor.RootType))
        {
            return properties;
        }

        properties.Add(new JsonProperty
        {
            PropertyName = EntityFamilyStorage.TypeField,
            UnderlyingName = EntityFamilyStorage.TypeField,
            PropertyType = typeof(string),
            DeclaringType = type,
            Readable = true,
            Writable = false,
            Order = int.MinValue,
            ValueProvider = RuntimeTypeValueProvider.Instance
        });

        return properties;
    }

    private static IEnumerable<KeyValuePair<object, object>> RejectReservedExtensionData(
        Type entityType,
        IEnumerable<KeyValuePair<object, object>> values)
    {
        foreach (var value in values)
        {
            if (string.Equals(
                    value.Key?.ToString(),
                    EntityFamilyStorage.TypeField,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Entity '{entityType.FullName}' contains extension data for reserved persistence field " +
                    $"'{EntityFamilyStorage.TypeField}'. Remove that key; Koan owns it for Entity-family identity.");
            }

            yield return value;
        }
    }

    private sealed class RuntimeTypeValueProvider : IValueProvider
    {
        public static RuntimeTypeValueProvider Instance { get; } = new();

        public object? GetValue(object target) => EntityTypeCatalog.TypeId(target.GetType());
        public void SetValue(object target, object? value) { }
    }
}
