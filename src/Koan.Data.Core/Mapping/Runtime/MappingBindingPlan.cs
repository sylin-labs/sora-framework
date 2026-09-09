using Koan.Data.Abstractions;
using Koan.Data.Core.Mapping.Runtime;

namespace Koan.Data.Core;

/// <summary>One compiled binding with allocation-free warm-path access and one shared encoding.</summary>
public sealed class MappingBindingPlan
{
    private readonly Func<object, object?> _read;
    private readonly Action<object, object?>? _assign;
    private readonly StructuredValuePlan? _structured;

    internal MappingBindingPlan(
        MappingBindingDescriptor descriptor,
        Func<object, object?> read,
        Action<object, object?>? assign,
        StructuredValuePlan? structured)
    {
        Descriptor = descriptor;
        _read = read;
        _assign = assign;
        _structured = structured;
    }

    public MappingBindingDescriptor Descriptor { get; }
    public string Id => Descriptor.Id;
    public MappingPath LogicalPath => Descriptor.LogicalPath;
    public PhysicalPath PhysicalPath => Descriptor.PhysicalPath;
    public Type LogicalType => Descriptor.LogicalType;
    public Type PhysicalType => Descriptor.PhysicalType;
    public MappingValueShape Shape => Descriptor.Shape;

    public object? Read(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return _read(entity);
    }

    public object? Encode(object? logical)
    {
        if (Descriptor.Codec is { } codec) return codec.Encode(logical);
        if (logical is Enum enumeration) return EnumStorageEncoding.Format(enumeration);
        return Shape == MappingValueShape.Object ? _structured!.Project(logical) : logical;
    }

    public object? Decode(object? physical)
    {
        if (Descriptor.Codec is { } codec) return codec.Decode(physical);
        return Shape == MappingValueShape.Object
            ? _structured!.Materialize(physical)
            : MappingValueConversion.To(physical, LogicalType);
    }

    internal void Assign(object entity, object? physical)
    {
        if (_assign is null)
            throw new InvalidOperationException($"Logical path '{LogicalPath}' is not directly assignable.");
        _assign(entity, Decode(physical));
    }
}
