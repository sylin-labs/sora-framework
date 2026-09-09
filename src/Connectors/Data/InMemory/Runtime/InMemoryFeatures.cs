using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;

namespace Koan.Data.Connector.InMemory.Runtime;

internal static class InMemoryFeatures
{
    private static readonly Capability[] Claims =
    [
        DataCaps.Query.Linq,
        DataCaps.Query.Filter,
        DataCaps.Query.FilterExecution,
        DataCaps.Write.ConditionalReplace,
        DataCaps.Write.InsertOnly,
        DataCaps.Write.BulkUpsert,
        DataCaps.Write.BulkDelete,
        DataCaps.Isolation.RowScoped,
        DataCaps.Isolation.ContainerScoped,
        DataCaps.Isolation.DatabaseScoped
    ];

    internal static void Declare(IDataClaims claims)
    {
        foreach (var capability in Claims) claims.Capability(capability);
    }

    internal static void DescribeBackend(ICapabilities capabilities) => capabilities
        .Add(DataCaps.Query.FilterExecution,
            new FilterExecutionProfile(FilterExecutionKind.InMemory, SupportsBoundedCandidates: true))
        .Add(DataCaps.Write.ConditionalReplace)
        .Add(DataCaps.Write.InsertOnly)
        .Add(DataCaps.Write.BulkUpsert)
        .Add(DataCaps.Write.BulkDelete);
}
