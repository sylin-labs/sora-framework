using System;
using System.Threading;
using System.Threading.Tasks;
using Koan.Data.Abstractions;
using Koan.Data.Core.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Core.Transactions;

/// <summary>
/// Represents a deferred entity operation that can be executed within a transaction.
/// </summary>
internal interface ITrackedOperation
{
    /// <summary>
    /// Execute the operation using the repository.
    /// </summary>
    Task Execute(CancellationToken ct);

    /// <summary>
    /// Get the adapter hint for this operation (for grouping).
    /// </summary>
    string GetAdapterHint();

    DataRouteBinding? ResolveDataBinding(IServiceProvider services);
}

/// <summary>
/// Tracked entity save operation.
/// </summary>
internal sealed class SaveOperation<TEntity, TKey> : ITrackedOperation
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly TEntity _entity;
    private readonly EntityContext.ContextState _context;
    private readonly string? _partition;

    public SaveOperation(TEntity entity, EntityContext.ContextState context, string? partition = null)
    {
        _entity = entity ?? throw new ArgumentNullException(nameof(entity));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _partition = partition;
    }

    public async Task Execute(CancellationToken ct)
    {
        var partition = _partition ?? _context.Partition;

        using var _ = EntityContext.With(
            source: _context.Source ?? "",
            adapter: _context.Adapter ?? "",
            partition: partition ?? "",
            preserveTransaction: false);

        await Data<TEntity, TKey>.Upsert(_entity, ct);
    }

    public string GetAdapterHint()
    {
        return _context.Adapter ?? _context.Source ?? "Default";
    }

    public DataRouteBinding? ResolveDataBinding(IServiceProvider services)
    {
        using var _ = EntityContext.With(
            source: _context.Source ?? "",
            adapter: _context.Adapter ?? "",
            partition: _partition ?? _context.Partition ?? "",
            preserveTransaction: false);
        return AdapterResolver.ResolveDecisionForEntity<TEntity>(
            services,
            services.GetRequiredService<DataSourceRegistry>()).Binding;
    }
}

/// <summary>
/// Tracked entity delete operation.
/// </summary>
internal sealed class DeleteOperation<TEntity, TKey> : ITrackedOperation
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly TKey _id;
    private readonly EntityContext.ContextState _context;
    private readonly string? _partition;

    public DeleteOperation(TKey id, EntityContext.ContextState context, string? partition = null)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _partition = partition;
    }

    public async Task Execute(CancellationToken ct)
    {
        var partition = _partition ?? _context.Partition;

        using var _ = EntityContext.With(
            source: _context.Source ?? "",
            adapter: _context.Adapter ?? "",
            partition: partition ?? "",
            preserveTransaction: false);

        await Data<TEntity, TKey>.Delete(_id, ct);
    }

    public string GetAdapterHint()
    {
        return _context.Adapter ?? _context.Source ?? "Default";
    }

    public DataRouteBinding? ResolveDataBinding(IServiceProvider services)
    {
        using var _ = EntityContext.With(
            source: _context.Source ?? "",
            adapter: _context.Adapter ?? "",
            partition: _partition ?? _context.Partition ?? "",
            preserveTransaction: false);
        return AdapterResolver.ResolveDecisionForEntity<TEntity>(
            services,
            services.GetRequiredService<DataSourceRegistry>()).Binding;
    }
}

