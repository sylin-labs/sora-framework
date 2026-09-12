using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Failures;
using Microsoft.Extensions.Logging;
using Koan.Data.Core.Routing;

namespace Koan.Data.Core.Transactions;

/// <summary>
/// Coordinates deferred entity operations without claiming native or distributed atomicity.
/// </summary>
internal sealed class TransactionCoordinator : ITransactionCoordinator
{
    private readonly ILogger<TransactionCoordinator> _logger;
    private readonly TransactionOptions _options;
    private readonly Dictionary<string, List<ITrackedOperation>> _operationsByAdapter = new();
    private readonly Activity? _activity;
    private bool _isCompleted;
    private readonly object _lock = new();
    private readonly IServiceProvider _services;
    private readonly DataOperationHorizon _operationHorizon;

    public string Name { get; }
    public bool IsCompleted => _isCompleted;

    public TransactionCoordinator(
        string name,
        IDataService dataService,
        IServiceProvider services,
        DataOperationHorizon operationHorizon,
        ILogger<TransactionCoordinator> logger,
        TransactionOptions options)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _operationHorizon = operationHorizon ?? throw new ArgumentNullException(nameof(operationHorizon));

        // Start telemetry span
        if (_options.EnableTelemetry)
        {
            _activity = TransactionTelemetry.StartTransaction(name);
        }

