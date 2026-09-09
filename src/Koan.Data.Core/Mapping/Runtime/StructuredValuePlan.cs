using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Annotations;
using Koan.Core.Reflection;

namespace Koan.Data.Core.Mapping.Runtime;

internal sealed class StructuredValuePlan
{
    private readonly Type _type;
    private readonly Func<object>? _factory;
    private readonly PropertyPlan[] _properties;
    private readonly StructuredValuePlan? _element;

    private StructuredValuePlan(Type type, Func<object>? factory, PropertyPlan[] properties, StructuredValuePlan? element = null)
    {
        _type = type;
        _factory = factory;
        _properties = properties;
        _element = element;
    }

    public static StructuredValuePlan Compile(Type type, IReadOnlySet<string>? excludedRootProperties = null)
        => CompileCore(type, excludedRootProperties, new HashSet<Type>(), 0);

    private static StructuredValuePlan CompileCore(
        Type type,
        IReadOnlySet<string>? excludedRootProperties,
        HashSet<Type> active,
        int depth)
    {
        var effective = Nullable.GetUnderlyingType(type) ?? type;
        if (depth >= 32)
            throw new InvalidOperationException($"Structured mapping for '{effective.FullName}' exceeds the maximum object depth of 32.");
        if (IsScalar(effective))
            return new StructuredValuePlan(effective, null, []);
        if (TryElementType(effective) is { } element)
            return new StructuredValuePlan(
                effective,
                null,
                [],
                IsScalar(element) ? null : CompileCore(element, null, active, depth + 1));
        if (!active.Add(effective))
            throw new InvalidOperationException(
                $"Structured mapping contains a recursive '{effective.FullName}' path. Exclude the relationship or map it through an explicit codec.");
        var ctor = effective.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"Structured mapping for '{effective.FullName}' requires a public parameterless constructor.");
        var factory = Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(ctor), typeof(object))).Compile();
        var properties = effective.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
            .Where(static property => property.GetCustomAttribute<NotMappedAttribute>(inherit: true) is null &&
                                      property.GetCustomAttribute<IgnoreStorageAttribute>(inherit: true) is null)
            .Where(property => excludedRootProperties is null || !excludedRootProperties.Contains(property.Name))
            .OrderBy(static property => DeclarationOrder.Of(property))
            .Select(property => CompileProperty(property, active, depth + 1))
            .ToArray();
        active.Remove(effective);
        return new StructuredValuePlan(effective, factory, properties);
    }

    public object? Project(object? value)
    {
        if (value is null) return null;
        return ProjectValue(value, _type, this);
    }

    public object? Materialize(object? value)
    {
        if (value is null) return null;
        if (_type.IsInstanceOfType(value)) return value;
        return MaterializeValue(value, _type, this);
    }

    private DataObject ProjectObject(object value)
    {
        var properties = new DataProperty[_properties.Length];
        for (var index = 0; index < _properties.Length; index++)
        {
            var property = _properties[index];
            var raw = property.Getter(value);
            properties[index] = new DataProperty(property.Name, ProjectValue(raw, property.Type, property.Structured));
        }
        return new DataObject(properties);
    }

    private object MaterializeObject(DataObject value)
    {
        if (_factory is null) throw new InvalidOperationException($"'{_type.FullName}' is not an object mapping.");
        var byName = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.Properties)
            if (!byName.TryAdd(property.Name, property.Value))
                throw new InvalidOperationException($"Structured value contains ambiguous duplicate property '{property.Name}'.");

        var instance = _factory();
        foreach (var property in _properties)
        {
            if (!byName.TryGetValue(property.Name, out var raw)) continue;
            if (property.Setter is null)
                throw new InvalidOperationException(
                    $"Structured property '{_type.FullName}.{property.Name}' is present but is not writable.");
            property.Setter(instance, MaterializeValue(raw, property.Type, property.Structured));
        }
        return instance;
    }

    private static object? ProjectValue(object? value, Type declaredType, StructuredValuePlan? plan)
    {
        if (value is null) return null;
        var effective = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (value is Enum enumeration) return EnumStorageEncoding.Format(enumeration);
        if (IsScalar(effective)) return value is byte[] bytes ? bytes.ToArray() : value;
        if (TryElementType(effective) is { } element && value is IEnumerable sequence)
        {
            var elementPlan = IsScalar(element) ? null : plan?._element;
            var values = sequence.Cast<object?>().Select(item => ProjectValue(item, element, elementPlan));
            return new DataArray(values);
        }
        return (plan ?? Compile(effective)).ProjectObject(value);
    }

    private static object? MaterializeValue(object? value, Type declaredType, StructuredValuePlan? plan)
    {
        if (value is null) return MappingValueConversion.To(null, declaredType);
        var effective = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (IsScalar(effective)) return MappingValueConversion.To(value, declaredType);
        if (TryElementType(effective) is { } element)
        {
            if (value is not DataArray array)
                throw new InvalidCastException($"Structured collection '{declaredType.FullName}' requires DataArray input.");
            var elementPlan = IsScalar(element) ? null : plan?._element;
            var converted = array.Items.Select(item => MaterializeValue(item, element, elementPlan)).ToArray();
            if (effective.IsArray)
            {
                var result = Array.CreateInstance(element, converted.Length);
                for (var index = 0; index < converted.Length; index++) result.SetValue(converted[index], index);
                return result;
            }
            var listType = typeof(List<>).MakeGenericType(element);
            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var item in converted) list.Add(item);
            if (effective.IsAssignableFrom(listType)) return list;
            var ctor = effective.GetConstructor([typeof(IEnumerable<>).MakeGenericType(element)]);
            return ctor is not null ? ctor.Invoke([list]) : list;
        }
        if (value is not DataObject dataObject)
            throw new InvalidCastException($"Structured object '{declaredType.FullName}' requires DataObject input.");
        return (plan ?? Compile(effective)).MaterializeObject(dataObject);
    }

    private static PropertyPlan CompileProperty(PropertyInfo property, HashSet<Type> active, int depth)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var access = Expression.Property(Expression.Convert(instance, property.DeclaringType!), property);
        var getter = Expression.Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), instance).Compile();
        Action<object, object?>? setter = null;
        if (property.SetMethod is not null)
        {
            var value = Expression.Parameter(typeof(object), "value");
            var call = Expression.Call(
                Expression.Convert(instance, property.DeclaringType!),
                property.SetMethod,
                Expression.Convert(value, property.PropertyType));
            setter = Expression.Lambda<Action<object, object?>>(call, instance, value).Compile();
        }
        var effective = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var structured = IsScalar(effective) ? null : CompileCore(effective, null, active, depth);
        return new PropertyPlan(property.Name, property.PropertyType, getter, setter, structured);
    }

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
        type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(byte[]);

    private static Type? TryElementType(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[])) return null;
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];
        return type.GetInterfaces()
            .FirstOrDefault(static candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private sealed record PropertyPlan(
        string Name,
        Type Type,
        Func<object, object?> Getter,
        Action<object, object?>? Setter,
        StructuredValuePlan? Structured);
}
