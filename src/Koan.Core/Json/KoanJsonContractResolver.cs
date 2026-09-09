using System.ComponentModel;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Koan.Core.Json;

/// <summary>Common JSON collection construction without persistence or authorization policy.</summary>
/// <remarks>Native Json.NET collection and constructor contracts remain authoritative.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public class KoanJsonContractResolver : DefaultContractResolver
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
}
