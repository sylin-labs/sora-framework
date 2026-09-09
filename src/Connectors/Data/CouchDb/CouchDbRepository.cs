using System.Collections.Frozen;
using System.Linq.Expressions;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Core;
using Koan.Data.Core.Configuration;
using Koan.Data.Core.Optimization;
using Koan.Data.Core.Readiness;
using Koan.Data.Connector.CouchDb.Infrastructure;
using Koan.Data.Connector.CouchDb.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Koan.Data.Connector.CouchDb;

internal sealed class CouchDbRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IOptimizedDataRepository<TEntity, TKey>,
    IConditionalWriteRepository<TEntity, TKey>,
    IInstructionExecutor<TEntity>,
    IDescribesCapabilities,
    IBulkUpsert<TKey>,
    IBulkDelete<TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IServiceProvider _services;
    private readonly CouchDbRoute _route;
    private readonly CouchDbClientManager _clients;
    private readonly CouchDbEntityPlan<TEntity, TKey> _entity;
    private readonly CouchDbQueryCompiler<TEntity, TKey> _queries;
    private readonly DataSourceReadinessCoordinator _readiness;
    private readonly object _containersGate = new();
    private readonly HashSet<string> _validatedContainers = new(StringComparer.Ordinal);

    public CouchDbRepository(
        IServiceProvider services,
        CouchDbRoute route,
        CouchDbClientManager clients,
        MappingPlan? mapping)
    {
        _services = services;
        _route = route;
        _clients = clients;
        _readiness = services.GetRequiredService<DataSourceReadinessCoordinator>();
        _entity = new CouchDbEntityPlan<TEntity, TKey>(services);
        _entity.DemandNoMappingPlan(mapping, route.Source);
        _queries = new CouchDbQueryCompiler<TEntity, TKey>(_entity.IdentityName);
        OptimizationInfo = services.GetStorageOptimization<TEntity, TKey>();
    }

    public StorageOptimizationInfo OptimizationInfo { get; }
    public void Describe(ICapabilities capabilities) => CouchDbFeatures.Describe(capabilities);
    public Task EnsureReady(CancellationToken ct = default) => Ready(ContainerName(), ct);

    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var (found, document) = await client.GetDocumentAsync(container, _entity.IdentityId(id), ct).ConfigureAwait(false);
        return found && document is not null ? _entity.Read(document) : null;
    }

    public async Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var requested = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        if (requested.Count == 0) return [];
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var keys = requested.Select(_entity.IdentityId).ToArray();
        var rows = await client.GetDocumentsAsync(container, keys, ct).ConfigureAwait(false);
        var found = new Dictionary<string, TEntity>(StringComparer.Ordinal);
        foreach (var (_, document, _) in rows)
            if (document is not null)
            {
                var item = _entity.Read(document);
                found[_entity.IdentityId(item.Id)] = item;
            }
        var result = new TEntity?[requested.Count];
        for (var index = 0; index < requested.Count; index++)
            if (found.TryGetValue(_entity.IdentityId(requested[index]), out var item))
                result[index] = item;
        return result;
    }

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        await DemandWrite("upsert", ct).ConfigureAwait(false);
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        await Put(container, model, knownRev: null, ct).ConfigureAwait(false);
        return model;
    }

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        var values = models as IReadOnlyList<TEntity> ?? models.ToArray();
        if (values.Count == 0) return 0;
        await DemandWrite("bulk upsert", ct).ConfigureAwait(false);
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        await BulkPut(container, values, ct).ConfigureAwait(false);
        return values.Count;
    }

    public async Task<bool> Delete(TKey id, CancellationToken ct = default)
    {
        await DemandWrite("delete", ct).ConfigureAwait(false);
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var key = _entity.IdentityId(id);
        var (found, document) = await client.GetDocumentAsync(container, key, ct).ConfigureAwait(false);
        if (!found || document is null) return false;
        DemandScope(document, key);
        var rev = document.Value<string>(Constants.Storage.Rev)
            ?? throw new InvalidOperationException($"CouchDB document '{container}/{key}' carries no revision to delete.");
        return await client.DeleteDocumentAsync(container, key, rev, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var values = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        if (values.Count == 0) return 0;
        await DemandWrite("bulk delete", ct).ConfigureAwait(false);
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var keys = values.Select(_entity.IdentityId).ToArray();
        var rows = await client.GetDocumentsAsync(container, keys, ct).ConfigureAwait(false);
        var docs = new List<JObject>(rows.Length);
        foreach (var (id, document, rev) in rows)
        {
            if (document is null || rev is null) continue;
            DemandScope(document, id);
            docs.Add(new JObject
            {
                [Constants.Storage.Identity] = id,
                [Constants.Storage.Rev] = rev,
                ["_deleted"] = true
            });
        }
        if (docs.Count == 0) return 0;
        var results = await client.BulkDocsAsync(container, docs, ct).ConfigureAwait(false);
        return results.Count(static row => row.Value<bool>("ok") == true);
    }

    public async Task<int> DeleteAll(CancellationToken ct = default)
    {
        await DemandWrite("delete all", ct).ConfigureAwait(false);
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var rows = await client.AllRevisionsAsync(container, ct).ConfigureAwait(false);
        if (rows.Count == 0) return 0;
        var docs = rows.Select(row => new JObject
        {
            [Constants.Storage.Identity] = row.Item1,
            [Constants.Storage.Rev] = row.Item2,
            ["_deleted"] = true
        }).ToArray();
        var results = await client.BulkDocsAsync(container, docs, ct).ConfigureAwait(false);
        return results.Count(static row => row.Value<bool>("ok") == true);
    }

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        if (strategy == RemoveStrategy.Fast)
        {
            // CouchDB has no truncate; the non-structural fast path is the same bulk delete, so the
            // honest count comes back instead of a -1 this store cannot back.
            _route.Policy.Demand(DataOperationEffect.SchemaOrAdmin, "fast remove");
            return await DeleteAll(ct).ConfigureAwait(false);
        }
        var count = await Count(new QueryDefinition { CountStrategy = CountStrategy.Exact }, ct).ConfigureAwait(false);
        await DeleteAll(ct).ConfigureAwait(false);
        return count.Value;
    }

    public async Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var selector = query.Filter is null ? new JObject() : _queries.Selector(query.Filter);
        var (sort, handled) = Sort(query.Sort);
        var sortComplete = query.Sort.Count == 0 || handled.Count == query.Sort.Count;
        var paged = query.HasPagination && sortComplete;

        long? total = null;
        if (query.CountStrategy is { })
            total = await CountExact(client, container, selector, ct).ConfigureAwait(false);

        var documents = await client.FindAsync(
            container,
            selector,
            sort,
            paged ? query.EffectivePageSize() : null,
            paged ? query.EffectiveOffset() : null,
            fields: null,
            ct).ConfigureAwait(false);

        return new RepositoryQueryResult<TEntity>
        {
            Items = documents.Select(_entity.Read).ToArray(),
            FilterHandled = query.Filter is not null,
            TotalCount = total,
            CountExecution = query.CountStrategy is null ? CountExecutionKind.None : CountExecutionKind.Exact,
            SortHandled = handled,
            PaginationHandled = paged
        };
    }

    public async Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        var counted = query.WithoutPagination().WithCountStrategy(CountStrategy.Exact);
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var selector = counted.Filter is null ? new JObject() : _queries.Selector(counted.Filter);
        return CountResult.Exact(await CountExact(client, container, selector, ct).ConfigureAwait(false));
    }

    /// <summary>Mango has no count endpoint; empty-field rows are materialized and counted — exact, honest.</summary>
    private static async Task<long> CountExact(CouchDbClient client, string container, JObject selector, CancellationToken ct) =>
        (await client.FindAsync(container, selector, sort: null, limit: null, skip: null, fields: [], ct).ConfigureAwait(false)).Count;

    public async Task<bool> ConditionalReplaceAsync(
        TEntity model,
        Filter guard,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(guard);
        var predicate = InMemoryFilterEvaluator.CompileConditional<TEntity>(guard);
        await DemandWrite("conditional replace", ct).ConfigureAwait(false);
        var container = await ReadyContainer(ct).ConfigureAwait(false);
        var client = _clients.Get(_route);
        var key = _entity.IdentityId(model);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (found, document) = await client.GetDocumentAsync(container, key, ct).ConfigureAwait(false);
            if (!found || document is null) return false;
            DemandScope(document, key);
            var current = _entity.Read(document);
            if (!predicate(current)) return false;
            var rev = document.Value<string>(Constants.Storage.Rev);
            try
            {
                await Put(container, model, rev, ct).ConfigureAwait(false);
                return true;
            }
            catch (CouchDbException error) when (attempt == 0 && IsConflict(error))
            {
                // The row moved under us; one re-read re-evaluates the guard against what is really stored.
            }
        }
        return false;
    }

    public IBatchSet<TEntity, TKey> CreateBatch() => new Batch(this);

    public async Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        switch (instruction.Name)
        {
            case DataInstructions.EnsureCreated:
                await EnsureReady(ct).ConfigureAwait(false);
                return Cast<TResult>(true);
            case DataInstructions.Clear:
                return Cast<TResult>(await DeleteAll(ct).ConfigureAwait(false));
            default:
                throw new NotSupportedException(
                    $"Instruction '{instruction.Name}' is not supported by CouchDB for '{typeof(TEntity).Name}'.");
        }
    }

    // ---- write path ----

    /// <summary>Read-rev-then-PUT. A scope in effect is enforced on the stored document before the write.</summary>
    private async Task Put(string container, TEntity model, string? knownRev = null, CancellationToken ct = default)
    {
        var client = _clients.Get(_route);
        var key = _entity.IdentityId(model);
        var rev = knownRev;
        if (rev is null)
        {
            var (found, stored) = await client.GetDocumentAsync(container, key, ct).ConfigureAwait(false);
            if (found && stored is not null)
            {
                DemandScope(stored, key);
                rev = stored.Value<string>(Constants.Storage.Rev);
            }
        }
        var body = _entity.Write(model);
        try
        {
            await client.PutDocumentAsync(container, key, body, rev, ct).ConfigureAwait(false);
        }
        catch (CouchDbException error) when (IsConflict(error) && rev is null)
        {
            // Create raced an insert; classify through the same boundary as a scoped takeover only when
            // a scope is in effect — otherwise surface the conflict by name.
            if (ManagedFieldWriteScope.Current is { Count: > 0 })
                throw new InvalidOperationException($"The write was rejected as a cross-scope write to CouchDB '{container}/{key}'.", error);
            throw;
        }
    }

    private async Task BulkPut(string container, IReadOnlyList<TEntity> models, CancellationToken ct)
    {
        var client = _clients.Get(_route);
        var keys = models.Select(_entity.IdentityId).ToArray();
        var rows = await client.GetDocumentsAsync(container, keys, ct).ConfigureAwait(false);
        var revs = rows.Where(static row => row.Doc is not null)
            .ToDictionary(static row => row.Id, static row => row.Rev, StringComparer.Ordinal);
        var docs = new List<JObject>(models.Count);
        foreach (var model in models)
        {
            var key = _entity.IdentityId(model);
            if (revs.TryGetValue(key, out var stored))
            {
                var (found, document) = await client.GetDocumentAsync(container, key, ct).ConfigureAwait(false);
                if (found && document is not null) DemandScope(document, key);
            }
            var body = _entity.Write(model);
            body[Constants.Storage.Identity] = key;
            if (revs.TryGetValue(key, out var rev)) body[Constants.Storage.Rev] = rev;
            docs.Add(body);
        }
        var results = await client.BulkDocsAsync(container, docs, ct).ConfigureAwait(false);
        var failure = results.FirstOrDefault(static row => row.Value<bool>("ok") != true);
        if (failure is not null)
            throw new InvalidOperationException(
                $"CouchDB rejected one bulk write for '{container}/{failure.Value<string>("id")}': " +
                $"{failure.Value<string>("error")} — {failure.Value<string>("reason")}.");
    }

    private void DemandScope(JObject stored, string key)
    {
        if (_entity.MatchesWriteGuard(stored)) return;
        throw new InvalidOperationException($"The write was rejected as a cross-scope write to CouchDB document '{key}'.");
    }

    // ---- readiness ----

    private async Task<string> ReadyContainer(CancellationToken ct)
    {
        var container = ContainerName();
        await Ready(container, ct).ConfigureAwait(false);
        return container;
    }

    private async Task Ready(string container, CancellationToken ct)
    {
        lock (_containersGate)
        {
            if (_validatedContainers.Contains(container)) return;
        }
        var target = $"{_route.DatabasePrefix}/{container}";
        if (_route.Policy.StorageLifecycle is StorageLifecycle.Managed &&
            _route.Policy.Access is DataSourceAccess.ReadWrite)
        {
            await _readiness.Provision(_route.Policy, target,
                token => Provision(container, token),
                token => Validate(container, token), ct).ConfigureAwait(false);
        }
        else
        {
            await _readiness.ValidateShape(_route.Policy, target,
                token => Validate(container, token), ct).ConfigureAwait(false);
        }
        lock (_containersGate)
        {
            _validatedContainers.Add(container);
        }
    }

    private async Task Provision(string container, CancellationToken ct)
    {
        var client = _clients.Get(_route);
        if (!await client.DatabaseExistsAsync(container, ct).ConfigureAwait(false))
            await client.CreateDatabaseAsync(container, ct).ConfigureAwait(false);
    }

    private Task Validate(string container, CancellationToken ct)
    {
        // Declared-shape validation for this store is database existence: the database IS the container.
        return Task.CompletedTask;
    }

    private Task DemandWrite(string operation, CancellationToken ct)
    {
        _route.Policy.Demand(DataOperationEffect.Write, operation);
        return Task.CompletedTask;
    }

    private string ContainerName()
    {
        // One entity container is one database under the route's prefix; the naming provider composes
        // the ambient partition, so container isolation resolves to a distinct physical database.
        var collection = Core.Configuration.AdapterNaming.GetOrCompute<TEntity, TKey>(_services);
        return DatabaseFor(collection, _route.DatabasePrefix);
    }

    internal static string DatabaseFor(string collection, string prefix)
    {
        var name = $"{prefix}_{collection}".ToLowerInvariant();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        return builder.ToString();
    }

    private (IReadOnlyList<JObject>? Sort, IReadOnlySet<SortSpec> Handled) Sort(IReadOnlyList<SortSpec> sort)
    {
        if (sort.Count == 0) return (null, RepositoryQueryResult<TEntity>.NoSortHandled);
        // Only the identity sorts: CouchDB's _all_docs index answers _id; every other field needs a
        // Mango index this adapter does not create, so it is a declared fallback, never a fake claim.
        var clauses = new List<JObject>(sort.Count);
        var handled = new List<SortSpec>(sort.Count);
        foreach (var spec in sort)
        {
            if (spec.Path.Members.Count != 1 ||
                !string.Equals(spec.Path.Members[0].Name, _entity.IdentityName, StringComparison.Ordinal) ||
                spec.Path.TraversesCollection)
                continue;
            clauses.Add(new JObject
            {
                [Constants.Storage.Identity] = spec.Desc ? "desc" : "asc"
            });
            handled.Add(spec);
        }
        if (handled.Count != sort.Count) return (null, RepositoryQueryResult<TEntity>.NoSortHandled);
        return (clauses, handled.ToFrozenSet());
    }

    private static bool IsConflict(CouchDbException error) =>
        error.Status == 409 || string.Equals(error.Error, "conflict", StringComparison.OrdinalIgnoreCase);

    private static TResult Cast<TResult>(object? value)
    {
        if (value is TResult typed) return typed;
        if (value is null) return default!;
        return (TResult)Convert.ChangeType(value, typeof(TResult), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Batch(CouchDbRepository<TEntity, TKey> repository) : IBatchSet<TEntity, TKey>
    {
        private readonly List<TEntity> _adds = [];
        private readonly List<TEntity> _updates = [];
        private readonly List<(TKey Id, Action<TEntity> Mutate)> _mutations = [];
        private readonly List<TKey> _deletes = [];

        // _bulk_docs commits per document: outcomes are complete, atomicity is not claimed.
        public BatchExecutionCapabilities ExecutionCapabilities => BatchExecutionCapabilities.CompleteItemOutcomes;
        public IBatchSet<TEntity, TKey> Add(TEntity entity) { _adds.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TEntity entity) { _updates.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TKey id, Action<TEntity> mutate) { _mutations.Add((id, mutate)); return this; }
        public IBatchSet<TEntity, TKey> Delete(TKey id) { _deletes.Add(id); return this; }
        public IBatchSet<TEntity, TKey> Clear() { _adds.Clear(); _updates.Clear(); _mutations.Clear(); _deletes.Clear(); return this; }

        public async Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            var total = _adds.Count + _updates.Count + _mutations.Count + _deletes.Count;
            if (total == 0) return new BatchResult(0, 0, 0);
            if (options?.MaxItems is { } bound && total > bound)
                throw new InvalidOperationException($"The CouchDB batch has {total} operations, exceeding its declared bound of {bound}.");
            if (options?.RequireAtomic == true)
                throw new NotSupportedException(
                    "CouchDB _bulk_docs commits per document; this adapter does not claim atomic batch execution.");
            var container = await repository.ReadyContainer(ct).ConfigureAwait(false);
            var client = repository._clients.Get(repository._route);

            var mutationModels = new List<TEntity>(_mutations.Count);
            if (_mutations.Count > 0)
            {
                var loaded = await repository.GetMany(_mutations.Select(static mutation => mutation.Id), ct).ConfigureAwait(false);
                for (var index = 0; index < _mutations.Count; index++)
                {
                    if (loaded[index] is not { } current) continue;
                    _mutations[index].Mutate(current);
                    mutationModels.Add(current);
                }
            }

            var writes = _adds.Concat(_updates).Concat(mutationModels).ToArray();
            var outcomes = new List<BatchItemResult>(total);
            var added = 0;
            var updated = 0;
            if (writes.Length > 0)
            {
                var keys = writes.Select(repository._entity.IdentityId).ToArray();
                var rows = await client.GetDocumentsAsync(container, keys, ct).ConfigureAwait(false);
                var revs = rows.Where(static row => row.Doc is not null)
                    .ToDictionary(static row => row.Id, static row => row.Rev, StringComparer.Ordinal);
                var docs = new List<JObject>(writes.Length);
                foreach (var model in writes)
                {
                    var key = repository._entity.IdentityId(model);
                    var body = repository._entity.Write(model);
                    body[Constants.Storage.Identity] = key;
                    if (revs.TryGetValue(key, out var rev)) body[Constants.Storage.Rev] = rev;
                    docs.Add(body);
                }
                var results = await client.BulkDocsAsync(container, docs, ct).ConfigureAwait(false);
                for (var index = 0; index < results.Count; index++)
                {
                    var ok = results[index].Value<bool>("ok") == true;
                    if (index < _adds.Count) added += ok ? 1 : 0;
                    else updated += ok ? 1 : 0;
                    outcomes.Add(new BatchItemResult(index, index < _adds.Count ? BatchOperation.Add : BatchOperation.Update,
                        ok ? BatchItemOutcome.Applied : BatchItemOutcome.Conflict));
                }
            }

            var deleted = 0;
            if (_deletes.Count > 0)
            {
                var keys = _deletes.Select(repository._entity.IdentityId).ToArray();
                var rows = await client.GetDocumentsAsync(container, keys, ct).ConfigureAwait(false);
                var docs = rows.Where(static row => row.Doc is not null && row.Rev is not null)
                    .Select(row => new JObject
                    {
                        [Constants.Storage.Identity] = row.Id,
                        [Constants.Storage.Rev] = row.Rev!,
                        ["_deleted"] = true
                    }).ToArray();
                var results = await client.BulkDocsAsync(container, docs, ct).ConfigureAwait(false);
                var index = writes.Length;
                foreach (var row in results)
                {
                    var ok = row.Value<bool>("ok") == true;
                    deleted += ok ? 1 : 0;
                    outcomes.Add(new BatchItemResult(index++, BatchOperation.Delete,
                        ok ? BatchItemOutcome.Applied : BatchItemOutcome.Missing));
                }
            }

            return new BatchResult(added, updated, deleted)
            {
                Atomicity = BatchAtomicity.NotGuaranteed,
                Items = outcomes,
                HasCompleteItemOutcomes = true
            };
        }
    }
}
