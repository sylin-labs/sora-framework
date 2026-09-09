namespace Koan.Jobs;

/// <summary>A translatable filter for the static facade (<c>MyModel.Jobs.Where(...)</c>) and dashboards.
/// Null fields are wildcards. Kept declarative (not an arbitrary predicate) so durable ledgers can push it down.</summary>
public sealed record JobQuery(
    string? WorkType = null,
    string? WorkId = null,
    string? Action = null,
    JobStatus? Status = null)
{
    /// <summary>Work source filter: null spans sources; empty selects the default, including legacy records.</summary>
    public string? WorkSource { get; init; }
    /// <summary>Work adapter filter: null spans adapters; empty selects the default.</summary>
    public string? WorkAdapter { get; init; }
    /// <summary>Work partition filter: null spans partitions; empty selects the default.</summary>
    public string? WorkPartition { get; init; }
}
