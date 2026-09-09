using System;
using System.Threading;
using System.Threading.Tasks;
using Koan.Cache.Abstractions.Policies;
using Koan.Core.Context;
using Koan.Core.Hosting.App;
using Koan.Data.Core.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koan.Data.Core;

/// <summary>
/// Ambient routing context for entity operations.
/// Supports source OR adapter selection (mutually exclusive) plus partition and transaction routing.
///
/// Routing dimensions:
/// - Source: Named configuration (e.g., "analytics", "backup") - sources define their own adapter
/// - Adapter: Provider override (e.g., "sqlite", "postgres") - used with default source
/// - Partition: Storage partition suffix (e.g., "archive", "cold") - appended to storage name
/// - Transaction: Named transaction for coordinated commit/rollback across adapters
///
/// Source and Adapter are mutually exclusive - specifying both throws InvalidOperationException.
/// </summary>
public static class EntityContext
{
    private const string TransactionOperation = "entity transaction";

    /// <summary>
    /// Routing context state combining source, adapter, partition, and transaction.
    /// </summary>
    public sealed record ContextState
    {
        public string? Source { get; init; }
        public string? Adapter { get; init; }
        public string? Partition { get; init; }
        public string? Transaction { get; init; }

        /// <summary>
        /// Per-request cache behavior override. Read-side only — writes always invalidate.
        /// Null = honor the policy's declared strategy.
        /// </summary>
        public CacheBehavior? CacheBehavior { get; init; }

        /// <summary>
        /// Transaction coordinator instance for deferred execution.
        /// Used by entity and vector operations to participate in transactions.
        /// </summary>
        public ITransactionCoordinator? TransactionCoordinator { get; init; }

        /// <summary>
        /// Create routing context with validation.
        /// </summary>
        /// <param name="source">Named source configuration (e.g., "analytics")</param>
        /// <param name="adapter">Adapter override (e.g., "sqlite")</param>
        /// <param name="partition">Storage partition suffix (e.g., "archive")</param>
        /// <param name="transaction">Transaction name for coordination (e.g., "save-batch")</param>
        /// <param name="cacheBehavior">Per-request cache behavior override.</param>
        /// <exception cref="InvalidOperationException">Thrown when both source and adapter are specified</exception>
        public ContextState(
            string? source = null,
            string? adapter = null,
            string? partition = null,
            string? transaction = null,
            CacheBehavior? cacheBehavior = null)
        {
            // Critical constraint: source and adapter are mutually exclusive
            if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(adapter))
                throw new InvalidOperationException(
                    "Cannot specify both 'source' and 'adapter'. Sources define their own adapter selection.");

            Source = string.IsNullOrWhiteSpace(source) ? null : source;
            Adapter = string.IsNullOrWhiteSpace(adapter) ? null : adapter;
            Partition = string.IsNullOrWhiteSpace(partition) ? null : partition;
            Transaction = transaction;
            CacheBehavior = cacheBehavior;
        }

