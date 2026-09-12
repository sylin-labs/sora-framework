using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;

namespace Koan.Data.Core.Polymorphism;

/// <summary>
/// Exact-variant CRUD view over one root semantic repository. It never selects or constructs a physical adapter.
/// </summary>
internal sealed class EntityVariantRepository<TRoot, TVariant, TKey> :
    IDataRepository<TVariant, TKey>,
    IInsertOnlyRepository<TVariant, TKey>,
    IQueryRepository<TVariant, TKey>,
    IDescribesCapabilities
    where TRoot : class, IEntity<TKey>
    where TVariant : class, TRoot, IEntity<TKey>
    where TKey : notnull
{
    private readonly Func<IDataRepository<TRoot, TKey>> _root;

    public EntityVariantRepository(Func<IDataRepository<TRoot, TKey>> root)
        => _root = root ?? throw new ArgumentNullException(nameof(root));

    public Task EnsureReady(CancellationToken ct = default) => _root().EnsureReady(ct);

    public async Task<TVariant?> Get(TKey id, CancellationToken ct = default)
    {
        var root = _root();
        using var _ = EntityMaterializationScope.Enter(typeof(TRoot), typeof(TVariant));
        return Convert(await root.Get(id, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<TVariant?>> GetMany(
        IEnumerable<TKey> ids,
        CancellationToken ct = default)
    {
        var root = _root();
        using var _ = EntityMaterializationScope.Enter(typeof(TRoot), typeof(TVariant));
        var values = await root.GetMany(ids, ct).ConfigureAwait(false);
        return values.Select(Convert).ToArray();
    }

    public async Task<MutationResult<TVariant, TKey>> Insert(TVariant model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        EntityTypeCatalog.Register(typeof(TVariant));
        if (_root() is not IInsertOnlyRepository<TRoot, TKey> inserts)
            throw new NotSupportedException($"The root repository for {typeof(TVariant).Name} does not support atomic insertion.");
        var result = await inserts.Insert(model, ct).ConfigureAwait(false);
        return new(result.Key, result.Outcome, Convert(result.Entity), result.CommitOutcome);
    }

    public async Task<TVariant> Upsert(TVariant model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        EntityTypeCatalog.Register(typeof(TVariant));
        var value = await _root().Upsert(model, ct).ConfigureAwait(false);
        return Convert(value)
            ?? throw new InvalidDataException(
                $"Root repository '{typeof(TRoot).FullName}' returned null after saving '{typeof(TVariant).FullName}'.");
    }

    public Task<bool> Delete(TKey id, CancellationToken ct = default) => _root().Delete(id, ct);

    public Task<int> UpsertMany(IEnumerable<TVariant> models, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        EntityTypeCatalog.Register(typeof(TVariant));
        return _root().UpsertMany(models.Cast<TRoot>(), ct);
    }

    public Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
        => _root().DeleteMany(ids, ct);

    public Task<int> DeleteAll(CancellationToken ct = default)
        => throw SetOperation(nameof(DeleteAll));

    public Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
        => throw SetOperation(nameof(RemoveAll));

    public Task<RepositoryQueryResult<TVariant>> Query(
        QueryDefinition query,
        CancellationToken ct = default)
        => throw SetOperation(nameof(Query));

    public Task<CountResult> Count(
        QueryDefinition query,
        CancellationToken ct = default)
        => throw SetOperation(nameof(Count));

    public IBatchSet<TVariant, TKey> CreateBatch()
        => new VariantBatch(_root().CreateBatch());

    public void Describe(ICapabilities caps)
    {
        var root = _root();
        if (root is IDescribesCapabilities)
        {
            var rootCapabilities = DataCaps.Describe(root, typeof(TRoot).Name);
            foreach (var token in rootCapabilities.All)
            {
                if (token == DataCaps.Write.ConditionalReplace) continue;
                if (rootCapabilities.Detail<object>(token) is { } detail) caps.Add(token, detail);
                else caps.Add(token);
            }
        }
    }

    private static TVariant? Convert(TRoot? value)
    {
        if (value is null)
        {
            return default;
        }

        if (value is TVariant variant)
        {
            return variant;
        }

        throw new InvalidDataException(
            $"Entity '{value.Id}' belongs to runtime type '{value.GetType().FullName}', not requested variant " +
            $"'{typeof(TVariant).FullName}'.");
    }

    private static NotSupportedException SetOperation(string operation)
        => new(
            $"'{operation}' is a set operation and cannot target variant '{typeof(TVariant).Name}' independently. " +
            $"Use the Entity root '{typeof(TRoot).Name}'.");

    private sealed class VariantBatch(IBatchSet<TRoot, TKey> root) : IBatchSet<TVariant, TKey>
    {
        public BatchExecutionCapabilities ExecutionCapabilities => root.ExecutionCapabilities;

        public IBatchSet<TVariant, TKey> Add(TVariant entity)
        {
            root.Add(entity);
            return this;
        }

        public IBatchSet<TVariant, TKey> Update(TVariant entity)
        {
            root.Update(entity);
            return this;
        }

        public IBatchSet<TVariant, TKey> Update(TKey id, Action<TVariant> mutate)
        {
            root.Update(id, entity =>
            {
                if (entity is not TVariant variant)
                {
                    throw new InvalidDataException(
                        $"Batch update '{id}' resolved '{entity.GetType().FullName}', not '{typeof(TVariant).FullName}'.");
                }

                mutate(variant);
            });
            return this;
        }

        public IBatchSet<TVariant, TKey> Delete(TKey id)
        {
            root.Delete(id);
            return this;
        }

        public IBatchSet<TVariant, TKey> Clear()
            => throw SetOperation("Batch.Clear");

        public Task<BatchResult> Save(
            BatchOptions? options = null,
            CancellationToken ct = default)
            => root.Save(options, ct);
    }
}
