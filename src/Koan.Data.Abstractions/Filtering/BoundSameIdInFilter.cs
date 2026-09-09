using System.ComponentModel;

namespace Koan.Data.Abstractions.Filtering;

/// <summary>Data-qualified counterpart predicate and its provider-owned physical target.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record BoundSameIdInFilter(Filter Predicate, CounterpartQueryTarget Target) : Filter;
