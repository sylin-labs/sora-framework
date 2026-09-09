using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sorting;

namespace Koan.Web.Hooks;

/// <summary>
/// Query and shaping options flowing through the controller and hooks.
/// </summary>
public sealed class QueryOptions
{
    /// <summary>The normalized relationship expansion choice shared by endpoint and emit hooks.</summary>
    public bool IncludeRelationships { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = Infrastructure.KoanWebConstants.Defaults.DefaultPageSize;

    /// <summary>
    /// Structured sort specs with resolved MemberPath. Built by EntityQueryParser (strict-by-default)
    /// and mutated by hooks. See DATA-0092.
    /// </summary>
    public List<SortSpec> Sort { get; set; } = new();

    public string Shape { get; set; } = "full"; // full | map | dict
    public string? View { get; set; }
    public Dictionary<string, string> Extras { get; } = new();

    /// <summary>
    /// Complete normalized server constraint, AND-composed with the user's filter before execution.
    /// AddPredicate lowers recognized captured values when contributed. Null means unconstrained.
    /// When a filter exists, free-text Q is dropped because that separate provider input cannot be composed.
    /// </summary>
    public Filter? Filter { get; set; }
}
