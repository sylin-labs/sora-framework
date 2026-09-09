using System.Collections.Concurrent;
using Koan.Data.Abstractions.Filtering;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Core.KeyValue;
using Koan.Data.Core.Polymorphism;

namespace Koan.Data.Connector.InMemory.Runtime;

/// <summary>Translates the KeyValue family primitives to detached host-memory snapshots.</summary>
internal sealed class InMemoryRepository<TEntity, TKey>(InMemoryState state, string source)
    : KeyValueStore<TEntity, TKey>,
      IConditionalWriteRepository<TEntity, TKey>,
      IInsertOnlyRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private static readonly Type RootType = EntityRootDescriptor.For(typeof(TEntity)).RootType;

    public Task<MutationResult<TEntity, TKey>> Insert(TEntity model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ct.ThrowIfCancellationRequested();
        if (EqualityComparer<TKey>.Default.Equals(model.Id, default!))
            throw new NotSupportedException("InMemory insert-only requires a non-default identity. Assign the entity identity before insertion.");
        var snapshot = new InMemoryState.Record(
            EntityJsonSerialization.SerializeDocument(model), CopyManaged(SnapshotManaged()));
        var inserted = Current().TryAdd(model.Id, snapshot);
        return Task.FromResult(new MutationResult<TEntity, TKey>(model.Id,
            inserted ? MutationOutcome.Inserted : MutationOutcome.Conflict,
            inserted ? model : null,
            inserted ? DataCommitOutcome.Committed : DataCommitOutcome.NotCommitted));
    }

    /// <summary>Compare the observed immutable stored record at the final dictionary update. Ordinary
    /// saves, deletes and clears participate without a separate lock or a read-then-write gap.</summary>
    public async Task<bool> ConditionalReplaceAsync(TEntity model, Filter guard, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(guard);
        ct.ThrowIfCancellationRequested();
        var guardFn = InMemoryFilterEvaluator.CompileConditional<TEntity>(guard);
        var store = Current();
        if (!store.TryGetValue(model.Id, out var observed)) return false;
        if (!guardFn(Materialize(observed).Entity)) return false;
        var prepared = await GuardAndSnapshotAsync(model, ct).ConfigureAwait(false);
        var replacement = new InMemoryState.Record(
            EntityJsonSerialization.SerializeDocument(prepared.Entity), CopyManaged(prepared.Managed));
        ct.ThrowIfCancellationRequested();
        return store.TryUpdate(model.Id, replacement, observed);
    }

    protected override Task<KvRecord<TEntity>?> ReadAsync(TKey id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Current().TryGetValue(id, out var record)
            ? (KvRecord<TEntity>?)Materialize(record)
            : null);
    }

    protected override Task<IReadOnlyList<KvRecord<TEntity>>> ScanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult((IReadOnlyList<KvRecord<TEntity>>)Current()
            .Select(static pair => Materialize(pair.Value))
            .ToArray());
    }

    protected override Task<IReadOnlyList<KvRecord<TEntity>>> ScanBoundedAsync(
        int maxCandidates,
        CancellationToken ct)
    {
        if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        ct.ThrowIfCancellationRequested();
        return Task.FromResult((IReadOnlyList<KvRecord<TEntity>>)Current()
            .Take(maxCandidates)
            .Select(static pair => Materialize(pair.Value))
            .ToArray());
    }

    protected override Task WriteAsync(TKey id, KvRecord<TEntity> record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = new InMemoryState.Record(
            EntityJsonSerialization.SerializeDocument(record.Entity),
            CopyManaged(record.Managed));
        Current()[id] = snapshot;
        return Task.CompletedTask;
    }

    protected override Task<bool> RemoveAsync(TKey id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Current().TryRemove(id, out _));
    }

    protected override Task<int> ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var store = Current();
        var count = store.Count;
        store.Clear();
        return Task.FromResult(count);
    }

    protected override void DescribeBackend(ICapabilities capabilities) =>
        InMemoryFeatures.DescribeBackend(capabilities);

    private ConcurrentDictionary<TKey, InMemoryState.Record> Current() => state.Store<TKey>(
        source,
        RootType,
        Koan.Data.Core.EntityContext.Current?.Partition ?? Infrastructure.Constants.Storage.DefaultPartition);

    private static KvRecord<TEntity> Materialize(InMemoryState.Record record)
    {
        var entity = EntityJsonSerialization.DeserializeDocument(record.EntityJson, typeof(TEntity)) as TEntity
            ?? throw new InvalidDataException(
                $"InMemory snapshot could not materialize '{typeof(TEntity).FullName}'.");
        return new KvRecord<TEntity>(entity, CopyManaged(record.Managed));
    }

    private static IReadOnlyDictionary<string, object?>? CopyManaged(
        IReadOnlyDictionary<string, object?>? values) => values is null
        ? null
        : new Dictionary<string, object?>(values, StringComparer.Ordinal);
}
