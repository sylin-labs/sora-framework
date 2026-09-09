using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Koan.Web.Authorization;

/// <summary>Each resolver belongs to one prepared operation, including its Newtonsoft contract cache.</summary>
internal sealed class FieldAccessContractResolver : Koan.Core.Json.KoanJsonContractResolver
{
    private readonly FieldAccess _access;

    public FieldAccessContractResolver(FieldAccess access, DefaultContractResolver? template)
    {
        _access = access;
        NamingStrategy = template?.NamingStrategy;
        if (template is not null)
        {
            SerializeCompilerGeneratedMembers = template.SerializeCompilerGeneratedMembers;
            IgnoreSerializableInterface = template.IgnoreSerializableInterface;
            IgnoreSerializableAttribute = template.IgnoreSerializableAttribute;
            IgnoreIsSpecifiedMembers = template.IgnoreIsSpecifiedMembers;
            IgnoreShouldSerializeMembers = template.IgnoreShouldSerializeMembers;
        }
    }

    protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
    {
        var properties = base.CreateProperties(type, memberSerialization);
        foreach (var property in properties)
        {
            if (FieldAccess.Member(property) is not { } member) continue;
            var read = _access.CanRead(member, type);
            var write = _access.CanWrite(member, type);
            var originalRead = property.ShouldSerialize;
            property.ShouldSerialize = value => read && (originalRead?.Invoke(value) ?? true);
            var originalWrite = property.ShouldDeserialize;
            property.ShouldDeserialize = value =>
            {
                if (!write) throw new JsonSerializationException($"Caller cannot write field '{property.PropertyName}'.");
                return originalWrite?.Invoke(value) ?? true;
            };
        }
        return properties;
    }
}
