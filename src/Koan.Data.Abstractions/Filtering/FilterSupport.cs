namespace Koan.Data.Abstractions.Filtering;

/// <summary>
/// The one structured capability detail in the framework (ARCH-0084): the operator set a provider
/// can push down for filtering, attached to the <c>DataCaps.Query.Filter</c> / <c>VectorCaps.Filters</c>
/// token. Covers both the entity path (scalar-vs-collection split) and the vector path (schemaless
/// single set via <see cref="Uniform"/>, where Scalar==Collection).
/// An operator the provider cannot honour faithfully must be left out so negotiation fails loud.
/// </summary>
public sealed record FilterSupport(
    IReadOnlySet<FilterOperator> ScalarOperators,
    IReadOnlySet<FilterOperator> CollectionOperators,
    bool NestedPaths = true,
    bool IgnoreCase = false)
{
    /// <summary>Native same-Entity identity counterpart queries. Row operator support alone never grants this capability.</summary>
    public bool SupportsSameIdIn { get; init; }

    /// <summary>
    /// Whether the provider can faithfully negate a supported subtree, including rows where a
    /// referenced field is null or absent. Providers with three-valued or missing-key semantics
    /// must opt out so the shared splitter leaves the complete <c>Not</c> node residual.
    /// </summary>
    public bool SupportsNegation { get; init; } = true;

    /// <summary>Can the provider push the operator on a field of the given kind?</summary>
    public bool CanPush(FilterOperator op, bool collectionField)
        => (collectionField ? CollectionOperators : ScalarOperators).Contains(op);

    private static readonly IReadOnlySet<FilterOperator> NoOps = new HashSet<FilterOperator>();
    private static readonly IReadOnlySet<FilterOperator> EveryOp =
        new HashSet<FilterOperator>((FilterOperator[])Enum.GetValues(typeof(FilterOperator)));

    /// <summary>
    /// Pushes nothing, so every non-empty filter becomes residual work. Entity materialized queries
    /// may evaluate that residual in memory; provider-bounded Entity streams may evaluate it
    /// pointwise over bounded candidate pages; consumers that cannot safely realize residuals reject.
    /// </summary>
    public static FilterSupport None { get; } = new(NoOps, NoOps, NestedPaths: false, IgnoreCase: false)
    {
        SupportsNegation = false
    };

    /// <summary>Pushes every operator (e.g. in-memory / oracle adapters that evaluate the AST directly).</summary>
    public static FilterSupport Full { get; } = new(EveryOp, EveryOp, NestedPaths: true, IgnoreCase: true);

    /// <summary>Entity form: explicit scalar + collection operator lists.</summary>
    public static FilterSupport Of(IEnumerable<FilterOperator> scalar, IEnumerable<FilterOperator> collection, bool nestedPaths = true, bool ignoreCase = false)
        => new(new HashSet<FilterOperator>(scalar), new HashSet<FilterOperator>(collection), nestedPaths, ignoreCase);

    /// <summary>Vector form: one operator set for both axes (vector metadata is schemaless — there is no scalar/collection split).</summary>
    public static FilterSupport Uniform(bool nestedPaths, bool ignoreCase, params FilterOperator[] operators)
    {
        var set = new HashSet<FilterOperator>(operators);
        return new(set, set, nestedPaths, ignoreCase);
    }
}
