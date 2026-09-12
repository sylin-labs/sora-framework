using System.Collections;
using System.Linq.Expressions;

namespace Koan.Data.Abstractions.Filtering;

/// <summary>
/// Lowers a raw LINQ predicate into the normalized <see cref="Filter"/> AST so hand-written
/// <c>x =&gt; x.Games.Contains("ffxiv")</c> converges with the JSON DSL onto identical nodes
/// (same pushdown, same fallback, same results). Recognized shapes — logical
/// <c>&amp;&amp;/||/!</c>, the six comparisons, string <c>StartsWith/EndsWith/Contains</c>,
/// collection/scalar <c>Contains</c>, and <c>collection.Any(i =&gt; i.Contains(s))</c> (lifted to
/// <see cref="FilterOperator.HasContains"/>) — become structured nodes; anything else (arbitrary
/// method calls, computed expressions) becomes a <see cref="ClrFilter"/> that the evaluator
/// runs in memory and translators treat as residual. A member chain is only treated as a
/// <see cref="FieldPath"/> when it actually resolves against the entity, so expressions like
/// <c>x.Games.Count &gt; 1</c> fall to <see cref="ClrFilter"/> rather than a bogus field.
/// </summary>
public static class LinqFilterCompiler
{
    public static Filter Compile<T>(Expression<Func<T, bool>> predicate)
        => Visit(predicate.Body, predicate.Parameters[0], typeof(T));

    private static Filter Visit(Expression e, ParameterExpression param, Type rootType)
    {
        // A closed (param-free) boolean subexpression is a compile-time constant: fold it. This keeps
        // C# short-circuit semantics (see CombineAnd/CombineOr) and avoids eagerly evaluating a captured
        // expression the runtime would never reach — e.g. `severity.Value` guarded by `severity == null`.
        if (TryConstBool(e, param, out var folded))
            return folded ? MatchAll : MatchNone;

        switch (e)
        {
            case BinaryExpression b when b.NodeType is ExpressionType.AndAlso or ExpressionType.And:
                return CombineAnd(b, param, rootType);
            case BinaryExpression b when b.NodeType is ExpressionType.OrElse or ExpressionType.Or:
                return CombineOr(b, param, rootType);
            case UnaryExpression u when u.NodeType == ExpressionType.Not:
                return new Not(Visit(u.Operand, param, rootType));
            case UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
                return Visit(u.Operand, param, rootType);
            case BinaryExpression b when IsComparison(b.NodeType):
                return Comparison(b, param, rootType);
            case MethodCallExpression mc when TryMethod(mc, param, rootType, out var mf):
                return mf;
            case MemberExpression m when m.Type == typeof(bool) && TryMemberPath(m, param, rootType, out var bp):
                return new FieldFilter(bp, FilterOperator.Eq, FilterValue.Of(true));
            default:
                return Opaque(e, param);
        }
    }

    // Logical &&/|| mirror C# short-circuit: a constant operand collapses the node WITHOUT visiting
    // (and thus without evaluating) the other side. The DATA-0096 refactor dropped this, so the
    // optional-filter idiom `param == null || x.F == param.Value` evaluated `param.Value` on a null
    // nullable and threw "Nullable object must have a value" (DATA-XXXX regression).
    private static Filter CombineAnd(BinaryExpression b, ParameterExpression param, Type rootType)
    {
        if (TryConstBool(b.Left, param, out var lv)) return lv ? Visit(b.Right, param, rootType) : MatchNone;
        if (TryConstBool(b.Right, param, out var rv)) return rv ? Visit(b.Left, param, rootType) : MatchNone;
        return new AllOf(new[] { Visit(b.Left, param, rootType), Visit(b.Right, param, rootType) });
    }

    private static Filter CombineOr(BinaryExpression b, ParameterExpression param, Type rootType)
    {
        if (TryConstBool(b.Left, param, out var lv)) return lv ? MatchAll : Visit(b.Right, param, rootType);
        if (TryConstBool(b.Right, param, out var rv)) return rv ? MatchAll : Visit(b.Left, param, rootType);
        return new AnyOf(new[] { Visit(b.Left, param, rootType), Visit(b.Right, param, rootType) });
    }

    private static Filter Comparison(BinaryExpression b, ParameterExpression param, Type rootType)
    {
        // A field-vs-constant comparison becomes a FieldFilter; the value side must be a CLOSED
        // constant. If it references the entity (field-to-field, e.g. x.Start < x.End, or an
        // entity-derived expression) it is not a field/value filter and must not be Eval()'d — that
        // would throw — so it falls to an in-memory ClrFilter (same class as the short-circuit fix).
        if (TryMemberPath(b.Left, param, rootType, out var pl) && !ReferencesParam(b.Right, param))
            return new FieldFilter(pl, MapOperator(b.NodeType), FilterValue.Of(Eval(b.Right)));
        if (TryMemberPath(b.Right, param, rootType, out var pr) && !ReferencesParam(b.Left, param))
            return new FieldFilter(pr, MapOperator(Flip(b.NodeType)), FilterValue.Of(Eval(b.Left)));
        return Opaque(b, param);
    }

