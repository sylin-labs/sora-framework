using System.Collections;

namespace Koan.Data.Abstractions;

/// <summary>
/// Lightweight shared type classification to help adapters decide storage mapping.
/// Adapters can use this to default complex types to JSON in relational providers.
/// </summary>
// ReSharper disable once InconsistentNaming
public static class TypeClassification
{
    /// <summary>
    /// Whether a CLR value's ordering is the <i>same on every</i> qualified adapter as it is in the framework's
    /// own sorter.
    ///
    /// <para>This does not gate anything. A stream orders by whatever the chosen provider can order, because
    /// holding every provider to what the weakest one manages helps nobody: it refused a string order on stores
    /// that order strings perfectly well, and offered "materialize the query" — load the whole set — in
    /// exchange. What this predicate decides is whether Koan says anything about the ordering it just used: a
    /// key outside this set is recorded as an order the store defines, so an application that needs the same
    /// sequence on a different backend can find out which keys will not give it.</para>
    ///
    /// <para>The list is evidence. Each type here is ordered identically by every adapter, ascending and
    /// descending, under the cross-adapter oracle <c>SortPushdownConvergence</c>. Widening it means extending
    /// that corpus and watching it pass everywhere.</para>
    ///
    /// <para>What is outside it, and why:</para>
    /// <list type="bullet">
    ///   <item><see cref="string"/>, <see cref="char"/> and <see cref="Guid"/> — collation, not values.
    ///   The same two rows can order differently under a different database collation.</item>
    ///   <item>Nullable values — provider null placement is not normalized for a plain column. A collection
    ///   aggregate counts as its element type, because the dialect states where its null belongs.</item>
    ///   <item><see cref="DateTime"/> — no offset, so its ordering depends on the kind each value carried
    ///   when it was written.</item>
    ///   <item><see cref="uint"/>, <see cref="ulong"/>, <see cref="float"/> — never put through the corpus.
    ///   Absence of proof keeps them out; that is the rule working, not a judgement about the types.</item>
    /// </list>
    /// </summary>
    public static bool IsPortableStreamSortScalar(Type t)
    {
        if (t is null) return false;
        if (t.IsEnum) return !t.IsDefined(typeof(FlagsAttribute), inherit: false);
        return t == typeof(bool) ||
               t == typeof(byte) ||
               t == typeof(sbyte) ||
               t == typeof(short) ||
               t == typeof(ushort) ||
               t == typeof(int) ||
               t == typeof(long) ||
               t == typeof(decimal) ||
               t == typeof(double) ||
               t == typeof(DateTimeOffset) ||
               t == typeof(DateOnly) ||
               t == typeof(TimeOnly) ||
               t == typeof(TimeSpan);
    }

    public static bool IsSimple(Type t)
    {
        if (t.IsPrimitive) return true;
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsEnum) return true;
        if (t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid)) return true;
        if (t == typeof(byte[]) || t == typeof(ReadOnlyMemory<byte>) || t == typeof(Memory<byte>)) return true;
        return false;
    }

    public static bool IsCollection(Type t)
    {
        if (t == typeof(string)) return false;
        return typeof(IEnumerable).IsAssignableFrom(t);
    }

    public static bool IsComplex(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (IsSimple(t)) return false;
        // Treat collections as complex (JSON by default in relational)
        if (IsCollection(t)) return true;
        // Any class/record/struct that's not simple becomes complex.
        return t.IsClass || (t.IsValueType && !t.IsPrimitive && !t.IsEnum && t != typeof(decimal));
    }
}