/// <summary>
/// Tracked vector save operation.
/// </summary>
internal sealed class VectorSaveOperation<TEntity, TKey> : ITrackedOperation
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly TKey _id;
    private readonly ReadOnlyMemory<float> _embedding;
    private readonly IReadOnlyDictionary<string, object>? _metadata;
    private readonly EntityContext.ContextState _context;
    private readonly string? _partition;

    public VectorSaveOperation(
        TKey id,
        ReadOnlyMemory<float> embedding,
        IReadOnlyDictionary<string, object>? _metadata,
        EntityContext.ContextState context,
        string? partition = null)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _embedding = embedding;
        this._metadata = _metadata;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _partition = partition;
    }

    public async Task Execute(CancellationToken ct)
    {
        var partition = _partition ?? _context.Partition;

        using var _ = EntityContext.With(
            source: _context.Source ?? "",
            adapter: _context.Adapter ?? "",
            partition: partition ?? "",
            preserveTransaction: false);

        // Use reflection to avoid circular dependency between Core and Vector assemblies
        var vectorServiceType = Type.GetType("Koan.Data.Vector.IVectorService, Koan.Data.Vector");
        if (vectorServiceType == null)
            throw new InvalidOperationException("Koan.Data.Vector assembly not loaded. Cannot execute vector save within transaction.");

        var vectorService = Koan.Core.Hosting.App.AppHost.Current?.GetService(vectorServiceType);
        if (vectorService == null)
            throw new InvalidOperationException("IVectorService not registered. Cannot execute vector save within transaction.");

        // Call TryGetRepository<TEntity, TKey>() via reflection
        var tryGetRepoMethod = vectorServiceType.GetMethod("TryGetRepository")?.MakeGenericMethod(typeof(TEntity), typeof(TKey));
        if (tryGetRepoMethod == null)
            throw new InvalidOperationException("TryGetRepository method not found on IVectorService.");

        var repo = tryGetRepoMethod.Invoke(vectorService, null);
        if (repo == null)
            throw new InvalidOperationException($"No vector repository configured for {typeof(TEntity).Name}.");

        // Call UpsertAsync via reflection
        var upsertMethod = repo.GetType().GetMethod("Upsert");
        if (upsertMethod == null)
            throw new InvalidOperationException("UpsertAsync method not found on vector repository.");

        // The vector repository's Upsert takes float[] (see VectorData.Upsert, which calls
        // vector.ToArray()). Reflection binds arguments by exact type, so the ReadOnlyMemory<float> field
        // must be materialized to an array here too — otherwise Invoke throws "ReadOnlyMemory<float>
        // cannot be converted to Single[]" and the whole transaction is reported non-atomic.
        var task = upsertMethod.Invoke(repo, new object?[] { _id, _embedding.ToArray(), _metadata, ct }) as Task;
        if (task != null)
            await task;
    }

    public string GetAdapterHint()
    {
        return $"vector:{_context.Adapter ?? _context.Source ?? "Default"}";
    }

    public DataRouteBinding? ResolveDataBinding(IServiceProvider services) => null;
}

/// <summary>
/// Tracked vector delete operation.
/// </summary>
internal sealed class VectorDeleteOperation<TEntity, TKey> : ITrackedOperation
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly TKey _id;
    private readonly EntityContext.ContextState _context;
    private readonly string? _partition;

    public VectorDeleteOperation(TKey id, EntityContext.ContextState context, string? partition = null)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _partition = partition;
    }

    public async Task Execute(CancellationToken ct)
    {
        var partition = _partition ?? _context.Partition;

        using var _ = EntityContext.With(
            source: _context.Source ?? "",
            adapter: _context.Adapter ?? "",
            partition: partition ?? "",
            preserveTransaction: false);

        // Use reflection to avoid circular dependency between Core and Vector assemblies
        var vectorServiceType = Type.GetType("Koan.Data.Vector.IVectorService, Koan.Data.Vector");
        if (vectorServiceType == null)
            throw new InvalidOperationException("Koan.Data.Vector assembly not loaded. Cannot execute vector delete within transaction.");

        var vectorService = Koan.Core.Hosting.App.AppHost.Current?.GetService(vectorServiceType);
        if (vectorService == null)
            throw new InvalidOperationException("IVectorService not registered. Cannot execute vector delete within transaction.");

        // Call TryGetRepository<TEntity, TKey>() via reflection
        var tryGetRepoMethod = vectorServiceType.GetMethod("TryGetRepository")?.MakeGenericMethod(typeof(TEntity), typeof(TKey));
        if (tryGetRepoMethod == null)
            throw new InvalidOperationException("TryGetRepository method not found on IVectorService.");

        var repo = tryGetRepoMethod.Invoke(vectorService, null);
        if (repo == null)
            throw new InvalidOperationException($"No vector repository configured for {typeof(TEntity).Name}.");

        // Call DeleteAsync via reflection
        var deleteMethod = repo.GetType().GetMethod("Delete");
        if (deleteMethod == null)
            throw new InvalidOperationException("DeleteAsync method not found on vector repository.");

        var task = deleteMethod.Invoke(repo, new object[] { _id, ct }) as Task;
        if (task != null)
            await task;
    }

    public string GetAdapterHint()
    {
        return $"vector:{_context.Adapter ?? _context.Source ?? "Default"}";
    }

    public DataRouteBinding? ResolveDataBinding(IServiceProvider services) => null;
}
