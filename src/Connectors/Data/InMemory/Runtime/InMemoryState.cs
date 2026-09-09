using System.Collections.Concurrent;
namespace Koan.Data.Connector.InMemory.Runtime;

/// <summary>Finite host-owned storage for detached Entity snapshots.</summary>
internal sealed class InMemoryState
{
    private readonly ConcurrentDictionary<StoreKey, object> _stores = new();
    private readonly object _creationGate = new();

    internal ConcurrentDictionary<TKey, Record> Store<TKey>(string source, Type root, string partition)
        where TKey : notnull
    {
        var key = new StoreKey(source, root, partition);
        if (_stores.TryGetValue(key, out var current))
            return Cast<TKey>(current, key);

        lock (_creationGate)
        {
            if (_stores.TryGetValue(key, out current)) return Cast<TKey>(current, key);
            if (_stores.Count >= Infrastructure.Constants.Provider.MaximumStoresPerHost)
                throw new InvalidOperationException(
                    $"InMemory reached the host bound of {Infrastructure.Constants.Provider.MaximumStoresPerHost} " +
                    "source/root/partition stores.");
            var created = new ConcurrentDictionary<TKey, Record>();
            if (!_stores.TryAdd(key, created))
                throw new InvalidOperationException("InMemory could not publish a newly reserved host store.");
            return created;
        }
    }

    internal readonly record struct Record(
        string EntityJson,
        IReadOnlyDictionary<string, object?>? Managed);

    private static ConcurrentDictionary<TKey, Record> Cast<TKey>(object value, StoreKey key)
        where TKey : notnull => value as ConcurrentDictionary<TKey, Record>
        ?? throw new InvalidOperationException(
            $"InMemory root '{key.Root.FullName}' was requested with conflicting key types.");

    private readonly record struct StoreKey(string Source, Type Root, string Partition);
}
