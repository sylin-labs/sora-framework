using System.Collections.Frozen;
using System.Linq.Expressions;
using Koan.Data.Relational.Ado;
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
using Koan.Data.Core.Optimization;
using Koan.Data.Core.Options;
using Koan.Data.Core.Readiness;
using Koan.Data.Core.Semantics;
using Koan.Data.Relational;
using Koan.Data.Relational.Linq;
using Koan.Data.Relational.Mapping;
using Koan.Data.Relational.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Koan.Data.Connector.MySql.Runtime;

internal sealed class MySqlRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>, IQueryRepository<TEntity, TKey>, IRawQueryRepository<TEntity, TKey>,
    IDescribesCapabilities, IOptimizedDataRepository<TEntity, TKey>, IBulkUpsert<TKey>, IBulkDelete<TKey>,
    IConditionalWriteRepository<TEntity, TKey>, IInstructionExecutor<TEntity>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IServiceProvider _services;
    private readonly MySqlRepositoryOptions _options;
    private readonly MappingPlan? _declaredMapping;
    private readonly DataSegmentationPlan _segmentation;
    private readonly DataSourceReadinessCoordinator _readiness;
    private readonly IRelationalSchemaOrchestrator _schema;
    private readonly RelationalSchemaPolicy _schemaPolicy;
    private readonly object _plansGate = new();
    private readonly Dictionary<string, MySqlEntityPlan<TEntity, TKey>> _plans = new(StringComparer.Ordinal);
    private readonly int _planLimit;
    private readonly bool _conditionalReplace;

    private MySqlEntityPlan<TEntity, TKey> Plan => ResolvePlan();

    public MySqlRepository(IServiceProvider services, MySqlRepositoryOptions options)
    {
        _services = services;
        _options = options;
        _conditionalReplace = !new MySqlConnectionStringBuilder(options.ConnectionString).UseAffectedRows;
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
    public void Describe(ICapabilities capabilities) => MySqlFeatures.Describe(capabilities, _conditionalReplace);
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
            await using var connection = await Open(ct).ConfigureAwait(false);
            await AdoCommands.ExecuteAsync(connection, $"TRUNCATE TABLE {Plan.QualifiedTable}", null, null, ct).ConfigureAwait(false);
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
        var sql = $"SELECT {plan.Select} FROM {plan.QualifiedTable}" +
                  (where is null ? string.Empty : $" WHERE {where}") + $" {order}" +
                  (paged ? $" LIMIT {query.EffectivePageSize()} OFFSET {query.EffectiveOffset()}" : string.Empty);
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
            sql += $" LIMIT {shaping.EffectivePageSize()} OFFSET {shaping.EffectiveOffset()}";
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
        TEntity model, Filter guard, CancellationToken ct = default)
    {
        if (!_conditionalReplace)
            throw new NotSupportedException("MySQL conditional replacement requires UseAffectedRows=false so a matching unchanged row reports success.");
        await Ready(ct).ConfigureAwait(false);
        _options.SourcePlan.Demand(DataOperationEffect.Write, "conditional replace");
        var plan = Plan;
        var command = plan.Commands.Update(model);
        var parameters = new SqlParameters();
        var set = UpdateSet(plan, command.Values, parameters, "set_");
        var identity = IdentityPredicate(command.Identity, "key_", parameters);
        var (condition, conditionValues) = Where(plan, guard);
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
                await using var connection = await Open(ct).ConfigureAwait(false);
                var validation = await Validation(connection, Plan, ct).ConfigureAwait(false);
                return (TResult)(object)validation.Report(Infrastructure.Constants.Provider);
            }
            case RelationalInstructions.SqlScalar:
            case RelationalInstructions.SqlNonQuery:
            case RelationalInstructions.SqlQuery:
                return await ExecuteSql<TResult>(instruction, ct).ConfigureAwait(false);
            default:
                throw new NotSupportedException(
                    $"Instruction '{instruction.Name}' is not supported by MySQL for {typeof(TEntity).Name}.");
        }
    }

    private MySqlEntityPlan<TEntity, TKey> ResolvePlan()
    {
        // A declared map names one physical container. An ambient partition asks for a different one, and this
        // store has no way to honour both — so it says so rather than serving the pinned container and leaving
        // the caller to believe the partition took effect. SQLite and Redis have always refused this; these three
        // read the pinned table instead, which is the same request answered with silence.
        if (_declaredMapping is not null && !string.IsNullOrWhiteSpace(EntityContext.Current?.Partition))
            throw new NotSupportedException(
                $"Explicit MySQL map '{_declaredMapping.Id}' pins one physical container and cannot accept an ambient partition.");
        var table = _declaredMapping?.Container.Name ?? Core.Configuration.AdapterNaming.GetOrCompute<TEntity, TKey>(_services);
        var key = _declaredMapping?.Id ?? $"{_options.Database}/{table}";
        lock (_plansGate)
        {
            if (_plans.TryGetValue(key, out var existing)) return existing;
            if (_plans.Count >= _planLimit)
                throw new InvalidOperationException($"The MySQL repository reached its configured mapping-plan limit of {_planLimit}.");
            var mapping = _declaredMapping ?? RelationalManagedMapping.Compile<TEntity>(
                _options.Source, StorageAddress.From(_options.Database, table));
            var created = new MySqlEntityPlan<TEntity, TKey>(mapping, _options, _segmentation);
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

    private async Task Provision(MySqlEntityPlan<TEntity, TKey> plan, CancellationToken ct)
    {
        await using var connection = await OpenOrCreate(ct).ConfigureAwait(false);
        await _schema.EnsureCreatedAsync(
            plan.Mapping,
            new MySqlDdlExecutor(connection, plan.Dialect),
            MySqlStoreFeatures.Instance,
            _schemaPolicy,
            ct).ConfigureAwait(false);
    }

    private async Task Validate(MySqlEntityPlan<TEntity, TKey> plan, CancellationToken ct)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        var validation = await Validation(connection, plan, ct).ConfigureAwait(false);
        if (validation.IsServiceable) return;
        throw new SchemaMismatchException(
            typeof(TEntity).FullName ?? typeof(TEntity).Name,
            validation.Plan.Table,
            _options.SchemaMatching,
            validation.Corrective,
            _options.SourcePlan.UsesLegacyProvisioningReadiness);
    }

    private Task<RelationalSchemaValidation> Validation(
        MySqlConnection connection,
        MySqlEntityPlan<TEntity, TKey> plan,
        CancellationToken ct) => _schema.ValidateAsync(
        plan.Mapping,
        new MySqlDdlExecutor(connection, plan.Dialect),
        MySqlStoreFeatures.Instance,
        _schemaPolicy,
        ct);

    private async Task<TEntity?> Get(MySqlConnection connection, MySqlTransaction? transaction, TKey id, CancellationToken ct)
    {
        var plan = Plan;
        var parameters = new SqlParameters();
        var predicate = IdentityPredicate(plan.Commands.Get(id).Identity, "key_", parameters);
        var rows = await AdoCommands.QueryRowsAsync(
            connection,
            $"SELECT {plan.Select} FROM {plan.QualifiedTable} WHERE {predicate} LIMIT 1",
            parameters, transaction, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : Materialize(plan, rows[0]);
    }

    private async Task Upsert(MySqlConnection connection, MySqlTransaction? transaction, TEntity model, CancellationToken ct)
    {
        var plan = Plan;
        var generated = plan.Mapping.Identity.IsGenerated && EqualityComparer<TKey>.Default.Equals(model.Id, default!);
        var command = generated ? plan.Commands.Insert(model) : plan.Commands.Update(model);
        var insert = Insert(plan, command);
        if (generated)
        {
            await AdoCommands.ExecuteAsync(connection, insert.Sql, insert.Parameters, transaction, ct).ConfigureAwait(false);
            var key = await AdoCommands.ExecuteScalarAsync(connection, "SELECT LAST_INSERT_ID()", null, transaction, ct).ConfigureAwait(false);
            plan.AssignGenerated(model, key);
            return;
        }

        if (ManagedFieldWriteScope.Current is { Count: > 0 })
        {
            if (transaction is not null)
            {
                await UpsertScoped(connection, transaction, plan, command, insert, ct).ConfigureAwait(false);
                return;
            }
            await using var local = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await UpsertScoped(connection, local, plan, command, insert, ct).ConfigureAwait(false);
            await local.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        var parameters = insert.Parameters;
        var assignments = UpdateSet(plan, command.Values, parameters, "update_");
        if (assignments.Length == 0)
        {
            var identity = MySqlDialect.Quote(plan.IdentityRoots[0]);
            assignments = $"{identity} = {identity}";
        }
        await AdoCommands.ExecuteAsync(connection, $"{insert.Sql} ON DUPLICATE KEY UPDATE {assignments}", parameters, transaction, ct).ConfigureAwait(false);
    }

    private static async Task UpsertScoped(
        MySqlConnection connection,
        MySqlTransaction transaction,
        MySqlEntityPlan<TEntity, TKey> plan,
        RelationalCommandPlan command,
        PreparedWrite insert,
        CancellationToken ct)
    {
        var parameters = insert.Parameters;
        var set = UpdateSet(plan, command.Values, parameters, "update_");
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
        catch (MySqlException error) when (error.Number == 1062)
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
        MySqlConnection connection,
        MySqlTransaction transaction,
        string table,
        string predicate,
        SqlParameters parameters,
        CancellationToken ct) =>
        await AdoCommands.ExecuteScalarInt64Async(
            connection,
            $"SELECT 1 FROM {table} WHERE {predicate} LIMIT 1 FOR UPDATE",
            parameters, transaction, ct).ConfigureAwait(false) == 1;

    private static PreparedWrite Insert(MySqlEntityPlan<TEntity, TKey> plan, RelationalCommandPlan command)
    {
        var groups = command.Identity.Concat(command.Values)
            .GroupBy(static value => value.Binding.PhysicalPath.Name, StringComparer.Ordinal).ToArray();
        var parameters = new SqlParameters();
        var columns = new List<string>(groups.Length);
        var values = new List<string>(groups.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index].ToArray();
            var name = $"insert_{index}";
            columns.Add(MySqlDialect.Quote(group[0].Binding.PhysicalPath.Name));
            parameters.Add(name, group.Any(static value => value.Binding.PhysicalPath.IsNested)
                ? plan.NestedRoot(group)
                : plan.Parameter(group[0]));
            values.Add($"@{name}");
        }
        return new PreparedWrite(
            $"INSERT INTO {plan.QualifiedTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})",
            parameters);
    }

    private static string UpdateSet(
        MySqlEntityPlan<TEntity, TKey> plan,
        IReadOnlyList<RelationalValue> values,
        SqlParameters parameters,
        string prefix)
    {
        var assignments = new List<string>();
        var parameterIndex = 0;
        foreach (var group in values.GroupBy(static value => value.Binding.PhysicalPath.Name, StringComparer.Ordinal))
        {
            var root = MySqlDialect.Quote(group.Key);
            var nested = group.Where(static value => value.Binding.PhysicalPath.IsNested).ToArray();
            if (nested.Length == 0)
            {
                var value = group.Single();
                var name = $"{prefix}{parameterIndex++}";
                parameters.Add(name, plan.Parameter(value));
                assignments.Add($"{root} = @{name}");
                continue;
            }
            var expression = $"COALESCE({root}, JSON_OBJECT())";
            foreach (var value in nested)
            {
                var name = $"{prefix}{parameterIndex++}";
                parameters.Add(name, plan.JsonParameter(value));
                var path = MySqlDialect.JsonPath(value.Binding.PhysicalPath.Segments);
                expression = $"JSON_SET({expression}, '{path}', CAST(@{name} AS JSON))";
            }
            assignments.Add($"{root} = {expression}");
        }
        return string.Join(", ", assignments);
    }

    private static string ManagedGuard(MySqlEntityPlan<TEntity, TKey> plan, SqlParameters parameters)
    {
        if (ManagedFieldWriteScope.Current is not { Count: > 0 } managed) return string.Empty;
        var guards = new List<string>(managed.Count);
        var index = 0;
        foreach (var pair in managed)
        {
            var name = $"managed_{index++}";
            parameters.Add(name, ComparableScalarEncoding.EncodeComparand(pair.Value));
            guards.Add($"{plan.ManagedPath(pair.Key, pair.Value?.GetType() ?? typeof(string))} = @{name}");
        }
        return string.Join(" AND ", guards);
    }

    private async Task<int> Delete(
        MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<TKey> ids, CancellationToken ct)
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
        MySqlEntityPlan<TEntity, TKey> plan, Filter? filter)
    {
        if (filter is null) return (null, []);
        var translated = new SqlFilterTranslator(plan.Dialect, plan.Mapping, plan.ManagedPath).Translate(filter);
        var sql = translated.whereSql.Replace(" ESCAPE '\\'", " ESCAPE '\\\\'", StringComparison.Ordinal);
        return (sql, translated.parameters);
    }

    private static (string Sql, IReadOnlySet<SortSpec> Handled) Order(
        MySqlEntityPlan<TEntity, TKey> plan, IReadOnlyList<SortSpec> sort)
    {
        if (sort.Count == 0) return (StableOrder(plan), RepositoryQueryResult<TEntity>.NoSortHandled);
        var clauses = new List<string>(sort.Count);
        var handled = new List<SortSpec>(sort.Count);
        foreach (var item in sort)
        {
            // An order key that reaches through a collection is an aggregate over a nested array, so it has
            // no binding of its own; the dialect expresses it directly instead of the framework sorting the
            // whole result in memory to answer it.
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
                clauses.Add($"{RelationalEnumOrder.Read(plan.Dialect, binding)} " +
                            (item.Desc ? "DESC" : "ASC"));
                handled.Add(item);
            }
            catch (MappingValueException) { }
        }
        // This store used to append the identity to every ORDER BY itself, and was the only one whose paged
        // reads were therefore stable over a non-unique key. The framework now supplies that tiebreaker for
        // every adapter (FilterPushdownCoordinator.EnsureOrderForPage), so keeping a private copy would spell
        // the same column twice.
        return clauses.Count == 0
            ? (StableOrder(plan), RepositoryQueryResult<TEntity>.NoSortHandled)
            : ("ORDER BY " + string.Join(", ", clauses), handled.ToFrozenSet());
    }

    private static string StableOrder(MySqlEntityPlan<TEntity, TKey> plan) =>
        "ORDER BY " + string.Join(", ", plan.IdentityRoots.Select(MySqlDialect.Quote));

    private static async Task<long> CountExact(
        MySqlConnection connection,
        MySqlEntityPlan<TEntity, TKey> plan,
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
            clauses.Add($"{MySqlDialect.Quote(value.Binding.PhysicalPath.Name)} = @{name}");
        }
        if (clauses.Count == 0) throw new InvalidOperationException("A relational identity predicate cannot be empty.");
        return "(" + string.Join(" AND ", clauses) + ")";
    }

    private static TEntity Materialize(
        MySqlEntityPlan<TEntity, TKey> plan, IReadOnlyDictionary<string, object?> row) => plan.Hydrate(row);

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

    private async Task<MySqlConnection> Open(CancellationToken ct)
    {
        var connection = new MySqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    // The server answered and the credentials work; the Koan database does not exist yet. Managed
    // lifecycle creates it before the first schema DDL, so a fresh zero-configuration server
    // provisions instead of failing (ER_BAD_DB_ERROR).
    private async Task<MySqlConnection> OpenOrCreate(CancellationToken ct)
    {
        try
        {
            return await Open(ct).ConfigureAwait(false);
        }
        catch (MySqlException error) when (error.Number == 1049)
        {
            var builder = new MySqlConnectionStringBuilder(_options.ConnectionString);
            var database = builder.Database;
            builder.Database = null;
            await using var maintenance = new MySqlConnection(builder.ConnectionString);
            await maintenance.OpenAsync(ct).ConfigureAwait(false);
            await using var create = new MySqlCommand(
                $"CREATE DATABASE IF NOT EXISTS `{database.Replace("`", "``")}`", maintenance);
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return await Open(ct).ConfigureAwait(false);
        }
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

    private sealed class Batch(MySqlRepository<TEntity, TKey> repository) : IBatchSet<TEntity, TKey>
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
