using System.ComponentModel;

namespace Koan.Data.Abstractions;

/// <summary>Operation-local Data evidence for identities selected by a complete native predicate. It is not a reusable permission.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IQueryReadEvidence
{
    Type EntityType { get; }
    Type KeyType { get; }
    string? Partition { get; }
    bool CoversRows(IEnumerable<object> rows);
    bool IsCurrentScope();
}
