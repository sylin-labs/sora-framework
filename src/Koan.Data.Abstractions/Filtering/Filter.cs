using System.Linq.Expressions;

namespace Koan.Data.Abstractions.Filtering;

/// <summary>
/// Provider-agnostic, immutable boolean predicate over an entity: the single normalized
/// filter model that both the JSON DSL (<c>JsonFilterParser</c>) and raw LINQ
/// (<c>LinqFilterCompiler</c>) lower into, that every adapter translates through
/// <c>IFilterTranslator</c>, and that <c>InMemoryFilterEvaluator</c> executes as the
/// fallback floor and convergence oracle.
///
/// Closed sealed-record hierarchy: exhaustive matching, structural equality, and no silent
/// operator drift. Promoted and generalized from the former Vector-only filter AST
/// (supersedes DATA-0056). Combinators are deliberately minimal — <c>$nor</c> lowers to
/// <c>Not(AnyOf(...))</c>, <c>$between</c> to <c>AllOf(Gte, Lte)</c>, and wildcard strings to
/// StartsWith/EndsWith/Contains at parse time — so the hierarchy carries no redundant nodes.
/// </summary>
public abstract record Filter
{
    /// <summary>Require the same identity in another partition to satisfy the supplied predicate.</summary>
    public static Filter SameIdIn<TEntity>(Expression<Func<TEntity, bool>> predicate, string? partition)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new SameIdInFilter(typeof(TEntity), Snapshot(LinqFilterCompiler.Compile(predicate), requireImmutableValues: true)!, partition);
    }

    /// <summary>Whether this tree requires a provider-bound counterpart read.</summary>
    public static bool HasCounterpart(Filter? filter) => filter switch
    {
        SameIdInFilter or BoundSameIdInFilter => true,
        AllOf all => all.Operands.Any(HasCounterpart),
        AnyOf any => any.Operands.Any(HasCounterpart),
        Not not => HasCounterpart(not.Operand),
        _ => false
    };

    /// <summary>Reject a database relationship where only an in-memory row predicate can be honored.</summary>
    public static void RequireRowOnly(Filter? filter, string operation)
    {
        if (HasCounterpart(filter))
            throw new NotSupportedException($"{operation} cannot enforce a counterpart predicate. Use a supported structured read; counterpart mutations are not supported.");
    }

    /// <summary>Compose optional predicates without introducing an empty conjunction.</summary>
    public static Filter? And(Filter? left, Filter? right)
        => left is null ? right : right is null ? left : All(left, right);

    /// <summary>Copy AST collections and binary values. Counterpart proofs reject opaque provider values;
    /// ordinary row filters retain their existing adapter scalar and CLR semantics.</summary>
    public static Filter? Snapshot(Filter? filter, bool requireImmutableValues = false)
        => SnapshotCore(filter, requireImmutableValues || HasCounterpart(filter));

    private static Filter? SnapshotCore(Filter? filter, bool strict) => filter switch
    {
        AllOf all => new AllOf(Array.AsReadOnly(all.Operands.Select(item => SnapshotCore(item, strict)!).ToArray())),
        AnyOf any => new AnyOf(Array.AsReadOnly(any.Operands.Select(item => SnapshotCore(item, strict)!).ToArray())),
        Not not => new Not(SnapshotCore(not.Operand, strict)!),
        FieldFilter field => field with
        {
            Field = field.Field with { Segments = Array.AsReadOnly(field.Field.Segments.ToArray()) },
            Value = field.Value switch
            {
                FilterValue.Set set => new FilterValue.Set(Array.AsReadOnly(set.Values.Select(value => SnapshotAtom(value, strict)).ToArray())),
                FilterValue.Scalar scalar => new FilterValue.Scalar(SnapshotAtom(scalar.Value, strict)),
                _ => field.Value
            }
        },
        SameIdInFilter same => same with { Predicate = SnapshotCore(same.Predicate, true)! },
        BoundSameIdInFilter bound => bound with { Predicate = SnapshotCore(bound.Predicate, true)! },
        _ => filter
    };

    /// <summary>Structural equality for normalized snapshots, including owned operand and value collections.</summary>
    public static bool Equivalent(Filter? left, Filter? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return (left, right) switch
        {
            (AllOf a, AllOf b) => SameOperands(a.Operands, b.Operands),
            (AnyOf a, AnyOf b) => SameOperands(a.Operands, b.Operands),
            (Not a, Not b) => Equivalent(a.Operand, b.Operand),
            (FieldFilter a, FieldFilter b) => a.Operator == b.Operator && a.IgnoreCase == b.IgnoreCase &&
                a.Field.ManagedClrType == b.Field.ManagedClrType && a.Field.Segments.SequenceEqual(b.Field.Segments) &&
                (a.Value is FilterValue.Set sa && b.Value is FilterValue.Set sb
                    ? SameValues(sa.Values, sb.Values) : a.Value is FilterValue.Scalar va && b.Value is FilterValue.Scalar vb ? EquivalentValue(va.Value, vb.Value) : Equals(a.Value, b.Value)),
            (SameIdInFilter a, SameIdInFilter b) => a.EntityType == b.EntityType && a.Partition == b.Partition && Equivalent(a.Predicate, b.Predicate),
            (BoundSameIdInFilter a, BoundSameIdInFilter b) => Equals(a.Target, b.Target) && Equivalent(a.Predicate, b.Predicate),
            (ClrFilter a, ClrFilter b) => ReferenceEquals(a.Predicate, b.Predicate),
            _ => false
        };
    }

    private static bool SameOperands(IReadOnlyList<Filter> left, IReadOnlyList<Filter> right)
        => left.Count == right.Count && left.Zip(right).All(pair => Equivalent(pair.First, pair.Second));

    private static bool SameValues(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
        => left.Count == right.Count && left.Zip(right).All(pair => EquivalentValue(pair.First, pair.Second));

    /// <summary>Conservative native atom equivalence shared by query and isolation snapshots.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static bool EquivalentValue(object? left, object? right)
        => (left, right) switch
        {
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            (DateTime a, DateTime b) => a.ToBinary() == b.ToBinary(),
            _ => Equals(left, right)
        };

    private static object? SnapshotAtom(object? value, bool strict)
    {
        if (value is null or string or decimal or Guid or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly ||
            value.GetType().IsPrimitive || value.GetType().IsEnum) return value;
        if (value is byte[] bytes) return bytes.ToArray();
        if (!strict) return value;
        throw new NotSupportedException($"Immutable filter snapshots cannot capture mutable or unsupported '{value.GetType().Name}' values. Use immutable scalar values or a set of immutable scalar values.");
    }

    /// <summary>Conjunction builder — every operand must match.</summary>
    public static Filter All(params Filter[] operands) => new AllOf(operands);

    /// <summary>Disjunction builder — at least one operand must match.</summary>
    public static Filter Any(params Filter[] operands) => new AnyOf(operands);

    /// <summary>Negation builder.</summary>
    public static Filter Negate(Filter operand) => new Not(operand);

    /// <summary>Field-predicate builder.</summary>
    public static Filter On(FieldPath field, FilterOperator op, FilterValue value) => new FieldFilter(field, op, value);

    public static Filter Eq(string field, object? value)
        => new FieldFilter(FieldPath.Of(field), FilterOperator.Eq, FilterValue.Of(value));

    public static Filter In(string field, IReadOnlyList<object?> values)
        => new FieldFilter(FieldPath.Of(field), FilterOperator.In, FilterValue.Many(values));

    public static Filter HasAny(string field, IReadOnlyList<object?> values)
        => new FieldFilter(FieldPath.Of(field), FilterOperator.HasAny, FilterValue.Many(values));

    public static Filter HasAll(string field, IReadOnlyList<object?> values)
        => new FieldFilter(FieldPath.Of(field), FilterOperator.HasAll, FilterValue.Many(values));
}