        _logger.LogInformation(
            "Transaction '{TransactionName}' started (id: {TransactionId})",
            Name,
            _activity?.Id ?? "unknown");
    }

    public void TrackSave<TEntity, TKey>(TEntity entity, EntityContext.ContextState context)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        ThrowIfCompleted();

        lock (_lock)
        {
            var operation = new SaveOperation<TEntity, TKey>(entity, context);
            TrackOperation(operation);

            _logger.LogDebug(
                "Transaction '{TransactionName}' tracked Save<{EntityType}> (id: {EntityId})",
                Name,
                typeof(TEntity).Name,
                entity.Id);
        }
    }

    public void TrackDelete<TEntity, TKey>(TKey id, EntityContext.ContextState context)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        ThrowIfCompleted();

        lock (_lock)
        {
            var operation = new DeleteOperation<TEntity, TKey>(id, context);
            TrackOperation(operation);

            _logger.LogDebug(
                "Transaction '{TransactionName}' tracked Delete<{EntityType}> (id: {EntityId})",
                Name,
                typeof(TEntity).Name,
                id);
        }
    }

    public void TrackVectorSave<TEntity, TKey>(
        TKey id,
        ReadOnlyMemory<float> embedding,
        IReadOnlyDictionary<string, object>? metadata,
        EntityContext.ContextState context)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        ThrowIfCompleted();

        lock (_lock)
        {
            var operation = new VectorSaveOperation<TEntity, TKey>(id, embedding, metadata, context);
            TrackOperation(operation);

            _logger.LogDebug(
                "Transaction '{TransactionName}' tracked VectorSave<{EntityType}> (id: {EntityId}, dimensions: {Dimensions})",
                Name,
                typeof(TEntity).Name,
                id,
                embedding.Length);
        }
    }

    public void TrackVectorDelete<TEntity, TKey>(TKey id, EntityContext.ContextState context)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        ThrowIfCompleted();

        lock (_lock)
        {
            var operation = new VectorDeleteOperation<TEntity, TKey>(id, context);
            TrackOperation(operation);

            _logger.LogDebug(
                "Transaction '{TransactionName}' tracked VectorDelete<{EntityType}> (id: {EntityId})",
                Name,
                typeof(TEntity).Name,
                id);
        }
    }

    private void TrackOperation(ITrackedOperation operation)
    {
        var adapter = operation.GetAdapterHint();

        if (!_operationsByAdapter.ContainsKey(adapter))
        {
            _operationsByAdapter[adapter] = new List<ITrackedOperation>();
        }

        _operationsByAdapter[adapter].Add(operation);

        // Check max operations limit
        var totalOperations = _operationsByAdapter.Values.Sum(list => list.Count);
        if (totalOperations > _options.MaxTrackedOperations)
        {
            throw new InvalidOperationException(
                $"Transaction '{Name}' exceeded maximum tracked operations ({_options.MaxTrackedOperations}). " +
                $"Consider breaking into smaller transactions.");
        }
    }

    public async Task Commit(CancellationToken ct = default)
    {
        if (_isCompleted) return; // idempotent: a second Commit (or Commit after Rollback) is a no-op

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(
                "Deferred coordination '{TransactionName}' executing {OperationCount} operations across {AdapterCount} adapter(s)",
                Name,
                _operationsByAdapter.Values.Sum(list => list.Count),
                _operationsByAdapter.Count);

            var bindings = _operationsByAdapter.Values
                .SelectMany(static operations => operations)
                .Select(operation => operation.ResolveDataBinding(_services))
                .OfType<DataRouteBinding>()
                .ToArray();
            await using var operationHorizon = await _operationHorizon
                .EnterMany(bindings, $"deferred transaction '{Name}'", ct)
                .ConfigureAwait(false);

            // Execute operations sequentially inside one declared route horizon. This does not create a native
            // transaction, so a later failure can leave an earlier operation durable.
            await ExecuteOperations(ct);

            _isCompleted = true;
            stopwatch.Stop();

            _logger.LogInformation(
                "Deferred coordination '{TransactionName}' completed successfully in {ElapsedMs}ms",
                Name,
                stopwatch.ElapsedMilliseconds);

            _activity?.SetStatus(ActivityStatusCode.Ok);
            _activity?.SetTag("transaction.outcome", "committed");
            _activity?.SetTag("transaction.duration_ms", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transaction '{TransactionName}' commit failed",
                Name);

            _activity?.SetStatus(ActivityStatusCode.Error, "deferred coordination failed");
            _activity?.SetTag("transaction.outcome", "failed");
            _isCompleted = true;

            if (ex is TransactionException) throw;
            throw new TransactionException(
                $"Deferred coordination '{Name}' failed. Its commit outcome is unknown and it will not be replayed.",
                Name,
                failedAdapter: null,
                completedOperationCount: 0,
                DataCommitOutcome.Unknown,
                ex);
        }
        finally
        {
            _activity?.Dispose();
        }
    }

    public async Task Rollback(CancellationToken ct = default)
    {
        if (_isCompleted) return; // idempotent: a second Rollback (or Rollback after Commit) is a no-op

        try
        {
            _logger.LogWarning(
                "Transaction '{TransactionName}' rolling back {OperationCount} operations (discarding tracked changes)",
                Name,
                _operationsByAdapter.Values.Sum(list => list.Count));

            // Simply discard tracked operations - nothing was persisted yet
            _operationsByAdapter.Clear();

            _isCompleted = true;

            _logger.LogInformation(
                "Transaction '{TransactionName}' rolled back successfully",
                Name);

            _activity?.SetTag("transaction.outcome", "rolled_back");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Transaction '{TransactionName}' rollback failed",
                Name);

            throw new TransactionException(
                $"Transaction '{Name}' rollback failed: {ex.Message}",
                Name,
                ex);
        }
        finally
        {
            _activity?.Dispose();
        }
    }

    // Deferred coordination is useful but is neither a native local transaction nor distributed atomicity.
    public CapabilitySet Capabilities { get; } = CapabilitySet.Build(
        "transaction",
        c => c.Add(TxCaps.DeferredCoordination));

    public IReadOnlyList<string> Adapters => _operationsByAdapter.Keys.ToArray();

    public int TrackedOperationCount => _operationsByAdapter.Values.Sum(list => list.Count);

    private async Task ExecuteOperations(CancellationToken ct)
    {
        var executedAdapters = new List<string>();
        var completedOperations = 0;

        // Temporarily clear transaction context to prevent recursion when operations execute
        var savedContext = EntityContext.Current;
        var clearedContext = savedContext != null
            ? new EntityContext.ContextState(
                source: savedContext.Source,
                adapter: savedContext.Adapter,
                partition: savedContext.Partition,
                transaction: null)  // Clear transaction
            : null;

        using var _ = clearedContext != null
            ? EntityContext.With(
                source: clearedContext.Source,
                adapter: clearedContext.Adapter,
                partition: clearedContext.Partition,
                preserveTransaction: false)
            : null;

        foreach (var (adapter, operations) in _operationsByAdapter)
        {
            _logger.LogDebug(
                "Transaction '{TransactionName}' executing {OperationCount} operations on adapter '{Adapter}'",
                Name,
                operations.Count,
                adapter);

            _activity?.SetTag($"transaction.adapter.{adapter}.operation_count", operations.Count);

            foreach (var operation in operations)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await operation.Execute(ct);
                    completedOperations++;
                }
                catch (Exception ex)
                {
                    // Critical: Some adapters completed, some didn't
                    _logger.LogError(ex,
                        "Transaction '{TransactionName}' operation failed on adapter '{Adapter}'. " +
                        "Completed adapters: {CompletedAdapters}",
                        Name,
                        adapter,
                        string.Join(", ", executedAdapters));

                    throw new TransactionException(
                        $"Deferred coordination '{Name}' failed after {completedOperations} completed operation(s). " +
                        "Its commit outcome is unknown and it will not be replayed.",
                        Name,
                        adapter,
                        completedOperations,
                        DataCommitOutcome.Unknown,
                        ex);
                }
            }

            executedAdapters.Add(adapter);
            _activity?.SetTag($"transaction.adapter.{adapter}.completed", true);
        }
    }

    private void ThrowIfCompleted()
    {
        if (_isCompleted)
        {
            throw new InvalidOperationException(
                $"Transaction '{Name}' has already been completed (committed or rolled back).");
        }
    }
}

/// <summary>
/// Telemetry helper for transaction spans.
/// </summary>
internal static class TransactionTelemetry
{
    private static readonly ActivitySource ActivitySource = new("Koan.Data.Transaction", "1.0.0");

    public static Activity? StartTransaction(string name)
    {
        var activity = ActivitySource.StartActivity("Transaction", ActivityKind.Internal);
        activity?.SetTag("transaction.name", name);
        return activity;
    }
}
