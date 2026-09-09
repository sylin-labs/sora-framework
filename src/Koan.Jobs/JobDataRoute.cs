using Koan.Core.Context;
using Koan.Core.Naming;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core;

namespace Koan.Jobs;

/// <summary>The durable address of work, independent of the shared scheduling store.</summary>
internal readonly record struct JobDataRoute(string? Source, string? Adapter, string? Partition)
{
    public static JobDataRoute From(EntityContext.ContextState? state)
        => new(NormalizeSource(state?.Source), NormalizeSelector(state?.Adapter), NormalizePartition(state?.Partition));

    public static JobDataRoute From(JobRecord record)
        => From(new EntityContext.ContextState(record.WorkSource, record.WorkAdapter, record.WorkPartition));

    public IDisposable Restore()
        => EntityContext.With(source: Source ?? "", adapter: Adapter ?? "", partition: Partition ?? "", preserveTransaction: false);

    public JobQuery Query(string workType, string workId)
        => new(WorkType: workType, WorkId: workId)
        {
            WorkSource = Source ?? "", WorkAdapter = Adapter ?? "", WorkPartition = Partition ?? ""
        };

    public string Fold(string key)
    {
        if (this == default) return key;
        var axes = new Dictionary<string, string>();
        if (Source is not null) axes.Add("koan:jobs:work-source", Source);
        if (Adapter is not null) axes.Add("koan:jobs:work-adapter", Adapter);
        if (Partition is not null) axes.Add("koan:jobs:work-partition", Partition);
        return AmbientAxisComposer.Append(key, axes);
    }

    public static (string Type, string Id, JobDataRoute Route) Identity(JobRecord record)
        // Claim comparison must tolerate malformed metadata so execution can dead-letter that row.
        => (record.WorkType, record.WorkId, new JobDataRoute(
            NormalizeSource(record.WorkSource), NormalizeSelector(record.WorkAdapter), NormalizePartition(record.WorkPartition)));

    public static string? NormalizeSelector(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ToLowerInvariant();

    public static string? NormalizeSource(string? value)
        => string.Equals(value, DataSourcePlan.Default.Source, StringComparison.OrdinalIgnoreCase)
            ? null : NormalizeSelector(value);

    private static string? NormalizePartition(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Ledger operations use the default Jobs store while retaining submission transaction participation.</summary>
internal static class JobsStorageScope
{
    public static IDisposable Enter()
        => KoanContext.Push((EntityContext.Current ?? new EntityContext.ContextState()) with
        {
            Source = null, Adapter = null, Partition = null
        });
}
