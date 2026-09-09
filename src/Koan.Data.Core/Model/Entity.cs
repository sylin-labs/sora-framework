// Central static provider for relationship metadata DI accessor
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Koan.Data.Abstractions;
using Koan.Data.Core;
using Koan.Data.Core.Transfers;
using Koan.Data.Core.Lifecycle;
using Koan.Data.Core.Relationships;
using Koan.Data.Core.Selection;

namespace Koan.Data.Core.Model
{

    // Domain-centric CRTP base with static conveniences, independent of data namespace
    public abstract partial class Entity<TEntity, TKey> : IEntity<TKey>
        where TEntity : class, Koan.Data.Abstractions.IEntity<TKey>
        where TKey : notnull
    {
        // Cached method delegates for fast facade/method access
        private static readonly Dictionary<(Type, string), Func<object, object?[], object?>> MethodDelegateCache = new();
        private static readonly object MethodDelegateLock = new();

        protected static Func<object, object?[], object?> GetMethodDelegate(Type type, string methodName)
        {
            var key = (type, methodName);
            if (MethodDelegateCache.TryGetValue(key, out var del))
                return del;
            lock (MethodDelegateLock)
            {
                if (MethodDelegateCache.TryGetValue(key, out del))
                    return del;
                var method = type.GetMethod(methodName);
                if (method == null)
                    throw new ArgumentException($"Method '{methodName}' not found on type '{type.Name}'");
                var instanceParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
                var argsParam = System.Linq.Expressions.Expression.Parameter(typeof(object[]), "args");
                var castInstance = System.Linq.Expressions.Expression.Convert(instanceParam, type);
                var parameters = method.GetParameters();
                var callArgs = new System.Linq.Expressions.Expression[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var argAccess = System.Linq.Expressions.Expression.ArrayIndex(argsParam, System.Linq.Expressions.Expression.Constant(i));
                    callArgs[i] = System.Linq.Expressions.Expression.Convert(argAccess, parameters[i].ParameterType);
                }
                var call = System.Linq.Expressions.Expression.Call(castInstance, method, callArgs);
                var convertResult = System.Linq.Expressions.Expression.Convert(call, typeof(object));
                var lambda = System.Linq.Expressions.Expression.Lambda<Func<object, object?[], object?>>(convertResult, instanceParam, argsParam);
                del = lambda.Compile();
                MethodDelegateCache[key] = del;
                return del;
            }
        }
        // Example usage:
        // var result = GetMethodDelegate(typeof(TEntity), "FacadeMethodName")(entity, new object?[] { arg1, arg2 });
        [Key]
        public virtual TKey Id { get; set; } = default!;

        /// <summary>Declares host-owned behavior around this entity's persistence lifecycle.</summary>
        public static EntityLifecycleBuilder<TEntity, TKey> Lifecycle { get; } = new();

        // Static conveniences forward to the data facade without exposing its namespace in domain types
        public static Task<TEntity?> Get(TKey id, CancellationToken ct = default)
            => Data<TEntity, TKey>.Get(id, ct);
        public static Task<IReadOnlyList<TEntity?>> Get(IEnumerable<TKey> ids, CancellationToken ct = default)
            => Data<TEntity, TKey>.GetMany(ids, ct);
        // Partition-aware variants
        public static Task<TEntity?> Get(TKey id, string partition, CancellationToken ct = default)
            => Data<TEntity, TKey>.Get(id, partition, ct);
        public static Task<IReadOnlyList<TEntity?>> Get(IEnumerable<TKey> ids, string partition, CancellationToken ct = default)
            => Data<TEntity, TKey>.GetMany(ids, partition, ct);

        public static Task<IReadOnlyList<TEntity>> All(CancellationToken ct = default)
            => Data<TEntity, TKey>.All(ct);

        public static Task<IReadOnlyList<TEntity>> All(QueryDefinition query, CancellationToken ct = default)
            => Data<TEntity, TKey>.All(query, ct);

        public static Task<QueryResult<TEntity>> AllWithCount(QueryDefinition? query = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.AllWithCount(query, ct);
        public static Task<IReadOnlyList<TEntity>> All(string partition, CancellationToken ct = default)
            => Data<TEntity, TKey>.All(partition, ct);
        public static Task<IReadOnlyList<TEntity>> Query(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
            => Data<TEntity, TKey>.Query(predicate, ct);

        public static Task<IReadOnlyList<TEntity>> Query(Expression<Func<TEntity, bool>> predicate, QueryDefinition? query, CancellationToken ct = default)
            => Data<TEntity, TKey>.Query(predicate, query, ct);

        public static Task<QueryResult<TEntity>> QueryWithCount(Expression<Func<TEntity, bool>> predicate, QueryDefinition? query = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryWithCount(predicate, query, ct);

        /// <summary>Query via the JSON filter DSL (e.g. <c>{ "Tags": { "$in": ["x"] } }</c>). The string
        /// surface is the provider-agnostic DSL; for provider-native queries use <see cref="QueryRaw"/>.</summary>
        public static Task<IReadOnlyList<TEntity>> Query(string filterJson, CancellationToken ct = default)
            => Data<TEntity, TKey>.Query(filterJson, ct);

        public static Task<IReadOnlyList<TEntity>> Query(string filterJson, QueryDefinition? query, CancellationToken ct = default)
            => Data<TEntity, TKey>.Query(filterJson, query, ct);

        public static Task<QueryResult<TEntity>> QueryWithCount(string filterJson, QueryDefinition? query = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryWithCount(filterJson, query, ct);

        /// <summary>Provider-native query escape hatch (raw SQL/N1QL/etc.), gated by adapter support.</summary>
        public static Task<IReadOnlyList<TEntity>> QueryRaw(string providerQuery, object? parameters = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryRaw(providerQuery, parameters, null, ct);

        // Streaming (IAsyncEnumerable). Supported adapters enforce bounded candidate pages; unsupported
        // execution rejects rather than silently materializing the complete result.
        public static IAsyncEnumerable<TEntity> AllStream(int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.AllStream(batchSize, ct);
        [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
        public static IAsyncEnumerable<TEntity> AllStream(CancellationToken ct)
            => Data<TEntity, TKey>.AllStream(ct);
        public static IAsyncEnumerable<TEntity> AllStream(string sort, int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.AllStream(sort, batchSize, ct);
        [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
        public static IAsyncEnumerable<TEntity> AllStream(string sort, CancellationToken ct)
            => Data<TEntity, TKey>.AllStream(sort, ct);
        public static IAsyncEnumerable<TEntity> AllStream(Action<Koan.Data.Core.Sorting.ISortBuilder<TEntity>> sort, int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.AllStream(sort, batchSize, ct);
        [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
        public static IAsyncEnumerable<TEntity> AllStream(Action<Koan.Data.Core.Sorting.ISortBuilder<TEntity>> sort, CancellationToken ct)
            => Data<TEntity, TKey>.AllStream(sort, ct);

        public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryStream(predicate, batchSize, ct);
        [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
        public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, CancellationToken ct)
            => Data<TEntity, TKey>.QueryStream(predicate, ct);
        public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, string sort, int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryStream(predicate, sort, batchSize, ct);
        [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
        public static IAsyncEnumerable<TEntity> QueryStream(Expression<Func<TEntity, bool>> predicate, string sort, CancellationToken ct)
            => Data<TEntity, TKey>.QueryStream(predicate, sort, ct);
        public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryStream(filterJson, batchSize, ct);
        [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
        public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, CancellationToken ct)
            => Data<TEntity, TKey>.QueryStream(filterJson, ct);
        public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, string sort, int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryStream(filterJson, sort, batchSize, ct);

        /// <summary>Stream a structured query through provider-bounded pages.</summary>
        public static IAsyncEnumerable<TEntity> QueryStream(QueryDefinition query, int? batchSize = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.QueryStream(query, batchSize, ct);
        [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
        public static IAsyncEnumerable<TEntity> QueryStream(string filterJson, string sort, CancellationToken ct)
            => Data<TEntity, TKey>.QueryStream(filterJson, sort, ct);

        // Basic paging helpers (materialized). Sort overloads thread through the orchestrator (ADR-0093).
        public static Task<IReadOnlyList<TEntity>> FirstPage(int size, CancellationToken ct = default)
            => Data<TEntity, TKey>.FirstPage(size, ct);
        public static Task<IReadOnlyList<TEntity>> FirstPage(int size, string sort, CancellationToken ct = default)
            => Data<TEntity, TKey>.FirstPage(size, sort, ct);
        public static Task<IReadOnlyList<TEntity>> FirstPage(int size, Action<Koan.Data.Core.Sorting.ISortBuilder<TEntity>> sort, CancellationToken ct = default)
            => Data<TEntity, TKey>.FirstPage(size, sort, ct);

        public static Task<IReadOnlyList<TEntity>> Page(int page, int size, CancellationToken ct = default)
            => Data<TEntity, TKey>.Page(page, size, ct);
        public static Task<IReadOnlyList<TEntity>> Page(int page, int size, string sort, CancellationToken ct = default)
            => Data<TEntity, TKey>.Page(page, size, sort, ct);
        public static Task<IReadOnlyList<TEntity>> Page(int page, int size, Action<Koan.Data.Core.Sorting.ISortBuilder<TEntity>> sort, CancellationToken ct = default)
            => Data<TEntity, TKey>.Page(page, size, sort, ct);

        // All with sort — lambda form only (string form ambiguous with existing partition overload; use
        // Entity.All(new QueryDefinition().WithSort("...")) for the string surface).
        public static Task<IReadOnlyList<TEntity>> All(Action<Koan.Data.Core.Sorting.ISortBuilder<TEntity>> sort, CancellationToken ct = default)
            => Data<TEntity, TKey>.All(sort, ct);

        // Query+predicate with sort
        public static Task<IReadOnlyList<TEntity>> Query(Expression<Func<TEntity, bool>> predicate, Action<Koan.Data.Core.Sorting.ISortBuilder<TEntity>> sort, CancellationToken ct = default)
            => Data<TEntity, TKey>.Query(predicate, sort, ct);

        // Counts
        // Simple: await Entity.Count → defaults to optimized
        // Explicit: await Entity.Count.Exact(ct), await Entity.Count.Fast(ct)
        public static EntityCountAccessor Count { get; } = new();

        /// <summary>
        /// Awaitable count accessor with fluent API.
        /// Simple: await Todo.Count (defaults to optimized)
        /// Explicit: await Todo.Count.Exact(ct), await Todo.Count.Fast(ct)
        /// </summary>
        public sealed class EntityCountAccessor
        {
            // Default: await Entity.Count → Optimized strategy
            public System.Runtime.CompilerServices.TaskAwaiter<long> GetAwaiter()
                => Data<TEntity, TKey>.Count(CountStrategy.Optimized, default).GetAwaiter();

            public Task<long> Exact(CancellationToken ct = default)
                => Data<TEntity, TKey>.Count(ct);

            public Task<long> Fast(CancellationToken ct = default)
                => Data<TEntity, TKey>.Count(CountStrategy.Fast, ct);

            public Task<long> Optimized(CancellationToken ct = default)
                => Data<TEntity, TKey>.Count(CountStrategy.Optimized, ct);

            public Task<long> Where(Expression<Func<TEntity, bool>> predicate, CountStrategy strategy = CountStrategy.Optimized, CancellationToken ct = default)
                => Data<TEntity, TKey>.Count(predicate, strategy, ct);

            public Task<long> Query(QueryDefinition query, CancellationToken ct = default)
                => Data<TEntity, TKey>.Count(query, ct);

            public Task<long> Partition(string partition, CountStrategy strategy = CountStrategy.Exact, CancellationToken ct = default)
                => Data<TEntity, TKey>.Count(partition, strategy, ct);
        }
        public static IBatchSet<TEntity, TKey> Batch() => Data<TEntity, TKey>.Batch();

        public static CopyTransferBuilder<TEntity, TKey> Copy()
            => Data<TEntity, TKey>.Copy();

        public static CopyTransferBuilder<TEntity, TKey> Copy(Expression<Func<TEntity, bool>> predicate)
            => Data<TEntity, TKey>.Copy(predicate);

        public static MoveTransferBuilder<TEntity, TKey> Move()
            => Data<TEntity, TKey>.Move();

        public static MoveTransferBuilder<TEntity, TKey> Move(Expression<Func<TEntity, bool>> predicate)
            => Data<TEntity, TKey>.Move(predicate);

        public static MirrorTransferBuilder<TEntity, TKey> Mirror(MirrorMode mode = MirrorMode.Push)
            => Data<TEntity, TKey>.Mirror(mode);

        public static MirrorTransferBuilder<TEntity, TKey> Mirror(Expression<Func<TEntity, bool>> predicate, MirrorMode mode = MirrorMode.Push)
            => Data<TEntity, TKey>.Mirror(predicate, mode);

        public static Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            return Data<TEntity, TKey>.Upsert(model, ct);
        }

        /// <summary>Replace an existing row only while its stored state matches the guard. False means missing or conflict.</summary>
        public static Task<bool> ReplaceIf(TEntity model, Expression<Func<TEntity, bool>> guard,
            string? partition = null, CancellationToken ct = default)
            => Data<TEntity, TKey>.ReplaceIf(model, guard, partition, ct);

        public static Task<MutationResult<TEntity, TKey>> UpsertWithOutcome(
            TEntity model,
            CancellationToken ct = default)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            return Data<TEntity, TKey>.UpsertWithOutcome(model, ct);
        }

        public static Task<TEntity> Upsert(TEntity model, string partition, CancellationToken ct = default)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(partition)) throw new ArgumentException("Partition must be provided.", nameof(partition));

            return Data<TEntity, TKey>.Upsert(model, partition, ct);
        }

        /// <summary>
        /// Writes only when the caller's model differs from the current stored application value.
        /// Lifecycle participates only when this comparison selects a real Upsert.
        /// </summary>
        public static async Task<bool> UpsertIfChanged(TEntity model, CancellationToken ct = default)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            var prior = await Data<TEntity, TKey>.Get(model.Id, ct).ConfigureAwait(false);
            if (prior is not null && SameValue(prior, model)) return false;
            await Data<TEntity, TKey>.Upsert(model, ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>Partitioned form of <see cref="UpsertIfChanged(TEntity, CancellationToken)"/>.</summary>
        public static async Task<bool> UpsertIfChanged(TEntity model, string partition, CancellationToken ct = default)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (string.IsNullOrWhiteSpace(partition)) throw new ArgumentException("Partition must be provided.", nameof(partition));

            var prior = await Data<TEntity, TKey>.Get(model.Id, partition, ct).ConfigureAwait(false);
            if (prior is not null && SameValue(prior, model)) return false;
            await Data<TEntity, TKey>.Upsert(model, partition, ct).ConfigureAwait(false);
            return true;
        }

        public static Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
        {
            if (models is null) throw new ArgumentNullException(nameof(models));

            return Data<TEntity, TKey>.UpsertMany(models, ct);
        }

        public static Task<int> UpsertMany(IEnumerable<TEntity> models, string partition, CancellationToken ct = default)
        {
            if (models is null) throw new ArgumentNullException(nameof(models));
            if (string.IsNullOrWhiteSpace(partition)) throw new ArgumentException("Partition must be provided.", nameof(partition));

            return Data<TEntity, TKey>.UpsertMany(models, partition, ct);
        }

        // Removal helpers
        public static Task<bool> Remove(TKey id, CancellationToken ct = default)
            => Data<TEntity, TKey>.Delete(id, ct);

        public static Task<MutationResult<TEntity, TKey>> RemoveWithOutcome(
            TKey id,
            CancellationToken ct = default)
            => Data<TEntity, TKey>.DeleteWithOutcome(id, ct);

        public static Task<bool> Remove(TKey id, QueryDefinition? options, CancellationToken ct = default)
        {
            Koan.Data.Abstractions.Filtering.Filter.RequireRowOnly(options?.Filter, "Entity.Remove");
            if (options?.Partition is string partition && !string.IsNullOrWhiteSpace(partition))
            {
                return Remove(id, partition, ct);
            }

            return Remove(id, ct);
        }

        public static Task<int> Remove(IEnumerable<TKey> ids, CancellationToken ct = default)
        {
            if (ids is null) throw new ArgumentNullException(nameof(ids));

            return Data<TEntity, TKey>.DeleteMany(ids, ct);
        }

        public static Task<int> Remove(IEnumerable<TKey> ids, QueryDefinition? options, CancellationToken ct = default)
        {
            Koan.Data.Abstractions.Filtering.Filter.RequireRowOnly(options?.Filter, "Entity.Remove");
            if (options?.Partition is string partition && !string.IsNullOrWhiteSpace(partition))
            {
                return Remove(ids, partition, ct);
            }

            return Remove(ids, ct);
        }

        // Partition-aware removal helpers
        public static Task<bool> Remove(TKey id, string partition, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(partition)) throw new ArgumentException("Partition must be provided.", nameof(partition));

            return Data<TEntity, TKey>.Delete(id, partition, ct);
        }

        public static Task<int> Remove(IEnumerable<TKey> ids, string partition, CancellationToken ct = default)
        {
            if (ids is null) throw new ArgumentNullException(nameof(ids));
            if (string.IsNullOrWhiteSpace(partition)) throw new ArgumentException("Partition must be provided.", nameof(partition));

            return Data<TEntity, TKey>.DeleteMany(ids, partition, ct);
        }

        private static bool SameValue(TEntity left, TEntity right)
        {
            try
            {
                return string.Equals(
                    System.Text.Json.JsonSerializer.Serialize(left, left.GetType()),
                    System.Text.Json.JsonSerializer.Serialize(right, right.GetType()),
                    StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// Remove all entities matching the repository-native <paramref name="query"/>.
        /// Renamed from <c>Remove(string query, …)</c> in v0.9 to break a silent
        /// overload collision: when <c>TKey == string</c>, calls like
        /// <c>Entity.Remove(id, ct)</c> resolved to the non-generic
        /// <c>Remove(string query, ct)</c> branch and shipped the id to the
        /// repository as a query expression, throwing
        /// <c>NotSupportedException: String queries are not supported</c>.
        /// The verb-explicit name keeps the key-based overload unambiguous.
        /// </summary>
        public static async Task<int> RemoveByQuery(string query, CancellationToken ct = default)
        {
            var items = await Data<TEntity, TKey>.Query(query, ct);
            return await Data<TEntity, TKey>.DeleteMany(items.Select(e => e.Id), ct);
        }

        /// <summary>Partitioned variant of <see cref="RemoveByQuery(string, CancellationToken)"/>.</summary>
        public static async Task<int> RemoveByQuery(string query, string partition, CancellationToken ct = default)
        {
            using var _ = Data<TEntity, TKey>.WithPartition(partition);
            var items = await Data<TEntity, TKey>.Query(query, ct);
            return await Data<TEntity, TKey>.DeleteMany(items.Select(e => e.Id), partition, ct);
        }

        /// <summary>
        /// Removes all entities using Optimized strategy.
        /// Uses current EntityContext or default partition.
        /// Configured remove Lifecycle is preserved; otherwise the provider may select its fast path.
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Number of entities removed, or -1 if unknown</returns>
        public static Task<long> RemoveAll(CancellationToken ct = default)
            => RemoveAll(RemoveStrategy.Optimized, ct);

        /// <summary>
        /// Removes all entities using the specified strategy.
        /// Uses current EntityContext or default partition.
        /// </summary>
        /// <param name="strategy">Removal strategy (Safe preserves Lifecycle; Fast explicitly bypasses it)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Number of entities removed, or -1 if unknown (TRUNCATE doesn't report count)</returns>
        public static Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
            => Data<TEntity, TKey>.RemoveAll(strategy, ct);

        /// <summary>
        /// Removes all entities in the specified partition using the given strategy.
        /// </summary>
        /// <param name="strategy">Removal strategy (Safe preserves Lifecycle; Fast explicitly bypasses it)</param>
        /// <param name="partition">Partition name to target</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Number of entities removed, or -1 if unknown</returns>
        public static Task<long> RemoveAll(RemoveStrategy strategy, string partition, CancellationToken ct = default)
            => Data<TEntity, TKey>.RemoveAll(strategy, partition, ct);

        // --- Patch convenience statics (DX sugar, normalize to canonical PatchOps) ---
        // Null → null semantics (application/json partial JSON)
        public static Task<TEntity?> Patch(TKey id, object partial, Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy? nulls = null, CancellationToken ct = default)
        {
            if (partial is null) throw new ArgumentNullException(nameof(partial));
            var ops = new List<Koan.Data.Abstractions.Instructions.PatchOp>();
            // Use Newtonsoft for consistent token walking used elsewhere
            var jt = Newtonsoft.Json.Linq.JToken.FromObject(partial);
            void Walk(Newtonsoft.Json.Linq.JToken token, string basePath)
            {
                if (token is Newtonsoft.Json.Linq.JObject obj)
                {
                    foreach (var p in obj.Properties())
                    {
                        var path = basePath + "/" + p.Name;
                        if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                        {
                            Walk(p.Value, path);
                        }
                        else if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            ops.Add(new Koan.Data.Abstractions.Instructions.PatchOp("replace", path, null, Newtonsoft.Json.Linq.JValue.CreateNull()));
                        }
                        else
                        {
                            ops.Add(new Koan.Data.Abstractions.Instructions.PatchOp("replace", path, null, p.Value.DeepClone()));
                        }
                    }
                }
                else
                {
                    ops.Add(new Koan.Data.Abstractions.Instructions.PatchOp("replace", basePath, null, token.DeepClone()));
                }
            }
            Walk(jt, "");
            ops = ops.Select(o => o with { Path = o.Path.StartsWith('/') ? o.Path : "/" + o.Path.TrimStart('/') }).ToList();
            var options = new Koan.Data.Abstractions.Instructions.PatchOptions(
                Koan.Data.Abstractions.Instructions.MergePatchNullPolicy.SetDefault,
                nulls ?? Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.SetNull,
                Koan.Data.Abstractions.Instructions.ArrayBehavior.Replace);
            var payload = new Koan.Data.Abstractions.Instructions.PatchPayload<TKey>(id, null, null, "partial-json", ops, options);
            return Data<TEntity, TKey>.Patch(payload, ct);
        }

        // Null → default semantics (application/merge-patch+json)
        public static Task<TEntity?> PatchMerge(TKey id, object delta, Koan.Data.Abstractions.Instructions.MergePatchNullPolicy? nulls = null, CancellationToken ct = default)
        {
            if (delta is null) throw new ArgumentNullException(nameof(delta));
            var jt = Newtonsoft.Json.Linq.JToken.FromObject(delta);
            var ops = new List<Koan.Data.Abstractions.Instructions.PatchOp>();
            void Walk(Newtonsoft.Json.Linq.JToken token, string basePath)
            {
                if (token is Newtonsoft.Json.Linq.JObject obj)
                {
                    foreach (var p in obj.Properties())
                    {
                        var path = basePath + "/" + p.Name;
                        if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                        {
                            Walk(p.Value, path);
                        }
                        else if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            ops.Add(new Koan.Data.Abstractions.Instructions.PatchOp("remove", path, null, null));
                        }
                        else
                        {
                            ops.Add(new Koan.Data.Abstractions.Instructions.PatchOp("replace", path, null, p.Value.DeepClone()));
                        }
                    }
                }
                else
                {
                    ops.Add(new Koan.Data.Abstractions.Instructions.PatchOp("replace", basePath, null, token.DeepClone()));
                }
            }
            Walk(jt, "");
            ops = ops.Select(o => o with { Path = o.Path.StartsWith('/') ? o.Path : "/" + o.Path.TrimStart('/') }).ToList();
            var options = new Koan.Data.Abstractions.Instructions.PatchOptions(
                nulls ?? Koan.Data.Abstractions.Instructions.MergePatchNullPolicy.SetDefault,
                Koan.Data.Abstractions.Instructions.PartialJsonNullPolicy.SetNull,
                Koan.Data.Abstractions.Instructions.ArrayBehavior.Replace);
            var payload = new Koan.Data.Abstractions.Instructions.PatchPayload<TKey>(id, null, null, "merge-patch", ops, options);
            return Data<TEntity, TKey>.Patch(payload, ct);
        }

        /// <summary>
        /// Indicates whether the current provider supports fast removal (TRUNCATE/DROP).
        /// </summary>
        public static bool SupportsFastRemove
            => Data<TEntity, TKey>.Capabilities.Has(Koan.Data.Abstractions.Capabilities.DataCaps.Write.FastRemove);

        // Instance self-remove
        public Task<bool> Remove(CancellationToken ct = default)
            => Remove(Id, ct);

        // Instance-based relationship navigation methods

        /// <summary>
        /// Gets the single parent entity for this instance. Only works when the entity has exactly one parent relationship.
        /// Throws InvalidOperationException if the entity has no parents or multiple parents.
        /// </summary>
        public async Task<object> GetParent(CancellationToken ct = default)
        {
            var relationshipService = GetRelationshipService();
            relationshipService.ValidateRelationshipCardinality(typeof(TEntity), "getparent");

            var parentRelationships = relationshipService.GetParentRelationships(typeof(TEntity));
            var (propertyName, parentType) = parentRelationships[0];

            var parentId = GetPropertyValue<TKey>(propertyName);
            if (parentId == null) return null!;

            var result = await LoadParentEntity(parentType, parentId, ct);
            return result ?? throw new InvalidOperationException($"Parent entity not found for {propertyName} = {parentId}");
        }

        /// <summary>
        /// Gets the parent entity of the specified type for this instance.
        /// Validates that exactly one relationship to TParent exists.
        /// </summary>
        public async Task<TParent?> GetParent<TParent>(CancellationToken ct = default)
            where TParent : class, IEntity<TKey>
        {
            var relationshipService = GetRelationshipService();
            var parentRelationships = relationshipService.GetParentRelationships(typeof(TEntity))
                .Where(x => x.ParentType == typeof(TParent))
                .ToList();

            if (parentRelationships.Count == 0)
                throw new InvalidOperationException($"{typeof(TEntity).Name} has no parent relationship to {typeof(TParent).Name}");

            if (parentRelationships.Count > 1)
                throw new InvalidOperationException($"{typeof(TEntity).Name} has multiple parent relationships to {typeof(TParent).Name}. Use GetParent<TParent>(propertyName) instead");

            var (propertyName, _) = parentRelationships[0];
            var parentId = GetPropertyValue<TKey>(propertyName);

            if (parentId == null) return null;

            return await Data<TParent, TKey>.Get(parentId, ct);
        }

        /// <summary>
        /// Gets the parent entity of the specified type using the specified property name.
        /// No validation - allows explicit access to specific parent relationships.
        /// </summary>
        public async Task<TParent?> GetParent<TParent>(string propertyName, CancellationToken ct = default)
            where TParent : class, IEntity<TKey>
        {
            var parentId = GetPropertyValue<TKey>(propertyName);
            if (parentId == null) return null;

            return await Data<TParent, TKey>.Get(parentId, ct);
        }

        /// <summary>
        /// Gets all parent entities for this instance as a dictionary.
        /// Key = property name, Value = parent entity or null.
        /// </summary>
        public async Task<Dictionary<string, object?>> GetParents(CancellationToken ct = default)
        {
            var relationshipService = GetRelationshipService();
            var parentRelationships = relationshipService.GetParentRelationships(typeof(TEntity));
            var parents = new Dictionary<string, object?>();

            foreach (var (propertyName, parentType) in parentRelationships)
            {
                var parentId = GetPropertyValue<TKey>(propertyName);
                if (parentId != null)
                {
                    var parent = await LoadParentEntity(parentType, parentId, ct);
                    parents[propertyName] = parent;
                }
                else
                {
                    parents[propertyName] = null;
                }
            }

            return parents;
        }

        /// <summary>
        /// Gets all children for this entity when it has exactly one child type.
        /// Throws InvalidOperationException if the entity has no children or multiple child types.
        /// </summary>
        public async Task<IReadOnlyList<object>> GetChildren(CancellationToken ct = default)
            => await GetChildren(RelationshipQueryPolicy.Strict, ct);

        /// <summary>Gets the only child relationship using an explicit execution safety policy.</summary>
        public async Task<IReadOnlyList<object>> GetChildren(RelationshipQueryPolicy policy, CancellationToken ct = default)
        {
            var relationshipService = GetRelationshipService();
            relationshipService.ValidateRelationshipCardinality(typeof(TEntity), "getchildren");

            var childTypes = relationshipService.GetAllChildTypes(typeof(TEntity));
            var childType = childTypes[0];

            return await LoadChildEntities(childType, policy, ct);
        }

        /// <summary>
        /// Gets all children of the specified type for this entity.
        /// </summary>
        public async Task<IReadOnlyList<TChild>> GetChildren<TChild>(CancellationToken ct = default)
            where TChild : class, IEntity<TKey>
            => await GetChildren<TChild>(RelationshipQueryPolicy.Strict, ct);

        /// <summary>Gets children of the specified type using an explicit execution safety policy.</summary>
        public async Task<IReadOnlyList<TChild>> GetChildren<TChild>(RelationshipQueryPolicy policy, CancellationToken ct = default)
            where TChild : class, IEntity<TKey>
        {
            var relationshipService = GetRelationshipService();
            var childRelationships = relationshipService.GetChildRelationships(typeof(TEntity))
                .Where(x => x.ChildType == typeof(TChild))
                .ToList();

            if (childRelationships.Count == 0)
                return new List<TChild>().AsReadOnly();

            var allChildren = new List<TChild>();
            foreach (var (referenceProperty, _) in childRelationships)
            {
                var children = await LoadChildrenByProperty<TChild>(referenceProperty, policy, ct);
                allChildren.AddRange(children);
            }

            return allChildren.AsReadOnly();
        }

        /// <summary>
        /// Gets children of the specified type using the specified reference property.
        /// </summary>
        public async Task<IReadOnlyList<TChild>> GetChildren<TChild>(string referenceProperty, CancellationToken ct = default)
            where TChild : class, IEntity<TKey>
            => await GetChildren<TChild>(referenceProperty, RelationshipQueryPolicy.Strict, ct);

        /// <summary>Gets a named child edge using an explicit execution safety policy.</summary>
        public async Task<IReadOnlyList<TChild>> GetChildren<TChild>(string referenceProperty, RelationshipQueryPolicy policy, CancellationToken ct = default)
            where TChild : class, IEntity<TKey>
        {
            return await LoadChildrenByProperty<TChild>(referenceProperty, policy, ct);
        }

        /// <summary>
        /// Gets the full relationship graph for this entity, including all parents and children.
        /// Returns a RelationshipGraph with selective enrichment - only this entity is enriched.
        /// </summary>
        public Task<RelationshipGraph<TEntity>> Relatives(CancellationToken ct = default)
            => Relatives(RelationshipQueryPolicy.Strict, ct);

        /// <summary>
        /// Gets this Entity's direct relationship graph using an explicit execution safety policy for
        /// child edges. The same operation lifts to finite and asynchronous Entity sources.
        /// </summary>
        public async Task<RelationshipGraph<TEntity>> Relatives(RelationshipQueryPolicy policy, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var loader = Koan.Core.Hosting.App.AppHost.GetRequiredService<RelationshipGraphLoader>("relationship graph loading");
            var source = EntityCardinality.One((TEntity)(object)this, ct);
            await foreach (var graph in loader.Load<TEntity, TKey>(source, policy, ct).ConfigureAwait(false))
            {
                return graph;
            }

            throw new InvalidOperationException($"Relationship graph loading returned no result for {typeof(TEntity).Name}.");
        }

        // Helper methods for relationship loading

        private TValue? GetPropertyValue<TValue>(string propertyName)
        {
            var getter = GetPropertyGetter(typeof(TEntity), propertyName);
            var value = getter(this);
            if (value == null) return default(TValue);
            try
            {
                return (TValue)value;
            }
            catch
            {
                return default(TValue);
            }
        }
        // Cached property getter delegates for fast access
        private static readonly Dictionary<(Type, string), Func<object, object?>> PropertyGetterCache = new();
        private static readonly object PropertyGetterLock = new();

        protected static Func<object, object?> GetPropertyGetter(Type type, string propertyName)
        {
            var key = (type, propertyName);
            if (PropertyGetterCache.TryGetValue(key, out var getter))
                return getter;
            lock (PropertyGetterLock)
            {
                if (PropertyGetterCache.TryGetValue(key, out getter))
                    return getter;
                var prop = type.GetProperty(propertyName);
                if (prop == null)
                    throw new ArgumentException($"Property '{propertyName}' not found on type '{type.Name}'");
                var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
                var cast = System.Linq.Expressions.Expression.Convert(param, type);
                var access = System.Linq.Expressions.Expression.Property(cast, prop);
                var convert = System.Linq.Expressions.Expression.Convert(access, typeof(object));
                var lambda = System.Linq.Expressions.Expression.Lambda<Func<object, object?>>(convert, param);
                getter = lambda.Compile();
                PropertyGetterCache[key] = getter;
                return getter;
            }
        }

        private async Task<object?> LoadParentEntity(Type parentType, TKey parentId, CancellationToken ct)
        {
            // Use reflection to call Data<TParent, TKey>.GetAsync
            var dataType = typeof(Data<,>).MakeGenericType(parentType, typeof(TKey));
            var method = dataType.GetMethod("Get", new[] { typeof(TKey), typeof(CancellationToken) });

            if (method == null) return null;

            var task = (Task)method.Invoke(null, new object[] { parentId, ct })!;
            await task;

            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty?.GetValue(task);
        }

        private async Task<IReadOnlyList<object>> LoadChildEntities(Type childType, RelationshipQueryPolicy policy, CancellationToken ct)
        {
            var children = new List<object>();
            var relationshipService = GetRelationshipService();
            var childRelationships = relationshipService.GetChildRelationships(typeof(TEntity))
                .Where(x => x.ChildType == childType);
            foreach (var (referenceProperty, _) in childRelationships)
            {
                children.AddRange(await LoadChildEntitiesByProperty(childType, referenceProperty, policy, ct));
            }
            return children.Distinct().ToList().AsReadOnly();
        }

        private async Task<IReadOnlyList<TChild>> LoadChildrenByProperty<TChild>(
            string referenceProperty,
            RelationshipQueryPolicy policy,
            CancellationToken ct)
            where TChild : class, IEntity<TKey>
        {
            var executor = Koan.Core.Hosting.App.AppHost.GetRequiredService<IRelationshipQueryExecutor>("relationship child loading");
            var result = await executor.LoadChildren<TEntity, TChild, TKey>(
                new[] { Id }, referenceProperty, policy: policy, ct: ct);
            return result.ByParent.TryGetValue(Id, out var children)
                ? children
                : Array.Empty<TChild>();
        }

        private async Task<IReadOnlyList<object>> LoadChildEntitiesByProperty(
            Type childType,
            string referenceProperty,
            RelationshipQueryPolicy policy,
            CancellationToken ct)
        {
            var method = GetType().GetMethod(
                nameof(LoadChildEntitiesByPropertyTyped),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(childType);
            return await (Task<IReadOnlyList<object>>)method.Invoke(this, new object[] { referenceProperty, policy, ct })!;
        }

        private async Task<IReadOnlyList<object>> LoadChildEntitiesByPropertyTyped<TChild>(
            string referenceProperty,
            RelationshipQueryPolicy policy,
            CancellationToken ct)
            where TChild : class, IEntity<TKey>
        {
            var rows = await LoadChildrenByProperty<TChild>(referenceProperty, policy, ct);
            return rows.Cast<object>().ToArray();
        }

        // Hostless metadata inspection needs no runtime owner; its reflection facts are immutable.
        // Hosted calls must resolve the active host's singleton on every invocation so a closed
        // Entity type never retains a service from an earlier, disposed host.
        private static readonly IRelationshipMetadata FallbackRelationshipMetadata = new RelationshipMetadataService();

        public IRelationshipMetadata GetRelationshipService()
        {
            var accessor = EntityMetadataProvider.RelationshipMetadataAccessor;
            var services = Koan.Core.Hosting.App.AppHost.Current;
            if (accessor != null && services != null)
            {
                return accessor(services);
            }

            return FallbackRelationshipMetadata;
        }

    }

    // Convenience for string-keyed entities with automatic GUID v7 generation
    public abstract partial class Entity<TEntity> : Entity<TEntity, string>
        where TEntity : class, Koan.Data.Abstractions.IEntity<string>
    {
        private string? _id;

        /// <summary>
        /// Gets or sets the entity ID. Automatically generates a GUID v7 value on first access if not already set.
        /// This promotes auto-safe ID patterns while allowing explicit override when needed.
        /// </summary>
        public override string Id
        {
            get => _id ??= Guid.CreateVersion7().ToString();
            set => _id = value;
        }
    }
}

public static class EntityMetadataProvider
{
    public static Func<IServiceProvider, IRelationshipMetadata>? RelationshipMetadataAccessor;
}

