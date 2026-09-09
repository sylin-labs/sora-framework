namespace Koan.Data.Abstractions.Filtering;

/// <summary>Requires a same-Entity, same-identity row in the selected partition to match a predicate.
/// Null inherits the outer partition; empty selects the default partition.</summary>
public sealed record SameIdInFilter(Type EntityType, Filter Predicate, string? Partition) : Filter;
