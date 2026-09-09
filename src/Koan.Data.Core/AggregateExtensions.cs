using Koan.Core;
using Koan.Core.Hosting.App;
using System.Linq.Expressions;
using Koan.Data.Abstractions;
using Koan.Data.Core.Model;

namespace Koan.Data.Core;

/// <summary>
/// High-level convenience extensions for aggregates and collections.
/// Wraps repository calls with concise verbs (Upsert, Save, Remove),
/// keeps parity with domain static helpers, and hides service lookups.
/// </summary>
public static class AggregateExtensions
{
    private const string DataOperation = "aggregate data access";

    private static IDataService DataService()
        => AppHost.GetRequiredService<IDataService>(DataOperation);

    /// <summary>Insert only if the identity is absent. Conflict returns no existing row.
    /// Null partition inherits; empty selects the default. An exception does not imply rollback.</summary>
    public static Task<MutationResult<TEntity, string>> Insert<TEntity>(this TEntity model,
        string? partition = null, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Data<TEntity, string>.Insert(model, partition, ct);

    /// <summary>Key-inferred insertion for Entity receivers with non-string keys.</summary>
    public static Task<MutationResult<TEntity, TKey>> Insert<TEntity, TKey>(this Model.Entity<TEntity, TKey> model,
        string? partition = null, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Data<TEntity, TKey>.Insert((TEntity)(object)model, partition, ct);

    /// <summary>Replace an existing row only while its stored state matches the guard. False means missing or conflict.
    /// Null partition inherits; empty selects the default. An exception after dispatch does not imply rollback.</summary>
    public static Task<bool> ReplaceIf<TEntity>(this TEntity model, Expression<Func<TEntity, bool>> guard,
        string? partition = null, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Data<TEntity, string>.ReplaceIf(model, guard, partition, ct);

    /// <summary>Key-inferred guarded replacement for Entity receivers with non-string keys.</summary>
    public static Task<bool> ReplaceIf<TEntity, TKey>(this Model.Entity<TEntity, TKey> model,
        Expression<Func<TEntity, bool>> guard, string? partition = null, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Data<TEntity, TKey>.ReplaceIf((TEntity)(object)model, guard, partition, ct);

    // Instance-level convenience: model.Upsert() (generic key)
    /// <summary>
    /// Insert or update a model for the aggregate using its configured repository.
    /// Ensures identifiers via the identity manager and returns the saved entity.
    /// </summary>
    public static Task<TEntity> Upsert<TEntity, TKey>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Model.Entity<TEntity, TKey>.Upsert(model, ct);

    // Alias: model.Save() -> Upsert (generic key)
    /// <summary>
    /// Alias for Upsert for generic-key entities; intended as a friendly verb in app code.
    /// </summary>
    public static Task<TEntity> Save<TEntity, TKey>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => model.Upsert<TEntity, TKey>(ct);

    /// <summary>
    /// Key-inferred Save for entities whose key is not string. The constructed Entity base exposes both generic
    /// arguments to C# inference; exact receiver overloads remain preferred for derived/polymorphic string entities.
    /// </summary>
    public static Task<TEntity> Save<TEntity, TKey>(
        this Model.Entity<TEntity, TKey> model,
        CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Model.Entity<TEntity, TKey>.Upsert((TEntity)(object)model, ct);

    /// <summary>Save and return the exact native insert/update outcome when the adapter proves it.</summary>
    public static Task<MutationResult<TEntity, TKey>> SaveWithOutcome<TEntity, TKey>(
        this TEntity model,
        CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Model.Entity<TEntity, TKey>.UpsertWithOutcome(model, ct);

    // Instance-level convenience: model.Upsert("partition") (generic key)
    /// <summary>
    /// Insert or update a model into a specific logical partition for the aggregate using its configured repository.
    /// Routes storage to BaseName#&lt;partition&gt; via EntityContext and the adapter factory's ResolveStorage.
    /// </summary>
    public static Task<TEntity> Upsert<TEntity, TKey>(this TEntity model, string partition, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Model.Entity<TEntity, TKey>.Upsert(model, partition, ct);

    // Alias: model.Save("partition") -> Upsert("partition") (generic key)
    /// <summary>
    /// Alias for Upsert into a specific partition for generic-key entities.
    /// </summary>
    public static Task<TEntity> Save<TEntity, TKey>(this TEntity model, string partition, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => model.Upsert<TEntity, TKey>(partition, ct);

    // Non-generic convenience for the common string key case
    /// <summary>
    /// Upsert for string-keyed entities without specifying TKey.
    /// </summary>
    public static Task<TEntity> Upsert<TEntity>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Model.Entity<TEntity, string>.Upsert(model, ct);

    // Alias: model.Save() -> Upsert (string key convenience)
    /// <summary>
    /// Save alias for string-keyed entities.
    /// </summary>
    public static Task<TEntity> Save<TEntity>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => model.Upsert(ct);

    /// <summary>String-key convenience for an exact native insert/update outcome.</summary>
    public static Task<MutationResult<TEntity, string>> SaveWithOutcome<TEntity>(
        this TEntity model,
        CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Model.Entity<TEntity, string>.UpsertWithOutcome(model, ct);

    // Non-generic convenience: model.Upsert("partition") (string key)
    /// <summary>
    /// Upsert a string-keyed entity into a specific logical partition.
    /// </summary>
    public static Task<TEntity> Upsert<TEntity>(this TEntity model, string partition, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Model.Entity<TEntity, string>.Upsert(model, partition, ct);

    // Alias: model.Save("partition") -> Upsert("partition") (string key)
    /// <summary>
    /// Save alias for string-keyed entities into a specific partition.
    /// </summary>
    public static Task<TEntity> Save<TEntity>(this TEntity model, string partition, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => model.Upsert(partition, ct);

    // Return only the identifier after upsert (generic)
    /// <summary>
    /// Upsert and return only the identifier (generic key).
    /// </summary>
    public static async Task<TKey> UpsertId<TEntity, TKey>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => (await Model.Entity<TEntity, TKey>.Upsert(model, ct)).Id;

    // Convenience for string key: UpsertId()
    /// <summary>
    /// Upsert and return only the identifier (string key convenience).
    /// </summary>
    public static Task<string> UpsertId<TEntity>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => model.UpsertId<TEntity, string>(ct);

    // Non-generic instance Upsert() with runtime inference
    /// <summary>
    /// Runtime Upsert for unknown entity types; reflects the repository and UpsertAsync method.
    /// </summary>
    public static async Task<object?> Upsert(this object model, CancellationToken ct = default)
    {
        if (model is null) throw new System.ArgumentNullException(nameof(model));
        var (aggType, keyType) = ResolveAggregateContract(model.GetType());
        var data = DataService();
        var getRepo = typeof(IDataService).GetMethod(nameof(IDataService.GetRepository))!;
        var repo = getRepo.MakeGenericMethod(aggType, keyType).Invoke(data, Array.Empty<object>())!;
        var upsert = repo.GetType().GetMethod("Upsert")!;
        var task = (Task)upsert.Invoke(repo, new object[] { model, ct })!;
        await task;
        var resultProp = task.GetType().GetProperty("Result");
        return resultProp?.GetValue(task);
    }

    // Explicit runtime compatibility path for callers that truly do not know the entity type.
    // Deliberately not an extension: arbitrary objects must not advertise persistence verbs.
    /// <summary>
    /// Runtime compatibility path for deleting an entity whose type is unknown at compile time.
    /// Typed application code should call <c>entity.Remove()</c> instead.
    /// </summary>
    [Obsolete("Use entity.Remove() for typed Entity code. Runtime-only callers may temporarily use AggregateExtensions.Delete(model) until they adopt a typed boundary.")]
    public static async Task<bool> Delete(object model, CancellationToken ct = default)
    {
        if (model is null) throw new System.ArgumentNullException(nameof(model));
        var type = model.GetType();
        var (aggType, keyType) = ResolveAggregateContract(type);
        var id = AggregateMetadata.GetIdValue(model) ?? throw new System.InvalidOperationException("Model has no identifier");
        var data = DataService();
        var getRepo = typeof(IDataService).GetMethod(nameof(IDataService.GetRepository))!;
        var repo = getRepo.MakeGenericMethod(aggType, keyType).Invoke(data, Array.Empty<object>())!;
        var del = repo.GetType().GetMethod("Delete")!;
        var task = (Task)del.Invoke(repo, new object[] { id, ct })!;
        await task;
        var resultProp = task.GetType().GetProperty("Result");
        return resultProp is not null && resultProp.GetValue(task) is bool b && b;
    }

    // Typed instance Remove mirroring Entity<TEntity,TKey>.Remove()
    /// <summary>
    /// Remove a single entity by its id (typed convenience mirroring Entity.Remove).
    /// </summary>
    public static Task<bool> Remove<TEntity, TKey>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    => Model.Entity<TEntity, TKey>.Remove(id: model.Id, ct: ct);

    /// <summary>Remove and distinguish deleted, missing, and lost-race outcomes.</summary>
    public static Task<MutationResult<TEntity, TKey>> RemoveWithOutcome<TEntity, TKey>(
        this TEntity model,
        CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Model.Entity<TEntity, TKey>.RemoveWithOutcome(model.Id, ct);

    // String-key convenience
    /// <summary>
    /// Remove a single entity (string-key convenience).
    /// </summary>
    public static Task<bool> Remove<TEntity>(this TEntity model, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
    => Model.Entity<TEntity, string>.Remove(id: model.Id, ct: ct);

    /// <summary>String-key convenience for an exact delete outcome.</summary>
    public static Task<MutationResult<TEntity, string>> RemoveWithOutcome<TEntity>(
        this TEntity model,
        CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Model.Entity<TEntity, string>.RemoveWithOutcome(model.Id, ct);

    private static (Type Aggregate, Type Key) ResolveAggregateContract(Type t)
    {
        foreach (var itf in t.GetInterfaces())
        {
            if (itf.IsGenericType && itf.GetGenericTypeDefinition() == typeof(IEntity<>))
                return (t, itf.GetGenericArguments()[0]);
        }
        throw new System.InvalidOperationException($"Type {t.Name} does not implement IEntity<TKey>");
    }

    // IEnumerable<T> conveniences
    // Bulk save (generic key)
    /// <summary>
    /// Bulk upsert a collection; providers may optimize to native bulk operations.
    /// </summary>
    public static Task<int> Save<TEntity, TKey>(this IEnumerable<TEntity> models, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Model.Entity<TEntity, TKey>.UpsertMany(models, ct);

    // String-key convenience delegates to generic (cannot infer TKey from constraint)
    /// <summary>
    /// Bulk upsert (string-key convenience).
    /// </summary>
    public static Task<int> Save<TEntity>(this IEnumerable<TEntity> models, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Model.Entity<TEntity, string>.UpsertMany(models, ct);

    // Bulk upsert into a specific partition (generic key)
    /// <summary>
    /// Bulk upsert into a specific logical partition for the aggregate (generic key).
    /// </summary>
    public static Task<int> Save<TEntity, TKey>(this IEnumerable<TEntity> models, string partition, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Model.Entity<TEntity, TKey>.UpsertMany(models, partition, ct);

    // Bulk upsert into a specific partition (string key convenience)
    /// <summary>
    /// Bulk upsert into a specific logical partition (string key convenience).
    /// </summary>
    public static Task<int> Save<TEntity>(this IEnumerable<TEntity> models, string partition, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Model.Entity<TEntity, string>.UpsertMany(models, partition, ct);

    // Bulk remove (delete many) by models collection
    /// <summary>
    /// Bulk remove a collection by projecting ids.
    /// </summary>
    public static Task<int> Remove<TEntity, TKey>(this IEnumerable<TEntity> models, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
        => Data<TEntity, TKey>.DeleteMany(models.Select(m => m.Id), ct);

    // String-key convenience delegates to generic
    /// <summary>
    /// Bulk remove (string-key convenience).
    /// </summary>
    public static Task<int> Remove<TEntity>(this IEnumerable<TEntity> models, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => Data<TEntity, string>.DeleteMany(models.Select(m => m.Id), ct);

    // Replace all contents with provided models
    /// <summary>
    /// Replace the entire partition with the provided models: delete all then upsert.
    /// Intended for dev/test seeding and idempotent resets; avoid on large datasets.
    /// </summary>
    public static async Task<int> SaveReplacing<TEntity, TKey>(this IEnumerable<TEntity> models, CancellationToken ct = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var existing = await Data<TEntity, TKey>.All(ct);
        if (existing.Count > 0)
        {
            var ids = existing.Select(e => e.Id);
            await Data<TEntity, TKey>.DeleteMany(ids, ct);
        }
        return await Data<TEntity, TKey>.UpsertMany(models, ct);
    }

    // String-key convenience delegates to generic
    /// <summary>
    /// String-key convenience for SaveReplacing.
    /// </summary>
    public static Task<int> SaveReplacing<TEntity>(this IEnumerable<TEntity> models, CancellationToken ct = default)
        where TEntity : class, IEntity<string>
        => SaveReplacing<TEntity, string>(models, ct);

    // Convert enumerable to a pre-filled batch (generic key)
    /// <summary>
    /// Convert a collection to a pre-filled batch by queuing add operations.
    /// </summary>
    public static IBatchSet<TEntity, TKey> AsBatch<TEntity, TKey>(this IEnumerable<TEntity> models)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var batch = Data<TEntity, TKey>.Batch();
        foreach (var m in models) batch.Add(m);
        return batch;
    }

    // Convert enumerable to a pre-filled batch (string key convenience)
    /// <summary>
    /// String-key convenience for AsBatch.
    /// </summary>
    public static IBatchSet<TEntity, string> AsBatch<TEntity>(this IEnumerable<TEntity> models)
        where TEntity : class, IEntity<string>
    {
        var batch = Data<TEntity, string>.Batch();
        foreach (var m in models) batch.Add(m);
        return batch;
    }

    // Vice versa helper: add many entities to an existing batch
    /// <summary>
    /// Add many entities to an existing batch for fluent composition.
    /// </summary>
    public static IBatchSet<TEntity, TKey> AddRange<TEntity, TKey>(this IBatchSet<TEntity, TKey> batch, IEnumerable<TEntity> models)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        foreach (var m in models) batch.Add(m);
        return batch;
    }
}

// Static facade: Data<TEntity,TKey>.GetAsync/QueryAsync/Batch()

// Fluent move/copy builder

// Instance-level sugar: model.MoveToPartition("target", fromPartition: null (ambient), copy: false)
