using System;

namespace Koan.Data.Core.Transactions;

/// <summary>
/// Configuration options for deferred Entity-operation coordination.
/// </summary>
public sealed class TransactionOptions
{
    /// <summary>
    /// Where these settings bind from, alongside every other Koan option family.
    /// </summary>
    public const string SectionPath = "Koan:Data:Transactions";

    /// <summary>
    /// Default timeout for deferred coordination. Default: 2 minutes.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether to automatically COMMIT a transaction on dispose when it was not explicitly committed or
    /// rolled back. Default: false — dispose rolls back (safe, matches .NET TransactionScope: work persists
    /// only on an explicit Commit()). This controls scope exit only and does not add atomicity. Set true to opt
    /// into auto-commit-on-dispose convenience.
    /// </summary>
    public bool AutoCommitOnDispose { get; set; } = false;

    /// <summary>
    /// Enable telemetry spans and structured logging for coordination scopes that track work.
    /// Empty/read-only scopes complete silently. Default: true.
    /// </summary>
    public bool EnableTelemetry { get; set; } = true;

    /// <summary>
    /// Maximum number of operations that can be tracked in a single coordination scope.
    /// Prevents memory issues with large batches. Default: 10,000.
    /// </summary>
    public int MaxTrackedOperations { get; set; } = 10_000;

    /// <summary>
    /// Warn if coordination duration exceeds this threshold. Default: 30 seconds.
    /// </summary>
    public TimeSpan LongRunningTransactionWarning { get; set; } = TimeSpan.FromSeconds(30);
}
