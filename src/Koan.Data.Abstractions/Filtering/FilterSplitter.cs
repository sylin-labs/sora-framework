namespace Koan.Data.Abstractions.Filtering;

/// <summary>The pushable and residual halves of a split filter. Either may be null.</summary>
public sealed record FilterSplit(Filter? Pushable, Filter? Residual);

/// <summary>
/// Splits a <see cref="Filter"/> into the part an adapter can push down (per its
/// <see cref="FilterSupport"/>) and the residual the caller evaluates in memory. The
/// split preserves results — <c>eval(full) == eval(pushable) AND eval(residual)</c> — by
/// following the only correctness-safe rules:
/// <list type="bullet">
/// <item><c>AllOf</c> splits per child (push some conjuncts, filter the rest in memory).</item>
/// <item><c>AnyOf</c> is pushed only if <b>every</b> child is fully pushable; otherwise the
/// whole disjunction is residual (pushing part of an OR would drop matching rows).</item>
/// <item><c>Not</c> is pushed only if its operand is fully pushable.</item>
/// <item><c>FieldFilter</c> is pushed only if the operator (on its field kind), nested-path,
/// and ignore-case requirements are all within capabilities.</item>
/// <item><c>ClrFilter</c> is always residual.</item>
/// </list>
/// This is the one shared algorithm behind partial pushdown; adapters do not reimplement it.
/// </summary>
public static class FilterSplitter
{
    public static FilterSplit Split(Filter filter, FilterSupport caps, Type entityType)
    {
        switch (filter)
        {
            case SameIdInFilter same:
                return caps.SupportsSameIdIn && same.EntityType == entityType && !Filter.HasCounterpart(same.Predicate)
                    && Split(same.Predicate, caps, entityType).Residual is null
                    ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            case BoundSameIdInFilter bound:
                return caps.SupportsSameIdIn && bound.Target.EntityType == entityType && !Filter.HasCounterpart(bound.Predicate)
                    && Split(bound.Predicate, caps, entityType).Residual is null
                    ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            case AllOf all:
            {
                if (all.Operands.Count == 0) return new FilterSplit(filter, null); // match-all, trivially pushable
                var push = new List<Filter>();
                var res = new List<Filter>();
                foreach (var child in all.Operands)
                {
                    var s = Split(child, caps, entityType);
                    if (s.Pushable is not null) push.Add(s.Pushable);
                    if (s.Residual is not null) res.Add(s.Residual);
                }
                return new FilterSplit(
                    push.Count == 0 ? null : Conjoin(push),
                    res.Count == 0 ? null : Conjoin(res));
            }
            case AnyOf any:
            {
                // A disjunction can only be pushed wholesale; partial push would lose rows.
                var fullyPushable = any.Operands.All(c => Split(c, caps, entityType).Residual is null);
                return fullyPushable ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            }
            case Not n:
            {
                if (!caps.SupportsNegation) return new FilterSplit(null, filter);
                var inner = Split(n.Operand, caps, entityType);
                return inner.Residual is null ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            }
            case FieldFilter f:
            {
                var resolved = FieldPathResolver.Resolve(entityType, f.Field);
                var pushable = caps.CanPush(f.Operator, resolved.TargetsCollection)
                    && (caps.NestedPaths || f.Field.Segments.Count == 1)
                    && (!f.IgnoreCase || caps.IgnoreCase);
                return pushable ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            }
            case ClrFilter:
                return new FilterSplit(null, filter);
            default:
                return new FilterSplit(null, filter);
        }
    }

    /// <summary>
    /// Schemaless split for the vector path (AI-0036 §9 / DATA-0097 P1). Same correctness-safe
    /// AllOf/AnyOf/Not rules as <see cref="Split(Filter, FilterSupport, Type)"/>, but the
    /// <see cref="FieldFilter"/> arm <b>never calls <see cref="FieldPathResolver"/></b> (vector
    /// metadata has no CLR type to resolve against) — every leaf is treated as a single metadata key,
    /// and capability is the schemaless single-set <see cref="FilterSupport"/> (Scalar==Collection via
    /// <see cref="FilterSupport.Uniform"/>). The <c>VectorFilterCoordinator</c> treats any non-empty
    /// residual as a hard error (no in-memory floor for vectors), so this method only decides
    /// pushable-vs-residual; it never evaluates.
    /// </summary>
    public static FilterSplit Split(Filter filter, FilterSupport caps)
    {
        switch (filter)
        {
            case AllOf all:
            {
                if (all.Operands.Count == 0) return new FilterSplit(filter, null); // match-all
                var push = new List<Filter>();
                var res = new List<Filter>();
                foreach (var child in all.Operands)
                {
                    var s = Split(child, caps);
                    if (s.Pushable is not null) push.Add(s.Pushable);
                    if (s.Residual is not null) res.Add(s.Residual);
                }
                return new FilterSplit(
                    push.Count == 0 ? null : Conjoin(push),
                    res.Count == 0 ? null : Conjoin(res));
            }
            case AnyOf any:
            {
                var fullyPushable = any.Operands.All(c => Split(c, caps).Residual is null);
                return fullyPushable ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            }
            case Not n:
            {
                if (!caps.SupportsNegation) return new FilterSplit(null, filter);
                var inner = Split(n.Operand, caps);
                return inner.Residual is null ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            }
            case FieldFilter f:
            {
                // No FieldPathResolver: schemaless metadata key, no scalar-vs-collection distinction
                // (FilterSupport is built via Uniform, so Scalar==Collection — collectionField is moot).
                var pushable = caps.CanPush(f.Operator, collectionField: false)
                    && (caps.NestedPaths || f.Field.Segments.Count == 1)
                    && (!f.IgnoreCase || caps.IgnoreCase);
                return pushable ? new FilterSplit(filter, null) : new FilterSplit(null, filter);
            }
            case ClrFilter:
                return new FilterSplit(null, filter); // opaque CLR predicate — never pushable, => hard error
            default:
                return new FilterSplit(null, filter);
        }
    }

    private static Filter Conjoin(List<Filter> items) => items.Count == 1 ? items[0] : new AllOf(items);
}
