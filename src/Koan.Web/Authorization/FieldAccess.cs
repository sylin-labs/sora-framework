using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Koan.Web.Hooks;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;

namespace Koan.Web.Authorization;

/// <summary>Operation-bound property admission shared by Web and protocol adapters. Application declarations use Access.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class FieldAccess
{
    private static readonly DefaultContractResolver MetadataResolver = new CamelCasePropertyNamesContractResolver();
    private static readonly ConcurrentDictionary<Type, Metadata> MetadataCache = new();
    private readonly Type _root;
    private ClaimsPrincipal _subject = new();
    private readonly Dictionary<(Type Type, PropertyInfo Member), (bool Read, bool Write)> _decisions = new();
    private readonly HashSet<Type> _types = [];
    private readonly Func<MemberInfo, bool>? _excludeInput;
    private readonly Func<MemberInfo, bool>? _excludeOutput;

    private FieldAccess(Type root, Func<MemberInfo, bool>? excludeInput, Func<MemberInfo, bool>? excludeOutput)
    {
        _root = root;
        _excludeInput = excludeInput;
        _excludeOutput = excludeOutput;
    }

    /// <summary>Whether the prepared typed contract contains conditional properties.</summary>
    public bool HasRestrictions => _decisions.Count != 0 || _types.SelectMany(type => Describe(type).Members)
        .Any(member => _excludeInput?.Invoke(member) == true || _excludeOutput?.Invoke(member) == true);

    /// <summary>Unknown declared runtime types need the same guarded serializer as known conditional members.</summary>
    public bool RequiresGuardedSerialization => HasRestrictions || _types.Any(IsUnresolved);

    private static bool IsUnresolved(Type type)
        => type == typeof(object) || MetadataResolver.ResolveContract(type) is JsonDynamicContract or JsonISerializableContract
            || !type.IsValueType && !type.IsSealed && MetadataResolver.ResolveContract(type) is JsonObjectContract
            || type.IsInterface && MetadataResolver.ResolveContract(type) is not JsonArrayContract and not JsonDictionaryContract;

    /// <summary>Refuse conditional typed contracts in a context-free serializer that cannot bind authority.</summary>
    public static void RequireUnconditional(Type rootType)
    {
        var pending = new Stack<Type>();
        var visited = new HashSet<Type>();
        pending.Push(rootType);
        while (pending.TryPop(out var type))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!visited.Add(type)) continue;
            var metadata = Describe(type);
            if (metadata.Members.Any(member => member.GetCustomAttribute<AccessAttribute>(true) is not null))
                throw new NotSupportedException("Conditional fields require a request-bound serializer. Use the governed endpoint or custom tool operation.");
            foreach (var child in metadata.Children) pending.Push(child);
        }
    }

    /// <summary>Prepare only the root's declared type closure. Never visits object values or captures authority globally.</summary>
    public static async Task<FieldAccess> Prepare(Type rootType, IServiceProvider services,
        ClaimsPrincipal principal, CancellationToken ct = default,
        Func<MemberInfo, bool>? excludeInput = null, Func<MemberInfo, bool>? excludeOutput = null)
    {
        var access = new FieldAccess(rootType, excludeInput, excludeOutput);
        IAccessGateCache? cache = null;
        IAgentGrantStore? grants = null;
        // A mutable caller principal cannot change the decisions halfway through preparation.
        var subject = new ClaimsPrincipal(principal.Identities.Select(identity => identity.Clone()));
        access._subject = subject;
        var pending = new Stack<Type>();
        pending.Push(rootType);
        while (pending.TryPop(out var type))
        {
            ct.ThrowIfCancellationRequested();
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!access._types.Add(type)) continue;
            var metadata = Describe(type);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Koan.Data.Core.Model.RelationshipGraph<>))
            {
                var relationships = services.GetRequiredService<Koan.Data.Core.Relationships.IRelationshipMetadata>();
                var entity = type.GetGenericArguments()[0];
                foreach (var (_, parent) in relationships.GetParentRelationships(entity)) pending.Push(parent);
                foreach (var (_, child) in relationships.GetChildRelationships(entity)) pending.Push(child);
            }
            foreach (var member in metadata.Members.OfType<PropertyInfo>().Where(member => member.GetCustomAttribute<AccessAttribute>(true) is not null))
            {
                if (cache is null)
                {
                    cache = services.GetRequiredService<IAccessGateCache>();
                    grants = services.GetService<IAgentGrantStore>();
                }
                var gate = cache.GetOrCompile(member);
                var read = await EntityFloorAuthorizationProvider.EvaluateGate(gate.For(EntityAuthorizeActions.Read),
                    type, subject, grants, ct).ConfigureAwait(false);
                var write = await EntityFloorAuthorizationProvider.EvaluateGate(gate.For(EntityAuthorizeActions.Write),
                    type, subject, grants, ct).ConfigureAwait(false);
                access._decisions[(type, member)] = (read is AuthorizeDecision.Allow, write is AuthorizeDecision.Allow);
            }
            foreach (var child in metadata.Children) pending.Push(child);
        }
        return access;
    }

    internal bool IsCurrent(Type rootType, ClaimsPrincipal principal)
        => rootType == _root && _subject.Identities.Count() == principal.Identities.Count()
            && _subject.Identities.Zip(principal.Identities).All(pair =>
                pair.First.AuthenticationType == pair.Second.AuthenticationType
                && pair.First.NameClaimType == pair.Second.NameClaimType
                && pair.First.RoleClaimType == pair.Second.RoleClaimType
                && pair.First.Claims.Select(ClaimValue).SequenceEqual(pair.Second.Claims.Select(ClaimValue)));

    private static (string, string, string, string, string) ClaimValue(Claim claim)
        => (claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer);

    internal Type RootType => _root;

    /// <summary>Admit fields copied into the framework's map/dict representation.</summary>
    public void DemandShape(string? shape)
    {
        if (!string.Equals(shape, "map", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(shape, "dict", StringComparison.OrdinalIgnoreCase)) return;
        DemandReadPath("Id");
        foreach (var name in new[] { "Name", "Title", "Label" })
            if (_root.GetProperty(name) is not null) DemandReadPath(name);
    }

    /// <summary>Read admission for a CLR property; an unprepared conditional runtime type refuses.</summary>
    public bool CanRead(MemberInfo member, Type? containingType = null) => Decision(member, containingType ?? _root).Read && _excludeOutput?.Invoke(member) != true;

    /// <summary>Write admission for a CLR property; an unprepared conditional runtime type refuses.</summary>
    public bool CanWrite(MemberInfo member, Type? containingType = null) => Decision(member, containingType ?? _root).Write && _excludeInput?.Invoke(member) != true;

    private (bool Read, bool Write) Decision(MemberInfo member, Type containingType)
    {
        if (member is not PropertyInfo property || property.GetCustomAttribute<AccessAttribute>(true) is null)
            return (true, true);
        if (_decisions.TryGetValue((containingType, property), out var decision)) return decision;
        throw new NotSupportedException($"Conditional field access for {property.DeclaringType?.Name}.{property.Name} was not prepared. Return a concrete typed contract instead of a polymorphic or dynamic governed value.");
    }

    /// <summary>Reject replacement when it could clear an omitted protected member.</summary>
    public void DemandReplacement()
    {
        foreach (var type in _types)
            foreach (var member in Describe(type).Members)
                if (!CanWrite(member, type)) Denied(member, "replace");
    }

    /// <summary>Validate a caller's dotted member path or JSON Pointer, including governed descendants.</summary>
    public void DemandReadPath(string path) => DemandPath(path, write: false);

    /// <summary>Validate a caller's assignment path, including governed descendants.</summary>
    public void DemandWritePath(string path) => DemandPath(path, write: true);

    /// <summary>Admit only caller-authored row filters; trusted server filters are not passed here.</summary>
    public void DemandFilter(Filter? filter)
    {
        switch (filter)
        {
            case null: return;
            case FieldFilter field:
                DemandReadPath(field.Field.ToString());
                return;
            case AllOf all:
                foreach (var child in all.Operands) DemandFilter(child);
                return;
            case AnyOf any:
                foreach (var child in any.Operands) DemandFilter(child);
                return;
            case Not not:
                DemandFilter(not.Operand);
                return;
            default:
                throw new NotSupportedException("Caller filters require typed row-field operations for field access admission.");
        }
    }

    /// <summary>Admit normalized patch paths, including disclosure and removal through from.</summary>
    public void DemandPatch(IEnumerable<PatchOp> operations)
    {
        foreach (var operation in operations)
        {
            switch (operation.Op.ToLowerInvariant())
            {
                case "test": DemandReadPath(operation.Path); break;
                case "copy":
                case "move":
                    if (string.IsNullOrEmpty(operation.From)) throw new InvalidOperationException("Patch copy/move requires from.");
                    DemandReadPath(operation.From);
                    if (operation.Op.Equals("move", StringComparison.OrdinalIgnoreCase)) DemandWritePath(operation.From);
                    DemandWritePath(operation.Path);
                    break;
                case "add":
                case "replace":
                case "remove": DemandWritePath(operation.Path); break;
                default: throw new NotSupportedException("This patch operation has no supported field access semantics.");
            }
        }
    }

    private void DemandPath(string path, bool write)
    {
        var parts = path.StartsWith('/')
            ? path[1..].Split('/').Select(part => part.Replace("~1", "/").Replace("~0", "~")).ToArray()
            : path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = _root;
        foreach (var part in parts.Where(part => part.Length != 0))
        {
            current = Nullable.GetUnderlyingType(current) ?? current;
            var contract = MetadataResolver.ResolveContract(current);
            if (contract is JsonArrayContract array)
            {
                current = array.CollectionItemType ?? typeof(object);
                if (part == "-" || int.TryParse(part, out _)) continue;
                contract = MetadataResolver.ResolveContract(current);
            }
            if (contract is JsonDictionaryContract dictionary)
            {
                current = dictionary.DictionaryValueType ?? typeof(object);
                continue;
            }
            if (contract is not JsonObjectContract obj)
                throw new NotSupportedException("Caller field access requires a concrete typed member path.");
            var property = obj.Properties.GetClosestMatchProperty(part)
                ?? obj.Properties.FirstOrDefault(property => string.Equals(property.UnderlyingName, part, StringComparison.OrdinalIgnoreCase));
            if (property is null || property.Ignored)
                throw new InvalidOperationException($"Unknown caller field '{part}'.");
            if (Member(property) is { } member && !(write ? CanWrite(member, current) : CanRead(member, current))) Denied(member, write ? "write" : "read");
            current = property.PropertyType ?? typeof(object);
        }
        DemandDescendants(current, write, []);
    }

    private void DemandDescendants(Type type, bool write, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!visited.Add(type)) return;
        var metadata = Describe(type);
        foreach (var member in metadata.Members)
            if (!(write ? CanWrite(member, type) : CanRead(member, type))) Denied(member, write ? "write" : "read");
        foreach (var child in metadata.Children) DemandDescendants(child, write, visited);
    }

    private static void Denied(MemberInfo member, string operation)
        => throw new UnauthorizedAccessException($"Caller cannot {operation} field '{member.Name}'.");

    /// <summary>Copy serializer settings and apply this operation's decisions plus optional stricter protocol exclusions.</summary>
    public JsonSerializerSettings CreateSerializerSettings(JsonSerializerSettings template)
    {
        if (!UsesStandardResolver(template.ContractResolver))
        {
            if (RequiresGuardedSerialization)
                throw new NotSupportedException("Conditional field access requires the standard Newtonsoft contract resolver. Remove the custom resolver for this typed response.");
            return new JsonSerializerSettings(template);
        }
        foreach (var type in _types)
        {
            if (!ContainsRestriction(type, [])) continue;
            var contract = MetadataResolver.ResolveContract(type);
            if (contract.Converter is not null || contract is JsonContainerContract { ItemConverter: not null }
                || template.Converters.Any(converter => converter.CanConvert(type) && !IsNativeProblemConverter(converter)))
                throw new NotSupportedException($"A custom converter for {type.Name} bypasses conditional field access. Return a typed response using the standard object contract.");
            if (contract is JsonObjectContract obj && obj.Properties.Any(property => (property.Converter is not null || property.ItemConverter is not null)
                && (Member(property) is { } member && member.GetCustomAttribute<AccessAttribute>(true) is not null
                    || property.PropertyType is { } child && ContainsRestriction(child, []))))
                throw new NotSupportedException($"A custom property converter on {type.Name} bypasses conditional field access.");
        }
        var settings = new JsonSerializerSettings(template)
        {
            ContractResolver = new FieldAccessContractResolver(this, template.ContractResolver as DefaultContractResolver)
        };
        return settings;
    }

    internal static bool UsesStandardResolver(IContractResolver? resolver)
        => resolver is null || resolver.GetType() == typeof(DefaultContractResolver)
            || resolver.GetType() == typeof(CamelCasePropertyNamesContractResolver)
            || resolver.GetType() == typeof(Koan.Core.Json.KoanJsonContractResolver);

    private bool ContainsRestriction(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!visited.Add(type)) return false;
        var metadata = Describe(type);
        return IsUnresolved(type) || metadata.Members.Any(member => member.GetCustomAttribute<AccessAttribute>(true) is not null
                || _excludeInput?.Invoke(member) == true || _excludeOutput?.Invoke(member) == true)
            || metadata.Children.Any(child => ContainsRestriction(child, visited));
    }

    // These sealed MVC converters serialize their annotated wrapper through the supplied serializer,
    // including each extension value. The operation resolver still guards every retained CLR value.
    private static bool IsNativeProblemConverter(JsonConverter converter)
        => converter is Microsoft.AspNetCore.Mvc.NewtonsoftJson.ProblemDetailsConverter
            or Microsoft.AspNetCore.Mvc.NewtonsoftJson.ValidationProblemDetailsConverter;

    internal static MemberInfo? Member(JsonProperty property)
    {
        var type = property.DeclaringType;
        var name = property.UnderlyingName ?? property.PropertyName!;
        return (MemberInfo?)type?.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static Metadata Describe(Type type) => MetadataCache.GetOrAdd(type, static type =>
    {
        if (type == typeof(object)) return new([], []);
        var contract = MetadataResolver.ResolveContract(type);
        if (contract is not JsonObjectContract && type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(member => member.GetCustomAttribute<AccessAttribute>(true) is not null))
            throw new NotSupportedException($"Conditional members on {type.Name} require a Newtonsoft object contract; custom serialization cannot bypass their gates.");
        return contract switch
        {
            JsonArrayContract array => new([], array.CollectionItemType is { } item ? [item] : []),
            JsonDictionaryContract dictionary => new([], dictionary.DictionaryValueType is { } value ? [value] : []),
            JsonObjectContract obj when !type.IsInterface => new(
                obj.Properties.Where(property => !property.Ignored || Member(property)?.GetCustomAttribute<AccessAttribute>(true) is not null).Select(Member).OfType<MemberInfo>().ToArray(),
                obj.Properties.Where(property => !property.Ignored).Select(property => property.PropertyType).OfType<Type>().ToArray()),
            _ => new([], [])
        };
    });

    private sealed record Metadata(MemberInfo[] Members, Type[] Children);

}
