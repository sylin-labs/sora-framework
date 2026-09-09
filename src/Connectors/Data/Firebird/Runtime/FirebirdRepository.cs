using System.Collections.Frozen;
using System.Data.Common;
using System.Linq.Expressions;
using FirebirdSql.Data.FirebirdClient;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core;
using Koan.Data.Core.Configuration;
using Koan.Data.Core.Optimization;
using Koan.Data.Core.Options;
using Koan.Data.Core.Readiness;
using Koan.Data.Core.Semantics;
using Koan.Data.Relational;
using Koan.Data.Relational.Ado;
using Koan.Data.Relational.Linq;
using Koan.Data.Relational.Mapping;
using Koan.Data.Relational.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koan.Data.Connector.Firebird.Runtime;

internal sealed class FirebirdRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>, IQueryRepository<TEntity, TKey>, IRawQueryRepository<TEntity, TKey>,
    IDescribesCapabilities, IOptimizedDataRepository<TEntity, TKey>, IBulkUpsert<TKey>, IBulkDelete<TKey>,
    IConditionalWriteRepository<TEntity, TKey>, IInstructionExecutor<TEntity>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IServiceProvider _services;
    private readonly FirebirdRepositoryOptions _options;
    private readonly MappingPlan? _declaredMapping;
    private readonly DataSegmentationPlan _segmentation;
    private readonly DataSourceReadinessCoordinator _readiness;
    private readonly IRelationalSchemaOrchestrator _schema;
    private readonly RelationalSchemaPolicy _schemaPolicy;
    private readonly object _plansGate = new();
    private readonly Dictionary<string, FirebirdEntityPlan<TEntity, TKey>> _plans = new(StringComparer.Ordinal);
    private readonly int _planLimit;

    private FirebirdEntityPlan<TEntity, TKey> Plan => ResolvePlan();

    public FirebirdRepository(IServiceProvider services, FirebirdRepositoryOptions options)
    {
        _services = services;
        _options = options;
        _readiness = services.GetRequiredService<DataSourceReadinessCoordinator>();
        _schema = services.GetRequiredService<IRelationalSchemaOrchestrator>();
        _schemaPolicy = new RelationalSchemaPolicy
        {
            Ddl = options.DdlPolicy,
            Matching = options.SchemaMatching,
            AllowProductionDdl = options.AllowProductionDdl,
            DefaultSchema = options.Database,
            StorageLifecycle = options.SourcePlan.StorageLifecycle,
            Access = options.SourcePlan.Access
        };
        _segmentation = services.GetRequiredService<DataSegmentationPlan>();
        _planLimit = services.GetRequiredService<IOptions<MappingOptions>>().Value.PlanEntries;
        _declaredMapping = services.GetRequiredService<IDataMappingPlans>().Find<TEntity>(options.Source);
        OptimizationInfo = services.GetStorageOptimization<TEntity, TKey>();
    }

    public StorageOptimizationInfo OptimizationInfo { get; }
    public void Describe(ICapabilities capabilities) => FirebirdFeatures.Describe(capabilities);
    public Task EnsureReady(CancellationToken ct = default) => Ready(ct);

    public async Task<TEntity?> Get(TKey id, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        await using var connection = await Open(ct).ConfigureAwait(false);
        return await Get(connection, null, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TEntity?>> GetMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        var requested = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        if (requested.Count == 0) return [];
        await Ready(ct).ConfigureAwait(false);
        var plan = Plan;
        var parameters = new SqlParameters();
        var predicates = new string[requested.Count];
        for (var index = 0; index < requested.Count; index++)
            predicates[index] = IdentityPredicate(plan.Commands.Get(requested[index]).Identity, $"k{index}_", parameters);
        await using var connection = await Open(ct).ConfigureAwait(false);
        var rows = await AdoCommands.QueryRowsAsync(connection, $"SELECT {plan.Select} FROM {plan.QualifiedTable} WHERE {string.Join(" OR ", predicates)}", parameters, null, ct).ConfigureAwait(false);
        var entities = rows.Select(row => Materialize(plan, row)).ToDictionary(static entity => entity.Id);
        return requested.Select(id => entities.TryGetValue(id, out var entity) ? entity : null).ToArray();
    }

    public async Task<TEntity> Upsert(TEntity model, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        _options.SourcePlan.Demand(DataOperationEffect.Write, "upsert");
        await using var connection = await Open(ct).ConfigureAwait(false);
        await Upsert(connection, null, model, ct).ConfigureAwait(false);
        return model;
    }

    public async Task<bool> Delete(TKey id, CancellationToken ct = default) =>
        await DeleteMany([id], ct).ConfigureAwait(false) > 0;

    public async Task<int> UpsertMany(IEnumerable<TEntity> models, CancellationToken ct = default)
    {
        var values = models as IReadOnlyList<TEntity> ?? models.ToArray();
        if (values.Count == 0) return 0;
        await Ready(ct).ConfigureAwait(false);
        _options.SourcePlan.Demand(DataOperationEffect.Write, "bulk upsert");
        await using var connection = await Open(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var model in values) await Upsert(connection, transaction, model, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return values.Count;
    }

    public async Task<int> DeleteMany(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        var values = ids as IReadOnlyList<TKey> ?? ids.ToArray();
        if (values.Count == 0) return 0;
        await Ready(ct).ConfigureAwait(false);
        _options.SourcePlan.Demand(DataOperationEffect.Write, "bulk delete");
        await using var connection = await Open(ct).ConfigureAwait(false);
        return await Delete(connection, null, values, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAll(CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        _options.SourcePlan.Demand(DataOperationEffect.Write, "delete all");
        await using var connection = await Open(ct).ConfigureAwait(false);
        return await AdoCommands.ExecuteAsync(connection, $"DELETE FROM {Plan.QualifiedTable}", null, null, ct).ConfigureAwait(false);
    }

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        if (strategy == RemoveStrategy.Fast)
        {
            _options.SourcePlan.Demand(DataOperationEffect.SchemaOrAdmin, "fast remove");
            // Firebird has no TRUNCATE; a plain DELETE is the non-structural fast path this store offers.
            await using var connection = await Open(ct).ConfigureAwait(false);
            await AdoCommands.ExecuteAsync(connection, $"DELETE FROM {Plan.QualifiedTable}", null, null, ct).ConfigureAwait(false);
            return -1;
        }
        var count = await Count(new QueryDefinition { CountStrategy = CountStrategy.Exact }, ct).ConfigureAwait(false);
        await DeleteAll(ct).ConfigureAwait(false);
        return count.Value;
    }

    public IBatchSet<TEntity, TKey> CreateBatch() => new Batch(this);

    public async Task<RepositoryQueryResult<TEntity>> Query(QueryDefinition query, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        var plan = Plan;
        var (where, parameters) = Where(plan, query.Filter);
        var (order, handledSort) = Order(plan, query.Sort);
        var sortComplete = query.Sort.Count == 0 || handledSort.Count == query.Sort.Count;
        var paged = query.HasPagination && sortComplete;
        // OFFSET/FETCH is the SQL-standard form Firebird speaks, and it only exists after ORDER BY —
        // which is always present here, because an unpaged query still carries the stable identity order.
        var sql = $"SELECT {plan.Select} FROM {plan.QualifiedTable}" +
                  (where is null ? string.Empty : $" WHERE {where}") + $" {order}" +
                  (paged ? $" OFFSET {query.EffectiveOffset()} ROWS FETCH NEXT {query.EffectivePageSize()} ROWS ONLY" : string.Empty);
        await using var connection = await Open(ct).ConfigureAwait(false);
        var rows = await AdoCommands.QueryRowsAsync(connection, sql, Parameters(parameters), null, ct).ConfigureAwait(false);
        var items = rows.Select(row => Materialize(plan, row)).ToArray();
        long? total = null;
        if (query.CountStrategy is not null)
            total = paged ? await CountExact(connection, plan, where, parameters, ct).ConfigureAwait(false) : items.LongLength;
        return new RepositoryQueryResult<TEntity>
        {
            Items = items,
            FilterHandled = query.Filter is not null,
            TotalCount = total,
            CountExecution = query.CountStrategy is null ? CountExecutionKind.None : CountExecutionKind.Exact,
            SortHandled = handledSort,
            PaginationHandled = paged,
            IsEstimate = false
        };
    }

    public async Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        var plan = Plan;
        var (where, parameters) = Where(plan, query.Filter);
        await using var connection = await Open(ct).ConfigureAwait(false);
        return CountResult.Exact(await CountExact(connection, plan, where, parameters, ct).ConfigureAwait(false));
    }

    public async Task<RepositoryQueryResult<TEntity>> QueryRaw(
        string query, object? parameters, QueryDefinition shaping, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        var plan = Plan;
        var paged = shaping.HasPagination;
        var full = query.TrimStart().StartsWith("select ", StringComparison.OrdinalIgnoreCase);
        var sql = full ? query : $"SELECT {plan.Select} FROM {plan.QualifiedTable} WHERE {query} {StableOrder(plan)}";
        if (paged && !full)
            sql += $" OFFSET {shaping.EffectiveOffset()} ROWS FETCH NEXT {shaping.EffectivePageSize()} ROWS ONLY";
        await using var connection = await Open(ct).ConfigureAwait(false);
        var rows = await AdoCommands.QueryRowsAsync(
            connection, sql, SqlParameters.FromObject(parameters), null, ct).ConfigureAwait(false);
        return new RepositoryQueryResult<TEntity>
        {
            Items = rows.Select(row => Materialize(plan, row)).ToArray(),
            PaginationHandled = paged && !full,
            CountExecution = CountExecutionKind.None
        };
    }

    public async Task<CountResult> CountRaw(string query, object? parameters, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        await using var connection = await Open(ct).ConfigureAwait(false);
        var count = await AdoCommands.ExecuteScalarInt64Async(connection, $"SELECT COUNT(1) FROM {Plan.QualifiedTable} WHERE {query}", SqlParameters.FromObject(parameters), null, ct).ConfigureAwait(false);
        return CountResult.Exact(count);
    }

    public async Task<bool> ConditionalReplaceAsync(
        TEntity model, Expression<Func<TEntity, bool>> guard, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        _options.SourcePlan.Demand(DataOperationEffect.Write, "conditional replace");
        var plan = Plan;
        var command = plan.Commands.Update(model);
        var parameters = new SqlParameters();
        var set = UpdateSet(plan, command.Values, parameters, "set_", model);
        var identity = IdentityPredicate(command.Identity, "key_", parameters);
        var (condition, conditionValues) = Where(plan, LinqFilterCompiler.Compile(guard));
        Add(parameters, conditionValues, "p");
        await using var connection = await Open(ct).ConfigureAwait(false);
        return await AdoCommands.ExecuteAsync(connection, $"UPDATE {plan.QualifiedTable} SET {set} WHERE {identity}" +
            (condition is null ? string.Empty : $" AND ({condition})"), parameters, null, ct).ConfigureAwait(false) == 1;
    }

    public async Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
    {
        switch (instruction.Name)
        {
            case DataInstructions.EnsureCreated:
            case RelationalInstructions.SchemaEnsureCreated:
                await EnsureReady(ct).ConfigureAwait(false);
                return (TResult)(object)true;
            case DataInstructions.Clear:
            case RelationalInstructions.SchemaClear:
                return (TResult)(object)await DeleteAll(ct).ConfigureAwait(false);
            case RelationalInstructions.SchemaValidate:
            {
                await Ready(ct).ConfigureAwait(false);
                var validation = await Validation(Plan, ct).ConfigureAwait(false);
                return (TResult)(object)validation.Report(Infrastructure.Constants.Provider);
            }
            case RelationalInstructions.SqlScalar:
            case RelationalInstructions.SqlNonQuery:
            case RelationalInstructions.SqlQuery:
                return await ExecuteSql<TResult>(instruction, ct).ConfigureAwait(false);
            default:
                throw new NotSupportedException(
                    $"Instruction '{instruction.Name}' is not supported by Firebird for {typeof(TEntity).Name}.");
        }
    }

    private FirebirdEntityPlan<TEntity, TKey> ResolvePlan()
    {
        // A declared map names one physical container. An ambient partition asks for a different one, and
        // this store has no way to honour both — so it says so rather than serving the pinned container
        // and leaving the caller to believe the partition took effect.
        if (_declaredMapping is not null && !string.IsNullOrWhiteSpace(EntityContext.Current?.Partition))
            throw new NotSupportedException(
                $"Explicit Firebird map '{_declaredMapping.Id}' pins one physical container and cannot accept an ambient partition.");
        var table = _declaredMapping?.Container.Name ?? AdapterNaming.GetOrCompute<TEntity, TKey>(_services);
        var key = _declaredMapping?.Id ?? table;
        lock (_plansGate)
        {
            if (_plans.TryGetValue(key, out var existing)) return existing;
            if (_plans.Count >= _planLimit)
                throw new InvalidOperationException($"The Firebird repository reached its configured mapping-plan limit of {_planLimit}.");
            var mapping = _declaredMapping ?? RelationalManagedMapping.Compile<TEntity>(
                _options.Source, StorageAddress.From(table));
            var created = new FirebirdEntityPlan<TEntity, TKey>(mapping, _segmentation);
            _plans.Add(key, created);
            return created;
        }
    }

    private async Task Ready(CancellationToken ct)
    {
        var plan = Plan;
        var target = plan.Target;
        if (_options.SourcePlan.UsesLegacyProvisioningReadiness)
        {
            await _readiness.Provision(_options.SourcePlan, target,
                token => Provision(plan, token),
                token => Validate(plan, token), ct).ConfigureAwait(false);
            return;
        }
        await _readiness.ValidateShape(_options.SourcePlan, target, token => Validate(plan, token), ct)
            .ConfigureAwait(false);
    }

    private Task Provision(FirebirdEntityPlan<TEntity, TKey> plan, CancellationToken ct) => _schema.EnsureCreatedAsync(
        plan.Mapping,
        FirebirdDdlExecutor.For(_options.ConnectionString, plan.ShadowColumns),
        FirebirdStoreFeatures.Instance,
        _schemaPolicy,
        ct);

    private async Task Validate(FirebirdEntityPlan<TEntity, TKey> plan, CancellationToken ct)
    {
        var validation = await Validation(plan, ct).ConfigureAwait(false);
        if (validation.IsServiceable) return;
        throw new SchemaMismatchException(
            typeof(TEntity).FullName ?? typeof(TEntity).Name,
            validation.Plan.Table,
            _options.SchemaMatching,
            validation.Corrective,
            _options.SourcePlan.UsesLegacyProvisioningReadiness);
    }

    private Task<RelationalSchemaValidation> Validation(
        FirebirdEntityPlan<TEntity, TKey> plan, CancellationToken ct) => _schema.ValidateAsync(
        plan.Mapping,
        FirebirdDdlExecutor.For(_options.ConnectionString, plan.ShadowColumns),
        FirebirdStoreFeatures.Instance,
        _schemaPolicy,
        ct);

    private async Task<TEntity?> Get(FbConnection connection, FbTransaction? transaction, TKey id, CancellationToken ct)
    {
        var plan = Plan;
        var parameters = new SqlParameters();
        var predicate = IdentityPredicate(plan.Commands.Get(id).Identity, "key_", parameters);
        var rows = await AdoCommands.QueryRowsAsync(
            connection,
            $"SELECT {plan.Select} FROM {plan.QualifiedTable} WHERE {predicate} ROWS 1",
            parameters, transaction, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : Materialize(plan, rows[0]);
    }

    private async Task Upsert(FbConnection connection, FbTransaction? transaction, TEntity model, CancellationToken ct)
    {
        var plan = Plan;
        var generated = plan.Mapping.Identity.IsGenerated && EqualityComparer<TKey>.Default.Equals(model.Id, default!);
        var command = generated ? plan.Commands.Insert(model) : plan.Commands.Update(model);
        var insert = Insert(plan, command, model);
        if (generated)
        {
            // One statement hands the identity back; Firebird answers RETURNING for singleton writes.
            var identityColumn = FirebirdDialect.Quote(plan.IdentityRoots[0]);
            var row = await AdoCommands.QueryRowsAsync(
                connection, $"{insert.Sql} RETURNING {identityColumn}", insert.Parameters, transaction, ct).ConfigureAwait(false);
            if (row.Count == 0)
                throw new InvalidOperationException("The Firebird upsert with generated identity returned no identity.");
            plan.AssignGenerated(model, row[0][plan.IdentityRoots[0]]);
            return;
        }

        if (ManagedFieldWriteScope.Current is { Count: > 0 })
        {
            if (transaction is not null)
            {
                await UpsertScoped(connection, transaction, plan, command, insert, model, ct).ConfigureAwait(false);
                return;
            }
            await using var local = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await UpsertScoped(connection, (FbTransaction)local, plan, command, insert, model, ct).ConfigureAwait(false);
            await local.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        await AdoCommands.ExecuteAsync(connection, insert.Sql, insert.Parameters, transaction, ct).ConfigureAwait(false);
    }

    private static async Task UpsertScoped(
        FbConnection connection,
        FbTransaction transaction,
        FirebirdEntityPlan<TEntity, TKey> plan,
        RelationalCommandPlan command,
        PreparedWrite insert,
        TEntity model,
        CancellationToken ct)
    {
        var parameters = insert.Parameters;
        var set = UpdateSet(plan, command.Values, parameters, "update_", model);
        var identity = IdentityPredicate(command.Identity, "key_", parameters);
        var managed = ManagedGuard(plan, parameters);
        var scoped = $"{identity} AND {managed}";
        if (set.Length > 0)
            await AdoCommands.ExecuteAsync(connection, $"UPDATE {plan.QualifiedTable} SET {set} WHERE {scoped}", parameters, transaction, ct).ConfigureAwait(false);
        if (await Exists(connection, transaction, plan.QualifiedTable, scoped, parameters, ct).ConfigureAwait(false)) return;
        if (await Exists(connection, transaction, plan.QualifiedTable, identity, parameters, ct).ConfigureAwait(false))
            throw new InvalidOperationException("The write was rejected as a cross-scope write.");
        try
        {
            await AdoCommands.ExecuteAsync(connection, insert.Sql, parameters, transaction, ct).ConfigureAwait(false);
        }
        catch (FbException error) when (IsDuplicateKey(error))
        {
            if (await Exists(connection, transaction, plan.QualifiedTable, scoped, parameters, ct).ConfigureAwait(false))
            {
                if (set.Length > 0)
                    await AdoCommands.ExecuteAsync(connection, $"UPDATE {plan.QualifiedTable} SET {set} WHERE {scoped}", parameters, transaction, ct).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("The write was rejected as a cross-scope write.", error);
        }
    }

    private static async Task<bool> Exists(
        FbConnection connection,
        FbTransaction transaction,
        string table,
        string predicate,
        SqlParameters parameters,
        CancellationToken ct) =>
        await AdoCommands.ExecuteScalarInt64Async(
            connection,
            $"SELECT 1 FROM {table} WHERE {predicate} FOR UPDATE WITH LOCK",
            parameters, transaction, ct).ConfigureAwait(false) == 1;

    private static bool IsDuplicateKey(FbException error) =>
        error.ErrorCode == 335544665; // isc_no_dup — a PRIMARY or UNIQUE KEY violation.

    private static PreparedWrite Insert(FirebirdEntityPlan<TEntity, TKey> plan, RelationalCommandPlan command, TEntity model)
    {
        var groups = command.Identity.Concat(command.Values)
            .GroupBy(static value => value.Binding.PhysicalPath.Name, StringComparer.Ordinal).ToArray();
        var parameters = new SqlParameters();
        var columns = new List<string>(groups.Length + 4);
        var values = new List<string>(groups.Length + 4);
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index].ToArray();
            var name = $"insert_{index}";
            columns.Add(FirebirdDialect.Quote(group[0].Binding.PhysicalPath.Name));
            parameters.Add(name, group.Any(static value => value.Binding.PhysicalPath.IsNested)
                ? plan.NestedRoot(group)
                : plan.Parameter(group[0]));
            values.Add($"@{name}");
        }
        // The document carries the whole entity; its top-level scalars mirror into their shadow
        // columns from the same bindings the reads resolve through, so a filter comparand, the
        // column and the document are always the same bytes.
        foreach (var (shadowName, shadowValue) in plan.ShadowValues(model))
        {
            var shadow = $"shadow_{columns.Count}";
            columns.Add(FirebirdDialect.Quote(shadowName));
            parameters.Add(shadow, shadowValue);
            values.Add($"@{shadow}");
        }
        // The managed discriminators ride their shadow columns on this store; the document they mirror
        // is stamped with the same values by the root binding's serializer.
        if (ManagedFieldWriteScope.Effective is { Count: > 0 } managed)
        {
            var shadow = 0;
            foreach (var pair in managed)
            {
                var name = $"managed_shadow_{shadow++}";
                columns.Add(FirebirdDialect.Quote(pair.Key));
                parameters.Add(name, ComparableScalarEncoding.EncodeComparand(pair.Value));
                values.Add($"@{name}");
            }
        }

        // Firebird's one-statement upsert. A generated-key insert carries no identity in the plan, so
        // it is a plain INSERT; everything else matches on the identity and updates the listed columns
        // — including the shadow mirrors — in one atomic statement.
        var matching = command.Identity
            .Select(static value => FirebirdDialect.Quote(value.Binding.PhysicalPath.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sql = matching.Length == 0
            ? $"INSERT INTO {plan.QualifiedTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})"
            : $"UPDATE OR INSERT INTO {plan.QualifiedTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)}) " +
              $"MATCHING ({string.Join(", ", matching)})";
        return new PreparedWrite(sql, parameters);
    }

    private static string UpdateSet(
        FirebirdEntityPlan<TEntity, TKey> plan,
        IReadOnlyList<RelationalValue> values,
        SqlParameters parameters,
        string prefix,
        TEntity model)
    {
        var assignments = new List<string>();
        var parameterIndex = 0;
        foreach (var group in values.GroupBy(static value => value.Binding.PhysicalPath.Name, StringComparer.Ordinal))
        {
            var root = FirebirdDialect.Quote(group.Key);
            var nested = group.Where(static value => value.Binding.PhysicalPath.IsNested).ToArray();
            if (nested.Length == 0)
            {
                var value = group.Single();
                var name = $"{prefix}{parameterIndex++}";
                parameters.Add(name, plan.Parameter(value));
                assignments.Add($"{root} = @{name}");
                continue;
            }
            // No JSON functions means no merge-patch: the write replaces the stored document root with
            // the serialized whole the write plan carries.
            var name2 = $"{prefix}{parameterIndex++}";
            parameters.Add(name2, plan.NestedRoot(group));
            assignments.Add($"{root} = @{name2}");
        }
        // The document mirror: the top-level scalars ride their shadow columns from the same bindings
        // the reads resolve through, so filters, sorts and the document stay byte-identical.
        foreach (var (shadowName, shadowValue) in plan.ShadowValues(model))
        {
            var shadow = $"{prefix}shadow_{parameterIndex++}";
            parameters.Add(shadow, shadowValue);
            assignments.Add($"{FirebirdDialect.Quote(shadowName)} = @{shadow}");
        }
        return string.Join(", ", assignments);
    }

    private static string ManagedGuard(FirebirdEntityPlan<TEntity, TKey> plan, SqlParameters parameters)
    {
        if (ManagedFieldWriteScope.Current is not { Count: > 0 } managed) return string.Empty;
        var guards = new List<string>(managed.Count);
        var index = 0;
        foreach (var pair in managed)
        {
            var name = $"managed_{index++}";
            parameters.Add(name, ComparableScalarEncoding.EncodeComparand(pair.Value));
            guards.Add($"{FirebirdDialect.Quote(pair.Key)} = @{name}");
        }
        return string.Join(" AND ", guards);
    }

    private async Task<int> Delete(
        FbConnection connection, FbTransaction? transaction, IReadOnlyList<TKey> ids, CancellationToken ct)
    {
        var plan = Plan;
        var parameters = new SqlParameters();
        var predicates = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
            predicates[index] = IdentityPredicate(plan.Commands.Delete(ids[index]).Identity, $"d{index}_", parameters);
        var predicate = "(" + string.Join(" OR ", predicates) + ")";
        var managed = ManagedGuard(plan, parameters);
        if (managed.Length > 0) predicate += $" AND ({managed})";
        return await AdoCommands.ExecuteAsync(connection, $"DELETE FROM {plan.QualifiedTable} WHERE {predicate}", parameters, transaction, ct).ConfigureAwait(false);
    }

    private static (string? Sql, IReadOnlyList<object?> Parameters) Where(
        FirebirdEntityPlan<TEntity, TKey> plan, Filter? filter)
    {
        if (filter is null) return (null, []);
        // Managed discriminators resolve through the plan to their shadow columns: the plan's
        // managed path is nested under the document root, and the dialect answers a single-segment
        // read with the plain shadow column.
        var translated = new SqlFilterTranslator(plan.Dialect, plan.Mapping, plan.ManagedPath).Translate(filter);
        return (translated.whereSql, translated.parameters);
    }

    private static (string Sql, IReadOnlySet<SortSpec> Handled) Order(
        FirebirdEntityPlan<TEntity, TKey> plan, IReadOnlyList<SortSpec> sort)
    {
        if (sort.Count == 0) return (StableOrder(plan), RepositoryQueryResult<TEntity>.NoSortHandled);
        var clauses = new List<string>(sort.Count);
        var handled = new List<SortSpec>(sort.Count);
        foreach (var item in sort)
        {
            // An order key that reaches through a collection has no lowering here (no JSON functions),
            // so RelationalCollectionOrder answers null and the framework's sorter owns the term.
            if (item.Path.TraversesCollection || item.Aggregation != SortAggregation.None)
            {
                var term = RelationalCollectionOrder.Term(plan.Dialect, plan.Mapping, item);
                if (term is null) continue;
                clauses.Add(term);
                handled.Add(item);
                continue;
            }

            try
            {
                var use = plan.Mapping.Use(
                    MappingPath.Of(item.Path.Members.Select(static member => member.Name).ToArray()),
                    MappingConsumer.Order);
                var binding = use.Bindings.Single();
                // The framework's sorter puts NULL first ascending and last descending; the placement is
                // spelled out because this store must not be trusted to share the assumption.
                clauses.Add($"{RelationalEnumOrder.Read(plan.Dialect, binding)} " +
                            (item.Desc ? "DESC NULLS LAST" : "ASC NULLS FIRST"));
                handled.Add(item);
            }
            catch (MappingValueException) { }
        }
        return clauses.Count == 0
            ? (StableOrder(plan), RepositoryQueryResult<TEntity>.NoSortHandled)
            : ("ORDER BY " + string.Join(", ", clauses), handled.ToFrozenSet());
    }

    private static string StableOrder(FirebirdEntityPlan<TEntity, TKey> plan) =>
        "ORDER BY " + string.Join(", ", plan.IdentityRoots.Select(FirebirdDialect.Quote));

    private static async Task<long> CountExact(
        FbConnection connection,
        FirebirdEntityPlan<TEntity, TKey> plan,
        string? where,
        IReadOnlyList<object?> parameters,
        CancellationToken ct) =>
        await AdoCommands.ExecuteScalarInt64Async(connection, $"SELECT COUNT(1) FROM {plan.QualifiedTable}" + (where is null ? string.Empty : $" WHERE {where}"), Parameters(parameters), null, ct).ConfigureAwait(false);

    private static string IdentityPredicate(
        IEnumerable<RelationalValue> identity, string prefix, SqlParameters parameters)
    {
        var clauses = new List<string>();
        var index = 0;
        foreach (var value in identity)
        {
            var name = $"{prefix}{index++}";
            parameters.Add(name, value.Value);
            clauses.Add($"{FirebirdDialect.Quote(value.Binding.PhysicalPath.Name)} = @{name}");
        }
        if (clauses.Count == 0) throw new InvalidOperationException("A relational identity predicate cannot be empty.");
        return "(" + string.Join(" AND ", clauses) + ")";
    }

    private static TEntity Materialize(
        FirebirdEntityPlan<TEntity, TKey> plan, IReadOnlyDictionary<string, object?> row) => plan.Hydrate(row);

    private static SqlParameters Parameters(IReadOnlyList<object?> values)
    {
        var parameters = new SqlParameters();
        Add(parameters, values, "p");
        return parameters;
    }

    private static void Add(SqlParameters parameters, IReadOnlyList<object?> values, string prefix)
    {
        for (var index = 0; index < values.Count; index++) parameters.Add($"{prefix}{index}", values[index]);
    }

    private async Task<FbConnection> Open(CancellationToken ct)
    {
        var connection = new FbConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<TResult> ExecuteSql<TResult>(Instruction instruction, CancellationToken ct)
    {
        var sql = InstructionSql(instruction);
        var parameters = instruction.Parameters is null
            ? null
            : SqlParameters.FromDictionary(instruction.Parameters);
        await using var connection = await Open(ct).ConfigureAwait(false);
        if (instruction.Name == RelationalInstructions.SqlScalar)
        {
            var value = await AdoCommands.ExecuteScalarAsync(connection, sql, parameters, null, ct).ConfigureAwait(false);
            if (value is null or DBNull) return default!;
            return (TResult)Convert.ChangeType(value, typeof(TResult), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (instruction.Name == RelationalInstructions.SqlNonQuery)
            return (TResult)(object)await AdoCommands.ExecuteAsync(connection, sql, parameters, null, ct).ConfigureAwait(false);
        var rows = await AdoCommands.QueryRowsAsync(connection, sql, parameters, null, ct).ConfigureAwait(false);
        return (TResult)(object)rows.Select(row =>
            ((IDictionary<string, object>)row).ToDictionary(
                static pair => pair.Key, static pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static string InstructionSql(Instruction instruction)
    {
        if (instruction.Payload is string text && !string.IsNullOrWhiteSpace(text)) return text;
        var property = instruction.Payload?.GetType().GetProperty("Sql") ?? instruction.Payload?.GetType().GetProperty("sql");
        if (property?.GetValue(instruction.Payload) is string sql && !string.IsNullOrWhiteSpace(sql)) return sql;
        if (instruction.Parameters?.TryGetValue("sql", out var value) == true && value is string parameterSql) return parameterSql;
        throw new ArgumentException("Instruction payload is missing Sql.", nameof(instruction));
    }

    private sealed record PreparedWrite(string Sql, SqlParameters Parameters);

    private sealed class Batch(FirebirdRepository<TEntity, TKey> repository) : IBatchSet<TEntity, TKey>
    {
        private readonly List<TEntity> _adds = [];
        private readonly List<TEntity> _updates = [];
        private readonly List<(TKey Id, Action<TEntity> Mutate)> _mutations = [];
        private readonly List<TKey> _deletes = [];

        public BatchExecutionCapabilities ExecutionCapabilities => BatchExecutionCapabilities.Atomic;
        public IBatchSet<TEntity, TKey> Add(TEntity entity) { _adds.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TEntity entity) { _updates.Add(entity); return this; }
        public IBatchSet<TEntity, TKey> Update(TKey id, Action<TEntity> mutate) { _mutations.Add((id, mutate)); return this; }
        public IBatchSet<TEntity, TKey> Delete(TKey id) { _deletes.Add(id); return this; }
        public IBatchSet<TEntity, TKey> Clear() { _adds.Clear(); _updates.Clear(); _mutations.Clear(); _deletes.Clear(); return this; }

        public async Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            await repository.Ready(ct).ConfigureAwait(false);
            repository._options.SourcePlan.Demand(DataOperationEffect.Write, "batch");
            await using var connection = await repository.Open(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var mutation in _mutations)
            {
                var current = await repository.Get(connection, transaction, mutation.Id, ct).ConfigureAwait(false);
                if (current is null) continue;
                mutation.Mutate(current);
                _updates.Add(current);
            }
            foreach (var entity in _adds.Concat(_updates))
                await repository.Upsert(connection, transaction, entity, ct).ConfigureAwait(false);
            var deleted = _deletes.Count == 0
                ? 0
                : await repository.Delete(connection, transaction, _deletes, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new BatchResult(_adds.Count, _updates.Count, deleted) { Atomicity = BatchAtomicity.Atomic };
        }
    }
}