        internal void ValidatePartitionName()
        {
            if (string.IsNullOrWhiteSpace(Partition)) return;

            if (!PartitionNameValidator.IsValid(Partition))
                throw new ArgumentException(
                    $"Invalid partition name '{Partition}'. A partition name must be a GUID, or contain only " +
                    $"letters, digits, '-', '.', or '_', so that distinct partitions cannot collide after " +
                    $"identifier sanitization (which maps every other character to '_'). Re-encode names that " +
                    $"contain spaces, '/', '$', or similar before using them as a partition.",
                    nameof(Partition));
        }
    }

    /// <summary>
    /// Get current routing context (null if not set).
    /// </summary>
    public static ContextState? Current => KoanContext.Get<ContextState>();

    /// <summary>
    /// Check if currently in a transaction.
    /// </summary>
    public static bool InTransaction => Current?.Transaction != null;

    /// <summary>
    /// The current transaction coordinator (null if not in a transaction) — exposes its declared
    /// <see cref="Transactions.ITransactionCoordinator.Capabilities"/> (ARCH-0084 TxCaps tokens) and its
    /// live <see cref="Transactions.ITransactionCoordinator.Adapters"/> /
    /// <see cref="Transactions.ITransactionCoordinator.TrackedOperationCount"/>.
    /// </summary>
    public static Transactions.ITransactionCoordinator? CurrentTransaction =>
        Current?.TransactionCoordinator;

    /// <summary>
    /// Push a routing context for the lifetime of the returned scope. The new context is built
    /// <b>inherit-unless-overridden</b>, not wholesale-replaced: each dimension whose argument is left
    /// null adopts the ambient (previous) context's value for that dimension; a non-null argument
    /// overrides it. Omitting an argument therefore <b>preserves</b> the inherited value — it does not
    /// clear it — so a nested scope can change one axis (e.g. add a partition) while the rest carry over.
    ///
    /// <para>Mutual exclusion of source and adapter is enforced on the <b>effective</b> (post-inheritance)
    /// values. Empty source, adapter or partition explicitly clears that axis to its normalized null default.
    /// Because the two inherit independently, naming an <paramref name="adapter"/> while a source
    /// is inherited from the ambient context — or a <paramref name="source"/> while an adapter is
    /// inherited — throws <see cref="InvalidOperationException"/>. Switch routing axes from a scope that
    /// does not inherit the other, or override both explicitly.</para>
    ///
    /// <para>Transaction carry-over is gated by <paramref name="preserveTransaction"/> (default true) and
    /// is independent of the other dimensions: when true and no new <paramref name="transaction"/> is
    /// named, the ambient transaction and its coordinator carry into the scope; when false they are
    /// dropped. Disposing the returned scope restores the previous context.</para>
    /// </summary>
    /// <param name="source">Named source configuration; null inherits the ambient source.</param>
    /// <param name="adapter">Adapter override; null inherits the ambient adapter.</param>
    /// <param name="partition">Storage partition suffix; null inherits the ambient partition.</param>
    /// <param name="transaction">Transaction name for coordination; null inherits the ambient transaction when <paramref name="preserveTransaction"/> is true.</param>
    /// <param name="cacheBehavior">Per-request cache behavior override; null inherits the ambient behavior.</param>
    /// <param name="preserveTransaction">When true, retain the ambient transaction if one exists and no new transaction is specified.</param>
    /// <returns>Disposable that restores previous context on disposal</returns>
    /// <exception cref="InvalidOperationException">Thrown when source and adapter are both set on the effective context (directly or via inheritance), or when starting a transaction inside an existing one</exception>
    /// <exception cref="ArgumentException">Thrown when partition name is invalid</exception>
    public static IDisposable With(
        string? source = null,
        string? adapter = null,
        string? partition = null,
        string? transaction = null,
        CacheBehavior? cacheBehavior = null,
        bool preserveTransaction = true)
    {
        var prev = Current;

        // Prevent nested transactions when explicitly starting a new one
        if (!string.IsNullOrWhiteSpace(transaction) && prev?.Transaction != null)
        {
            throw new InvalidOperationException(
                $"Cannot start transaction '{transaction}' inside existing transaction '{prev.Transaction}'. " +
                "nested transactions are not supported.");
        }

        var effectiveSource = source ?? prev?.Source;
        var effectiveAdapter = adapter ?? prev?.Adapter;
        var effectivePartition = partition ?? prev?.Partition;
        var effectiveTransaction = preserveTransaction
            ? transaction ?? prev?.Transaction
            : transaction;
        var effectiveCacheBehavior = cacheBehavior ?? prev?.CacheBehavior;

        var newContext = new ContextState(effectiveSource, effectiveAdapter, effectivePartition, effectiveTransaction, effectiveCacheBehavior);
        // Fail fast on partition names that would NOT survive identifier sanitization unchanged — their lossy
        // character replacement could collide a distinct partition onto the same physical store (data bleed).
        // GUIDs and already-identifier-safe names pass; see PartitionNameValidator.
        newContext.ValidatePartitionName();

        ITransactionCoordinator? coordinatorForScope = null;
        var activeCoordinator = preserveTransaction ? prev?.TransactionCoordinator : null;

        // Create a new coordinator only when starting a new transaction
        if (!string.IsNullOrWhiteSpace(transaction))
        {
            coordinatorForScope = CreateTransactionCoordinator(transaction);
            activeCoordinator = coordinatorForScope;
        }

        if (activeCoordinator is not null)
        {
            newContext = newContext with { TransactionCoordinator = activeCoordinator };
        }

        var options = coordinatorForScope is null
            ? null
            : AppHost.Current?.GetService<IOptions<TransactionOptions>>()?.Value;
        var contextScope = KoanContext.Push(newContext);
        return new TransactionScope(contextScope, coordinatorForScope, options);
    }

    /// <summary>
    /// Convenience method to set only source routing.
    /// </summary>
    public static IDisposable Source(string source) => With(source: source);

    /// <summary>
    /// Convenience method to set only adapter routing.
    /// </summary>
    public static IDisposable Adapter(string adapter) => With(adapter: adapter);

    /// <summary>
    /// Convenience method to set only partition routing.
    /// </summary>
    public static IDisposable Partition(string partition) => With(partition: partition);

    /// <summary>
    /// Push a cache behavior override onto the AsyncLocal stack for the duration of the scope.
    /// Read-side only — writes (Upsert/Delete) always invalidate regardless of override.
    /// </summary>
    public static IDisposable WithCacheBehavior(CacheBehavior behavior) => With(cacheBehavior: behavior);

    /// <summary>
    /// Push <see cref="CacheBehavior.Bypass"/>: skip cache reads, hit the DB, do not populate.
    /// Writes within the scope still invalidate cache.
    /// </summary>
    public static IDisposable NoCache() => WithCacheBehavior(CacheBehavior.Bypass);

    /// <summary>
    /// Push <see cref="CacheBehavior.Refresh"/>: skip cache reads, hit the DB, repopulate from fresh value.
    /// </summary>
    public static IDisposable RefreshCache() => WithCacheBehavior(CacheBehavior.Refresh);

    /// <summary>
    /// Start a named transaction. Operations will be tracked and committed/rolled back atomically.
    /// Transaction commits automatically on dispose unless explicitly rolled back.
    /// </summary>
    /// <param name="name">Transaction name for correlation and telemetry</param>
    /// <returns>Disposable that auto-commits on successful disposal</returns>
    /// <exception cref="InvalidOperationException">Thrown when already in a transaction (nested transactions not supported)</exception>
    public static IDisposable Transaction(string name) => With(transaction: name);

    /// <summary>
    /// Commit the current transaction. All tracked operations are executed and committed across adapters.
    /// Idempotent no-op when there is no active transaction (or it has already completed) — calling Commit
    /// without a transaction, or twice, never throws.
    /// </summary>
    /// <exception cref="TransactionException">Thrown when commit fails</exception>
    public static Task Commit(CancellationToken ct = default)
    {
        var current = Current;
        if (current?.TransactionCoordinator == null)
            return Task.CompletedTask; // no active transaction → no-op
        return current.TransactionCoordinator.Commit(ct);
    }

    /// <summary>
    /// Rollback the current transaction. All tracked operations are discarded. Idempotent no-op when there
    /// is no active transaction (or it has already completed) — calling Rollback without a transaction, or
    /// after a commit/rollback, never throws.
    /// </summary>
    public static Task Rollback(CancellationToken ct = default)
    {
        var current = Current;
        if (current?.TransactionCoordinator == null)
            return Task.CompletedTask; // no active transaction → no-op
        return current.TransactionCoordinator.Rollback(ct);
    }

    private static ITransactionCoordinator CreateTransactionCoordinator(string name)
    {
        var factory = AppHost.GetRequiredService<ITransactionCoordinatorFactory>(TransactionOperation);
        return factory.Create(name);
    }

    private sealed class TransactionScope : IDisposable
    {
        private readonly IDisposable _contextScope;
        private readonly ITransactionCoordinator? _coordinator;
        private readonly TransactionOptions? _options;
        private bool _disposed;

        public TransactionScope(
            IDisposable contextScope,
            ITransactionCoordinator? coordinator,
            TransactionOptions? options)
        {
            _contextScope = contextScope;
            _coordinator = coordinator;
            _options = options;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // Disposed without an explicit Commit/Rollback. Default to ROLLBACK (safe — matches
                // .NET TransactionScope): pending work persists only on an explicit Commit(), so forgetting
                // to commit or an exception escaping the using-block discards it. Opt into the legacy
                // auto-commit behavior via TransactionOptions.AutoCommitOnDispose = true.
                if (_coordinator != null && !_coordinator.IsCompleted)
                {
                    var autoCommit = _options?.AutoCommitOnDispose ?? false;
                    if (autoCommit)
                        _coordinator.Commit(default).GetAwaiter().GetResult();
                    else
                        _coordinator.Rollback(default).GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Swallow exceptions in Dispose (already logged by coordinator)
                // Transaction will be marked as failed
            }
            finally
            {
                _contextScope.Dispose();
            }
        }
    }
}