/// <summary>Conjunction — every operand must match.</summary>
public sealed record AllOf(IReadOnlyList<Filter> Operands) : Filter;

/// <summary>Disjunction — at least one operand must match.</summary>
public sealed record AnyOf(IReadOnlyList<Filter> Operands) : Filter;

/// <summary>Negation of a single inner predicate. <c>$nor</c> is modelled as <c>Not(AnyOf(...))</c>.</summary>
public sealed record Not(Filter Operand) : Filter;

/// <summary>
/// A predicate on a single (possibly nested) field of the entity. <paramref name="IgnoreCase"/>
/// requests case-insensitive comparison for string operators (folds in DATA-0031's
/// <c>$options.ignoreCase</c> as a per-node flag rather than a threaded mutable option).
/// </summary>
public sealed record FieldFilter(FieldPath Field, FilterOperator Operator, FilterValue Value, bool IgnoreCase = false) : Filter;

/// <summary>
/// An opaque CLR predicate the front-end could not lower into a translatable node
/// (e.g. an arbitrary lambda body from raw LINQ). It is never pushed down — it is the
/// residual the <c>FilterPushdownCoordinator</c> evaluates in memory via
/// <c>InMemoryFilterEvaluator</c>. Retains the original expression so it stays inspectable.
/// </summary>
public sealed record ClrFilter(LambdaExpression Predicate) : Filter;
