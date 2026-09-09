using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;

namespace Koan.Data.Connector.Mongo.Runtime;

internal static class MongoFeatures
{
    public static readonly FilterSupport Filters = FilterSupport.Of(
        [
            FilterOperator.Eq, FilterOperator.Ne, FilterOperator.Gt, FilterOperator.Gte,
            FilterOperator.Lt, FilterOperator.Lte, FilterOperator.In, FilterOperator.Nin,
            FilterOperator.StartsWith, FilterOperator.EndsWith, FilterOperator.Contains,
            FilterOperator.Exists
        ],
        [
            FilterOperator.Has, FilterOperator.HasAny, FilterOperator.HasAll,
            FilterOperator.HasNone, FilterOperator.Size, FilterOperator.HasContains, FilterOperator.Exists
        ],
        nestedPaths: true,
        ignoreCase: false);

    public static readonly IReadOnlyList<Capability> All =
    [
        DataCaps.Query.Linq,
        DataCaps.Query.ProviderBoundedPaging,
        DataCaps.Query.Filter,
        DataCaps.Query.FilterExecution,
        DataCaps.Write.BulkUpsert,
        DataCaps.Write.BulkDelete,
        DataCaps.Write.ConditionalReplace,
        DataCaps.Retention.TtlIndex,
        DataCaps.Isolation.RowScoped,
        DataCaps.Isolation.ContainerScoped,
        DataCaps.Isolation.DatabaseScoped
    ];

    public static void Declare(IDataClaims claims)
    {
        foreach (var capability in All) claims.Capability(capability);
        // Availability is qualified by the selected repository's identity shape.
        claims.Capability(DataCaps.Write.InsertOnly);
    }

    public static void Describe(ICapabilities capabilities, bool supportsSameIdIn = false, bool supportsConditionalReplace = true)
    {
        foreach (var capability in All)
            if (supportsConditionalReplace || capability != DataCaps.Write.ConditionalReplace) capabilities.Add(capability);
        capabilities
            .Add(DataCaps.Query.Filter, Filters with { SupportsSameIdIn = supportsSameIdIn })
            .Add(DataCaps.Query.FilterExecution, new FilterExecutionProfile(FilterExecutionKind.Native));
    }
}
