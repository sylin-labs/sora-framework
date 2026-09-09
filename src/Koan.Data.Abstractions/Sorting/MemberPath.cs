using System.Reflection;
using System.Text;

namespace Koan.Data.Abstractions.Sorting;

/// <summary>
/// A resolved chain of member accesses against a root entity type. Produced by the parser
/// or LINQ expression visitor; consumed by adapters to translate to native sort syntax.
/// Never carries an unresolved or invalid path — construction fails fast.
/// </summary>
public sealed record MemberPath
{
    /// <summary>The entity type the path was resolved against.</summary>
    public Type RootType { get; }

    /// <summary>Ordered chain of MemberInfo from root to leaf (always Property in practice, Field allowed).</summary>
    public IReadOnlyList<MemberInfo> Members { get; }

    /// <summary>Canonical dot-notation path (e.g. "Sightings.LastChangedAt"). Uses member names as declared.</summary>
    public string DotPath { get; }

    /// <summary>The type of the leaf member's value (for collection-aggregating paths, this is the element type's scalar).</summary>
    public Type ValueType { get; }

    /// <summary>True when any segment crossed into a collection (IEnumerable&lt;T&gt; excluding string).</summary>
    public bool TraversesCollection { get; }

    /// <summary>
    /// The 0-based index of the first member whose declaring step was a collection traversal.
    /// -1 when <see cref="TraversesCollection"/> is false.
    /// </summary>
    public int CollectionSegmentIndex { get; }

    public MemberPath(
        Type rootType,
        IReadOnlyList<MemberInfo> members,
        Type valueType,
        bool traversesCollection,
        int collectionSegmentIndex)
    {
        RootType = rootType ?? throw new ArgumentNullException(nameof(rootType));
        ArgumentNullException.ThrowIfNull(members);
        Members = Array.AsReadOnly(members.ToArray());
        if (Members.Count == 0) throw new ArgumentException("MemberPath must have at least one segment.", nameof(members));
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        TraversesCollection = traversesCollection;
        CollectionSegmentIndex = collectionSegmentIndex;

        var sb = new StringBuilder();
        for (var i = 0; i < Members.Count; i++)
        {
            if (i > 0) sb.Append('.');
            sb.Append(Members[i].Name);
        }
        DotPath = sb.ToString();
    }

    public override string ToString() => DotPath;

    public bool Equals(MemberPath? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (RootType != other.RootType) return false;
        if (Members.Count != other.Members.Count) return false;
        for (var i = 0; i < Members.Count; i++)
        {
            if (!Members[i].Equals(other.Members[i])) return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RootType);
        foreach (var m in Members) hash.Add(m);
        return hash.ToHashCode();
    }
}
