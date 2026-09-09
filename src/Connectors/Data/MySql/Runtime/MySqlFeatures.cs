using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Relational.Linq;

namespace Koan.Data.Connector.MySql.Runtime;

internal static class MySqlFeatures
{
    private static readonly Capability[] All =
    [
        DataCaps.Query.Linq, DataCaps.Query.String,
        DataCaps.Query.ProviderBoundedPaging, DataCaps.Query.Filter, DataCaps.Query.FilterExecution,
        DataCaps.Write.BulkUpsert, DataCaps.Write.BulkDelete, DataCaps.Write.AtomicBatch,
        DataCaps.Write.FastRemove, DataCaps.Write.ConditionalReplace,
        DataCaps.Isolation.RowScoped, DataCaps.Isolation.ContainerScoped, DataCaps.Isolation.DatabaseScoped
    ];

    public static void Declare(IDataClaims claims)
    {
        foreach (var capability in All) claims.Capability(capability);
    }

    public static void Describe(ICapabilities capabilities, bool conditionalReplace = true)
    {
        foreach (var capability in All)
            if (conditionalReplace || capability != DataCaps.Write.ConditionalReplace) capabilities.Add(capability);
        capabilities
            .Add(DataCaps.Query.Filter, RelationalFilterSupport.Default)
            .Add(DataCaps.Query.FilterExecution, new FilterExecutionProfile(FilterExecutionKind.Native));
    }
}
