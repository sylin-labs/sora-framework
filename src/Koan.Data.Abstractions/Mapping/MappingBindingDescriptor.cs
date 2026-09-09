namespace Koan.Data.Abstractions;

/// <summary>One immutable logical-value-to-physical-location decision.</summary>
public sealed record MappingBindingDescriptor
{
    public MappingBindingDescriptor(
        string id,
        MappingPath logicalPath,
        MappingRole role,
        Type logicalType,
        PhysicalPath physicalPath,
        MappingValueShape shape,
        MappingDirection direction = MappingDirection.ReadWrite,
        MappingGeneration generation = MappingGeneration.Application,
        MappingAuthority authority = MappingAuthority.Canonical,
        IDataMappingCodec? codec = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(logicalPath);
        ArgumentNullException.ThrowIfNull(logicalType);
        ArgumentNullException.ThrowIfNull(physicalPath);
        Id = id.Trim();
        LogicalPath = logicalPath;
        Role = role;
        LogicalType = logicalType;
        PhysicalPath = physicalPath;
        Shape = shape;
        Direction = direction;
        Generation = generation;
        Authority = authority;
        Codec = codec;
    }

    public string Id { get; }
    public MappingPath LogicalPath { get; }
    public MappingRole Role { get; }
    public Type LogicalType { get; }
    public PhysicalPath PhysicalPath { get; }
    public MappingValueShape Shape { get; }
    public MappingDirection Direction { get; }
    public MappingGeneration Generation { get; }
    public MappingAuthority Authority { get; }
    public IDataMappingCodec? Codec { get; }
    public Type PhysicalType => Codec?.PhysicalType ??
        ((Nullable.GetUnderlyingType(LogicalType) ?? LogicalType).IsEnum ? typeof(string) : LogicalType);
}
