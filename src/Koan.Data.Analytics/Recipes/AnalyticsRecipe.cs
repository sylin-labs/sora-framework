using System.Linq.Expressions;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Analytics;
using Koan.Data.Abstractions.Filtering;

namespace Koan.Data.Analytics.Recipes;

/// <summary>
/// The fluid vocabulary for one declared question. Every member accepts a direct property expression —
/// the mapping knows where that property physically lives; more than a direct property is a v0 refusal
/// with a corrective, not a silent wrong answer.
/// </summary>
public sealed class AnalyticsRecipe<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private const string DirectMemberOnly = "only direct property expressions are expressible in this grammar version";

    internal Expression<Func<TEntity, bool>>? WhereExpression { get; private set; }
    internal List<AnalyticsParameterDeclaration> ParameterDeclarations { get; } = [];
    internal string? GroupMember { get; private set; }
    internal AnalyticsMeasureKind MeasureKind { get; private set; } = AnalyticsMeasureKind.Count;
    internal string? MeasureMember { get; private set; }
    internal int? RowCap { get; private set; }
    internal AnalyticsProjectionPolicy? Projection { get; private set; }
    internal Type? GroupType { get; private set; }
    internal Type? MeasureValueType { get; private set; }

    /// <summary>
    /// The materialization's shape, computed from the recipe's state — group first, then the measure —
    /// so declaration order of <c>By</c>/<c>Count</c>/<c>Materialize</c> never changes the columns.
    /// </summary>
    internal IReadOnlyList<AnalyticsProjectionColumn> ComputeColumns()
    {
        var columns = new List<AnalyticsProjectionColumn>(2);
        if (GroupMember is { } group)
            columns.Add(new AnalyticsProjectionColumn(group, GroupType ?? typeof(string)));
        columns.Add(new AnalyticsProjectionColumn(
            MeasureAlias(MeasureKind, MeasureMember),
            MeasureKind == AnalyticsMeasureKind.Count ? typeof(long) : MeasureValueType ?? typeof(double)));
        return columns;
    }

    internal AnalyticsRecipe() { }

    /// <summary>
    /// The predicate is kept as an expression, not compiled: ask-time parameter values are substituted
    /// before compilation, so a declared question is one artifact answering a family of slices.
    /// </summary>
    public AnalyticsRecipe<TEntity, TKey> Where(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        WhereExpression = predicate;
        return this;
    }

    /// <summary>Declare an ask-time value this question accepts. The name must appear in the Where clause.</summary>
    public AnalyticsRecipe<TEntity, TKey> WithParameter<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ParameterDeclarations.Any(p => p.Name == name))
            throw new InvalidOperationException($"Analytics parameter '{name}' is declared more than once on this question.");
        ParameterDeclarations.Add(new AnalyticsParameterDeclaration(name, typeof(T)));
        return this;
    }

    /// <summary>
    /// Declare a parameter with a default: ask-time values still override it, and a materialized
    /// question refreshes through the default (there is no ask-time value at refresh time).
    /// </summary>
    public AnalyticsRecipe<TEntity, TKey> WithParameterDefault<T>(string name, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ParameterDeclarations.Any(p => p.Name == name))
            throw new InvalidOperationException($"Analytics parameter '{name}' is declared more than once on this question.");
        ParameterDeclarations.Add(new AnalyticsParameterDeclaration(name, typeof(T), value, HasDefault: true));
        return this;
    }

    public AnalyticsRecipe<TEntity, TKey> By<TMember>(Expression<Func<TEntity, TMember>> member)
    {
        GroupMember = MemberOf(member, nameof(By));
        GroupType = typeof(TMember);
        return this;
    }

    /// <summary>
    /// Materialize this question: refresh it on a cadence (or by trigger), store the answer in the elected
    /// engine, and label every served answer with its age. Cron spelling arrives with the scheduler pillar.
    /// </summary>
    public AnalyticsRecipe<TEntity, TKey> Materialize(Action<AnalyticsMaterializationBuilder> refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        var builder = new AnalyticsMaterializationBuilder();
        refresh(builder);
        Projection = builder.Policy;
        return this;
    }

    public AnalyticsRecipe<TEntity, TKey> Count()
    {
        MeasureKind = AnalyticsMeasureKind.Count;
        MeasureMember = null;
        return this;
    }

    public AnalyticsRecipe<TEntity, TKey> Sum<TMember>(Expression<Func<TEntity, TMember>> member) =>
        Measure(AnalyticsMeasureKind.Sum, member);

    public AnalyticsRecipe<TEntity, TKey> Min<TMember>(Expression<Func<TEntity, TMember>> member) =>
        Measure(AnalyticsMeasureKind.Min, member);

    public AnalyticsRecipe<TEntity, TKey> Max<TMember>(Expression<Func<TEntity, TMember>> member) =>
        Measure(AnalyticsMeasureKind.Max, member);

    public AnalyticsRecipe<TEntity, TKey> Average<TMember>(Expression<Func<TEntity, TMember>> member) =>
        Measure(AnalyticsMeasureKind.Average, member);

    public AnalyticsRecipe<TEntity, TKey> CapRowsAt(int cap)
    {
        if (cap <= 0) throw new ArgumentOutOfRangeException(nameof(cap), cap, "A row cap must be positive.");
        RowCap = cap;
        return this;
    }

    private AnalyticsRecipe<TEntity, TKey> Measure<TMember>(AnalyticsMeasureKind kind, Expression<Func<TEntity, TMember>> member)
    {
        MeasureKind = kind;
        MeasureMember = MemberOf(member, kind.ToString());
        MeasureValueType = typeof(TMember);
        return this;
    }

    internal static string MeasureAlias(AnalyticsMeasureKind kind, string? member) =>
        kind == AnalyticsMeasureKind.Count ? "count" : $"{kind.ToString().ToLowerInvariant()}_{member}";

    private static string MemberOf<TMember>(Expression<Func<TEntity, TMember>> expression, string verb)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var body = expression.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? convert.Operand
            : expression.Body;
        if (body is MemberExpression { Expression: not null } member &&
            member.Expression is ParameterExpression)
            return member.Member.Name;
        throw new ArgumentException($"Analytics '{verb}' accepts {DirectMemberOnly}.", nameof(expression));
    }
}