    private static bool TryMethod(MethodCallExpression mc, ParameterExpression param, Type rootType, out Filter filter)
    {
        filter = null!;

        // string instance methods: StartsWith / EndsWith / Contains
        if (mc.Method.DeclaringType == typeof(string) && mc.Object is not null && mc.Arguments.Count == 1
            && TryMemberPath(mc.Object, param, rootType, out var sp) && !ReferencesParam(mc.Arguments[0], param))
        {
            FilterOperator? op = mc.Method.Name switch
            {
                nameof(string.StartsWith) => FilterOperator.StartsWith,
                nameof(string.EndsWith) => FilterOperator.EndsWith,
                nameof(string.Contains) => FilterOperator.Contains,
                _ => null
            };
            if (op is { } sop) { filter = new FieldFilter(sp, sop, FilterValue.Of(Eval(mc.Arguments[0]))); return true; }
        }

        // collection / scalar Contains. C# 14 prefers MemoryExtensions.Contains for array receivers,
        // represented as an array-to-ReadOnlySpan op_Implicit inside the expression tree. Normalize
        // that exact standard-library shape back to its closed array before evaluating the set.
        if (TryContainsOperands(mc, out var source, out var item))
        {
            // member-collection.Contains(constant) -> Has. The item must be a closed constant
            // (not merely a non-path): an entity-derived item must not be Eval()'d.
            if (IsCollectionType(source.Type) && TryMemberPath(source, param, rootType, out var colPath)
                && !ReferencesParam(item, param))
            {
                filter = new FieldFilter(colPath, FilterOperator.Has, FilterValue.Of(Eval(item)));
                return true;
            }
            // constant-collection.Contains(member-scalar) -> In. The source set must be closed.
            if (TryMemberPath(item, param, rootType, out var scalarPath)
                && !ReferencesParam(source, param))
            {
                filter = new FieldFilter(scalarPath, FilterOperator.In, ToSet(Eval(source)));
                return true;
            }
        }

        // collection.Any(i => i.Contains(constantString)) -> HasContains (some element contains the value).
        // The element receiver must be the lambda parameter itself and the argument a closed string
        // constant; any other body (StartsWith/EndsWith, computed, captured, nested member) stays an
        // un-lifted ClrFilter, exactly as before.
        if (mc.Method.Name == nameof(Enumerable.Any) && mc.Method.DeclaringType == typeof(Enumerable)
            && mc.Arguments.Count == 2
            && Unwrap(mc.Arguments[1]) is LambdaExpression lambda && lambda.Parameters.Count == 1
            && Unwrap(lambda.Body) is MethodCallExpression inner
            && inner.Method.DeclaringType == typeof(string) && inner.Object is not null
            && inner.Method.Name == nameof(string.Contains) && inner.Arguments.Count == 1
            && inner.Arguments[0].Type == typeof(string)
            && ReferenceEquals(Unwrap(inner.Object), lambda.Parameters[0])
            && IsCollectionType(mc.Arguments[0].Type)
            && TryMemberPath(mc.Arguments[0], param, rootType, out var anyPath)
            && FieldPathResolver.Resolve(rootType, anyPath).ComparableType == typeof(string)
            && !ReferencesParam(inner.Arguments[0], param)
            && !ReferencesParam(inner.Arguments[0], lambda.Parameters[0]))
        {
            filter = new FieldFilter(anyPath, FilterOperator.HasContains, FilterValue.Of(Eval(inner.Arguments[0])));
            return true;
        }

        return false;
    }

    private static Filter Opaque(Expression e, ParameterExpression param) => new ClrFilter(Expression.Lambda(e, param));

    // --- helpers ---

