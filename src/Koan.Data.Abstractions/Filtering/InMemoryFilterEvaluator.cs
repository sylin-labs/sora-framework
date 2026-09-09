using System.Collections;

namespace Koan.Data.Abstractions.Filtering;

/// <summary>
/// Executes a <see cref="Filter"/> against in-memory entities. This serves two roles:
/// the bounded in-memory fallback floor (for adapters that cannot push a node down) and
/// the convergence oracle (the reference result every adapter's native pushdown must match).
///
/// Implements the locked null/<c>Nin</c> semantics (DATA-XXXX §7): null/absent is not a
/// member of any set, so <see cref="FilterOperator.Nin"/> / <see cref="FilterOperator.HasNone"/>
/// match null/missing while <see cref="FilterOperator.In"/> / <see cref="FilterOperator.HasAny"/>
/// do not. Relational comparisons (<c>Gt/Gte/Lt/Lte</c>) are false when either side is null.
/// </summary>
public static class InMemoryFilterEvaluator
{
    /// <summary>Compile a complete row guard for an adapter that fences the observed document using native CAS.
    /// Binary and DateTime equality are not qualified for conditional writes by this evaluator.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static Func<T, bool> CompileConditional<T>(Filter filter)
    {
        Filter.RequireRowOnly(filter, "Conditional replacement");
        Demand(filter);
        return Compile<T>(filter);

        static void Demand(Filter guard)
        {
            switch (guard)
            {
                case AllOf all: foreach (var item in all.Operands) Demand(item); break;
                case AnyOf any: foreach (var item in any.Operands) Demand(item); break;
                case Not not: Demand(not.Operand); break;
                case FieldFilter field:
                    var resolved = FieldPathResolver.Resolve(typeof(T), field.Field);
                    if (resolved.IsManaged || resolved.ComparableType == typeof(DateTime) ||
                        resolved.ComparableType == typeof(byte[]) || resolved.LeafType == typeof(byte[]) ||
                        field.Value is FilterValue.Scalar { Value: DateTime or byte[] } ||
                        field.Value is FilterValue.Set set && set.Values.Any(value => value is DateTime or byte[]))
                        throw new NotSupportedException("Document CAS guards evaluated in memory do not support managed, DateTime or binary fields. Use an immutable string, Guid or numeric revision.");
                    break;
                default: throw new NotSupportedException("Conditional replacement requires a complete normalized row predicate.");
            }
        }
    }

    /// <summary>Compiles a filter into a reusable predicate over <typeparamref name="T"/> (fields resolved once).</summary>
    public static Func<T, bool> Compile<T>(Filter filter)
    {
        var predicate = Build(filter, typeof(T));
        return entity => entity is not null && predicate(entity);
    }

    /// <summary>Convenience: filter an in-memory sequence.</summary>
    public static IEnumerable<T> Apply<T>(IEnumerable<T> source, Filter filter)
        => source.Where(Compile<T>(filter));

    private static Func<object, bool> Build(Filter filter, Type rootType)
    {
        switch (filter)
        {
            case AllOf all:
            {
                var ps = all.Operands.Select(o => Build(o, rootType)).ToArray();
                return e => { foreach (var p in ps) if (!p(e)) return false; return true; };
            }
            case AnyOf any:
            {
                var ps = any.Operands.Select(o => Build(o, rootType)).ToArray();
                return e => { foreach (var p in ps) if (p(e)) return true; return false; };
            }
            case Not n:
            {
                var inner = Build(n.Operand, rootType);
                return e => !inner(e);
            }
            case ClrFilter clr:
            {
                var fn = clr.Predicate.Compile();
                return e => (bool)fn.DynamicInvoke(e)!;
            }
            case FieldFilter f:
                return BuildField(f, FieldPathResolver.Resolve(rootType, f.Field));
            default:
                throw new NotSupportedException($"Unknown filter node '{filter.GetType().Name}'.");
        }
    }

