using System.ComponentModel;

namespace Koan.Data.Abstractions.Filtering;

/// <summary>Opaque provider target captured under Data's guarded counterpart scope. It contains no caller-supplied physical route.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract record CounterpartQueryTarget(Type EntityType);