    private static bool IsComparison(ExpressionType t) => t is ExpressionType.Equal or ExpressionType.NotEqual
        or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static FilterOperator MapOperator(ExpressionType t) => t switch
    {
        ExpressionType.Equal => FilterOperator.Eq,
        ExpressionType.NotEqual => FilterOperator.Ne,
        ExpressionType.GreaterThan => FilterOperator.Gt,
        ExpressionType.GreaterThanOrEqual => FilterOperator.Gte,
        ExpressionType.LessThan => FilterOperator.Lt,
        ExpressionType.LessThanOrEqual => FilterOperator.Lte,
        _ => throw new NotSupportedException($"Comparison '{t}' is not supported.")
    };

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t
    };

    private static bool IsCollectionType(Type t) => FieldPathResolver.TryGetElementType(t) is not null;

    private static bool TryContainsOperands(MethodCallExpression call, out Expression source, out Expression item)
    {
        source = item = null!;
        var method = call.Method;
        if (method.Name != nameof(Enumerable.Contains) || method.ReturnType != typeof(bool)) return false;

        // Preserve supported instance collection calls while requiring an actual collection receiver
        // and a one-element membership signature rather than matching an arbitrary method by name.
        if (!method.IsStatic && call.Object is not null && call.Arguments.Count == 1
            && IsCollectionType(call.Object.Type) && method.GetParameters().Length == 1)
        {
            source = call.Object;
            item = call.Arguments[0];
            return true;
        }

        // Enumerable.Contains<T>(IEnumerable<T>, T), excluding the comparer overload.
        if (method.IsStatic && method.DeclaringType == typeof(Enumerable)
            && method.IsGenericMethod && method.GetGenericArguments().Length == 1
            && call.Arguments.Count == 2 && method.GetParameters().Length == 2)
        {
            source = call.Arguments[0];
            item = call.Arguments[1];
            return true;
        }

        // MemoryExtensions.Contains<T>(ReadOnlySpan<T>, T), excluding comparer and char/string
        // overloads. Only the compiler's standard T[] -> span conversion is safe to unwrap: arbitrary
        // span producers cannot be evaluated through Expression.Compile and remain a ClrFilter.
        if (method.IsStatic && method.DeclaringType == typeof(MemoryExtensions)
            && method.IsGenericMethod && method.GetGenericArguments() is [var elementType]
            && call.Arguments.Count == 2 && method.GetParameters() is [var spanParameter, var itemParameter]
            && IsSpanOf(spanParameter.ParameterType, elementType)
            && itemParameter.ParameterType == elementType
            && TryUnwrapArrayToSpan(call.Arguments[0], spanParameter.ParameterType, elementType, out var array))
        {
            source = array;
            item = call.Arguments[1];
            return true;
        }

        return false;
    }

    private static bool TryUnwrapArrayToSpan(
        Expression expression,
        Type expectedSpanType,
        Type elementType,
        out Expression array)
    {
        array = null!;

        if (expression is UnaryExpression conversion
            && conversion.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
            && conversion.Method is { IsSpecialName: true, Name: "op_Implicit" } conversionMethod
            && conversion.Type == expectedSpanType
            && IsSpanType(conversionMethod.DeclaringType))
        {
            expression = conversion.Operand;
        }
        else if (expression is MethodCallExpression conversionCall
            && conversionCall.Object is null && conversionCall.Arguments.Count == 1
            && conversionCall.Method is { IsSpecialName: true, Name: "op_Implicit" }
            && conversionCall.Type == expectedSpanType
            && IsSpanType(conversionCall.Method.DeclaringType))
        {
            expression = conversionCall.Arguments[0];
        }
        else
        {
            return false;
        }

        expression = Unwrap(expression);
        if (!expression.Type.IsArray || expression.Type.GetArrayRank() != 1
            || expression.Type.GetElementType() != elementType)
            return false;

        array = expression;
        return true;
    }

    private static bool IsSpanOf(Type type, Type elementType)
        => IsSpanType(type) && type.GetGenericArguments()[0] == elementType;

    private static bool IsSpanType(Type? type)
        => type is { IsGenericType: true }
            && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(Span<>) || definition == typeof(ReadOnlySpan<>));

    private static bool TryMemberPath(Expression e, ParameterExpression param, Type rootType, out FieldPath path)
    {
        path = null!;
        var segments = new List<string>();
        var current = Unwrap(e);
        while (current is MemberExpression m)
        {
            segments.Insert(0, m.Member.Name);
            current = m.Expression is null ? null! : Unwrap(m.Expression);
        }
        if (!ReferenceEquals(current, param) || segments.Count == 0) return false;

        var candidate = FieldPath.Of(segments.ToArray());
        // Only a genuine entity path counts; CLR member access like .Count/.Length that the
        // model can't resolve falls through to a ClrFilter instead of a bogus field.
        try { FieldPathResolver.Resolve(rootType, candidate); }
        catch (InvalidFilterFieldException) { return false; }

        path = candidate;
        return true;
    }

    private static Expression Unwrap(Expression e)
        => e is UnaryExpression u && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
            ? Unwrap(u.Operand)
            : e;

    private static object? Eval(Expression e)
    {
        if (e is ConstantExpression c) return c.Value;
        return Expression.Lambda(Unwrap(e)).Compile().DynamicInvoke();
    }

    private static readonly Filter MatchAll = new AllOf(Array.Empty<Filter>());
    private static readonly Filter MatchNone = new Not(new AllOf(Array.Empty<Filter>()));

    /// <summary>
    /// True when <paramref name="e"/> is a boolean expression that does not reference the entity
    /// parameter — a closed constant we can evaluate now (and therefore short-circuit on).
    /// </summary>
    private static bool TryConstBool(Expression e, ParameterExpression param, out bool value)
    {
        value = false;
        if (e.Type != typeof(bool) || ReferencesParam(e, param)) return false;
        if (Eval(e) is bool b) { value = b; return true; }
        return false;
    }

    private static bool ReferencesParam(Expression e, ParameterExpression param)
    {
        var finder = new ParameterFinder(param);
        finder.Visit(e);
        return finder.Found;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly ParameterExpression _param;
        public bool Found { get; private set; }
        public ParameterFinder(ParameterExpression param) => _param = param;
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, _param)) Found = true;
            return base.VisitParameter(node);
        }
    }

    private static FilterValue ToSet(object? value)
    {
        var list = new List<object?>();
        if (value is IEnumerable en and not string)
            foreach (var x in en) list.Add(x);
        return FilterValue.Many(list);
    }
}