    private static Func<object, bool> BuildField(FieldFilter f, ResolvedField field)
    {
        var op = f.Operator;
        var ic = f.IgnoreCase;

        if (op == FilterOperator.Exists)
        {
            var desired = ScalarRaw(f.Value) as bool? ?? true;
            return e => (field.GetValue(e) is not null) == desired;
        }

        if (field.TargetsCollection)
        {
            if (op == FilterOperator.Size)
            {
                var count = (int)FilterValueConverter.Convert(ScalarRaw(f.Value), typeof(int))!;
                return e => Count(field.GetValue(e) as IEnumerable) == count;
            }

            var collSet = CoerceSet(f.Value, field.ComparableType);
            var collSingle = CoerceScalar(f.Value, field.ComparableType);
            return e => EvaluateCollection(op, field.GetValue(e) as IEnumerable, collSet, collSingle, ic);
        }

        if (op is FilterOperator.In or FilterOperator.Nin)
        {
            var set = CoerceSet(f.Value, field.ComparableType);
            // Nin matches null (null is not a member of any set) — locked semantics.
            return op == FilterOperator.In
                ? e => InSet(field.GetValue(e), set, ic)
                : e => !InSet(field.GetValue(e), set, ic);
        }

        var rhs = CoerceScalar(f.Value, field.ComparableType);
        var cmp = ic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return op switch
        {
            FilterOperator.Eq => e => ValEq(field.GetValue(e), rhs, ic),
            FilterOperator.Ne => e => !ValEq(field.GetValue(e), rhs, ic),
            FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte
                => e => CompareSatisfies(op, field.GetValue(e), rhs),
            FilterOperator.StartsWith => e => field.GetValue(e) is string s && rhs is string p && s.StartsWith(p, cmp),
            FilterOperator.EndsWith => e => field.GetValue(e) is string s && rhs is string p && s.EndsWith(p, cmp),
            FilterOperator.Contains => e => field.GetValue(e) is string s && rhs is string p && s.Contains(p, cmp),
            _ => throw new NotSupportedException($"Operator '{op}' is not valid on scalar field '{field}'.")
        };
    }

    private static bool EvaluateCollection(FilterOperator op, IEnumerable? col, List<object?> set, object? single, bool ic)
    {
        var items = Materialize(col);
        return op switch
        {
            FilterOperator.Has => items.Any(i => ValEq(i, single, ic)),
            FilterOperator.HasAny => items.Any(i => InSet(i, set, ic)),
            FilterOperator.HasAll => set.All(x => items.Any(i => ValEq(i, x, ic))),
            // null/empty collection is disjoint from any set -> HasNone matches (locked).
            FilterOperator.HasNone => !items.Any(i => InSet(i, set, ic)),
            // substring within a string element; null/missing/empty arrays match nothing, and a
            // non-string element never matches (the operator is declared on string collections only).
            FilterOperator.HasContains => items.Any(i => i is string s && single is string p && s.Contains(p, ic ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)),
            _ => throw new NotSupportedException($"Operator '{op}' is not valid on a collection field.")
        };
    }

    // --- value helpers ---

    private static object? ScalarRaw(FilterValue v) => v switch
    {
        FilterValue.Scalar s => s.Value,
        FilterValue.Set st => st.Values.Count > 0 ? st.Values[0] : null,
        _ => null
    };

    private static IReadOnlyList<object?> SetRaw(FilterValue v) => v switch
    {
        FilterValue.Set st => st.Values,
        FilterValue.Scalar s => new[] { s.Value },
        _ => Array.Empty<object?>()
    };

    private static object? CoerceScalar(FilterValue v, Type t) => FilterValueConverter.Convert(ScalarRaw(v), t);

    private static List<object?> CoerceSet(FilterValue v, Type t)
        => SetRaw(v).Select(x => FilterValueConverter.Convert(x, t)).ToList();

    private static bool ValEq(object? a, object? b, bool ic)
    {
        if (ic && a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        return a is null ? b is null : a.Equals(b);
    }

    private static bool InSet(object? value, IReadOnlyList<object?> set, bool ic)
    {
        foreach (var x in set) if (ValEq(value, x, ic)) return true;
        return false;
    }

    private static bool CompareSatisfies(FilterOperator op, object? a, object? b)
    {
        if (a is null || b is null) return false; // SQL-like: comparisons with null are false
        var c = Comparer.Default.Compare(a, b);
        return op switch
        {
            FilterOperator.Gt => c > 0,
            FilterOperator.Gte => c >= 0,
            FilterOperator.Lt => c < 0,
            FilterOperator.Lte => c <= 0,
            _ => false
        };
    }

    private static List<object?> Materialize(IEnumerable? col)
    {
        var list = new List<object?>();
        if (col is not null) foreach (var x in col) list.Add(x);
        return list;
    }

    private static int Count(IEnumerable? col)
    {
        if (col is null) return 0;
        if (col is ICollection c) return c.Count;
        var n = 0;
        foreach (var _ in col) n++;
        return n;
    }
}
