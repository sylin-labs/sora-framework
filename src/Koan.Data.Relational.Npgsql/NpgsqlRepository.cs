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
using Koan.Data.Relational.Linq;
using Koan.Data.Relational.Mapping;
using Koan.Data.Relational.Npgsql.Runtime;
using Koan.Data.Relational.Orchestration;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koan.Data.Relational.Npgsql;

public sealed class NpgsqlRepository<TEntity, TKey> :
    IDataRepository<TEntity, TKey>,
    IQueryRepository<TEntity, TKey>,
    IRawQueryRepository<TEntity, TKey>,
    IDescribesCapabilities,
    IOptimizedDataRepository<TEntity, TKey>,
    IBulkUpsert<TKey>,
    IBulkDelete<TKey>,
    IConditionalWriteRepository<TEntity, TKey>,
    IInstructionExecutor<TEntity>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly NpgsqlRepositoryOptions _options;
    private readonly IServiceProvider _services;
    private readonly MappingPlan? _declaredMapping;
    private readonly DataSegmentationPlan _segmentation;
    private readonly INamingProvider? _namingProvider;
    private readonly object _plansGate = new();
    private readonly Dictionary<string, NpgsqlEntityPlan<TEntity, TKey>> _plans = new(StringComparer.Ordinal);
    private readonly int _planLimit;
    private readonly DataSourceReadinessCoordinator _readiness;
    private readonly IRelationalSchemaOrchestrator _schema;
    private readonly IRelationalStoreFeatures _features;
    private readonly RelationalSchemaPolicy _schemaPolicy;

    private NpgsqlEntityPlan<TEntity, TKey> _plan => ResolvePlan();

    public NpgsqlRepository(IServiceProvider services, NpgsqlRepositoryOptions options, IStorageNameResolver resolver)
        : this(services, options, resolver, null)
    {
    }

    public NpgsqlRepository(
        IServiceProvider services,
        NpgsqlRepositoryOptions options,
        IStorageNameResolver resolver,
        INamingProvider? namingProvider)
    {
        _services = services;
        _options = options;
        _readiness = services.GetRequiredService<DataSourceReadinessCoordinator>();
        _schema = services.GetRequiredService<IRelationalSchemaOrchestrator>();
        _features = new NpgsqlStoreFeatures(options.ProviderName);
        _schemaPolicy = new RelationalSchemaPolicy
        {
            Ddl = options.DdlPolicy,
            Matching = options.SchemaMatching,
            AllowProductionDdl = options.AllowProductionDdl,
            DefaultSchema = options.SearchPath,
            StorageLifecycle = options.SourcePlan.StorageLifecycle,
            Access = options.SourcePlan.Access
        };
        _segmentation = services.GetRequiredService<DataSegmentationPlan>();
        _planLimit = services.GetRequiredService<IOptions<MappingOptions>>().Value.PlanEntries;
        OptimizationInfo = services.GetStorageOptimization<TEntity, TKey>();
        _declaredMapping = services.GetRequiredService<IDataMappingPlans>().Find<TEntity>(options.Source);
        _namingProvider = namingProvider;
        _ = resolver;
    }

    private NpgsqlEntityPlan<TEntity, TKey> ResolvePlan()
    {
        // A declared map names one physical container. An ambient partition asks for a different one, and this
        // store has no way to honour both — so it says so rather than serving the pinned container and leaving
        // the caller to believe the partition took effect. SQLite and Redis have always refused this; these three
        // read the pinned table instead, which is the same request answered with silence.
        if (_declaredMapping is not null && !string.IsNullOrWhiteSpace(EntityContext.Current?.Partition))
            throw new NotSupportedException(
                $"Explicit PostgreSQL map '{_declaredMapping.Id}' pins one physical container and cannot accept an ambient partition.");
        var table = _declaredMapping?.Container.Name ?? ResolveStorageName();
        var key = _declaredMapping?.Id ?? $"{_options.SearchPath}/{table}";
        lock (_plansGate)
        {
            if (_plans.TryGetValue(key, out var existing)) return existing;
            if (_plans.Count >= _planLimit)
                throw new InvalidOperationException(
                    $"The PostgreSQL repository reached its configured mapping-plan limit of {_planLimit}.");
            var mapping = _declaredMapping ?? RelationalManagedMapping.Compile<TEntity>(
                _options.Source,
                StorageAddress.From(_options.SearchPath, table));
            var created = new NpgsqlEntityPlan<TEntity, TKey>(mapping, _options, _segmentation);
            _plans.Add(key, created);
            return created;
        }
    }

    private string ResolveStorageName() => _namingProvider is null
        ? Core.Configuration.AdapterNaming.GetOrCompute<TEntity, TKey>(_services)
        : _namingProvider.ResolveStorage(typeof(TEntity), EntityContext.Current?.Partition, _services);

    public StorageOptimizationInfo OptimizationInfo { get; }

    public void Describe(ICapabilities capabilities) => NpgsqlFeatures.Describe(capabilities);

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
        var parameters = new SqlParameters();
        var predicates = new string[requested.Count];
        for (var index = 0; index < requested.Count; index++)
            predicates[index] = IdentityPredicate(_plan.Commands.Get(requested[index]).Identity, $"k{index}_", parameters);
        var sql = $"SELECT {_plan.Select} FROM {_plan.QualifiedTable} WHERE {string.Join(" OR ", predicates)}";
        await using var connection = await Open(ct).ConfigureAwait(false);
        var rows = await AdoCommands.QueryRowsAsync(connection, sql, parameters, null, ct).ConfigureAwait(false);
        var entities = rows.Select(Materialize).ToDictionary(static entity => entity.Id);
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
        foreach (var model in values)
            await Upsert(connection, transaction, model, ct).ConfigureAwait(false);
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
        return await AdoCommands.ExecuteAsync(connection, $"DELETE FROM {_plan.QualifiedTable}", null, null, ct).ConfigureAwait(false);
    }

    public async Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        if (strategy == RemoveStrategy.Fast)
        {
            _options.SourcePlan.Demand(DataOperationEffect.SchemaOrAdmin, "fast remove");
            await using var connection = await Open(ct).ConfigureAwait(false);
            await AdoCommands.ExecuteAsync(connection, $"TRUNCATE TABLE {_plan.QualifiedTable}", null, null, ct).ConfigureAwait(false);
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
        var (where, parameters) = Where(query.Filter);
        var (order, handledSort) = Order(query.Sort);
        var sortComplete = query.Sort.Count == 0 || handledSort.Count == query.Sort.Count;
        var paged = query.HasPagination && sortComplete;
        var sql = $"SELECT {_plan.Select} FROM {_plan.QualifiedTable}" +
                  (where is null ? string.Empty : $" WHERE {where}") + $" {order}" +
                  (paged ? $" LIMIT {query.EffectivePageSize()} OFFSET {query.EffectiveOffset()}" : string.Empty);
        await using var connection = await Open(ct).ConfigureAwait(false);
        var rows = await AdoCommands.QueryRowsAsync(connection, sql, Parameters(parameters), null, ct).ConfigureAwait(false);
        var items = rows.Select(Materialize).ToArray();
        long? total = null;
        if (query.CountStrategy is not null)
            total = paged ? await Count(connection, where, parameters, ct).ConfigureAwait(false) : items.LongLength;
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
        var (where, parameters) = Where(query.Filter);
        await using var connection = await Open(ct).ConfigureAwait(false);
        return CountResult.Exact(await Count(connection, where, parameters, ct).ConfigureAwait(false));
    }

    public async Task<RepositoryQueryResult<TEntity>> QueryRaw(
        string query,
        object? parameters,
        QueryDefinition shaping,
        CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        var paged = shaping.HasPagination;
        var sql = query.TrimStart().StartsWith("select ", StringComparison.OrdinalIgnoreCase)
            ? query
            : $"SELECT {_plan.Select} FROM {_plan.QualifiedTable} WHERE {query} {StableOrder()}" +
              (paged ? $" LIMIT {shaping.EffectivePageSize()} OFFSET {shaping.EffectiveOffset()}" : string.Empty);
        await using var connection = await Open(ct).ConfigureAwait(false);
        var rows = await AdoCommands.QueryRowsAsync(
            connection, sql, SqlParameters.FromObject(parameters), null, ct).ConfigureAwait(false);
        return new RepositoryQueryResult<TEntity>
        {
            Items = rows.Select(Materialize).ToArray(),
            PaginationHandled = paged,
            CountExecution = CountExecutionKind.None
        };
    }

    public async Task<CountResult> CountRaw(string query, object? parameters, CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        await using var connection = await Open(ct).ConfigureAwait(false);
        var count = await AdoCommands.ExecuteScalarInt64Async(connection, $"SELECT COUNT(1) FROM {_plan.QualifiedTable} WHERE {query}", SqlParameters.FromObject(parameters), null, ct).ConfigureAwait(false);
        return CountResult.Exact(count);
    }

    public async Task<bool> ConditionalReplaceAsync(
        TEntity model,
        Filter guard,
        CancellationToken ct = default)
    {
        await Ready(ct).ConfigureAwait(false);
        _options.SourcePlan.Demand(DataOperationEffect.Write, "conditional replace");
        var command = _plan.Commands.Update(model);
        var parameters = new SqlParameters();
        var set = UpdateSet(command.Values, parameters, "set_");
        var identity = IdentityPredicate(command.Identity, "key_", parameters);
        var (condition, conditionValues) = Where(guard);
        Add(parameters, conditionValues, "p");
        var sql = $"UPDATE {_plan.QualifiedTable} SET {set} WHERE {identity}" +
                  (condition is null ? string.Empty : $" AND ({condition})");
        await using var connection = await Open(ct).ConfigureAwait(false);
        return await AdoCommands.ExecuteAsync(connection, sql, parameters, null, ct).ConfigureAwait(false) == 1;
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
                var validation = await _schema.ValidateAsync(
                    _plan.Mapping, new NpgsqlDdlExecutor(connection, _plan.Dialect), _features, _schemaPolicy, ct)
                    .ConfigureAwait(false);
                return (TResult)(object)validation.Report(_options.ProviderName);
            }
            case RelationalInstructions.SqlScalar:
            case RelationalInstructions.SqlNonQuery:
            case RelationalInstructions.SqlQuery:
                return await ExecuteSql<TResult>(instruction, ct).ConfigureAwait(false);
            default:
                throw new NotSupportedException(
                    $"Instruction '{instruction.Name}' is not supported by {_options.ProviderName} for {typeof(TEntity).Name}.");
        }
    }

    private async Task Ready(CancellationToken ct)
    {
        var target = _plan.Target;
        if (_options.SourcePlan.UsesLegacyProvisioningReadiness)
        {
            await _readiness.Provision(
                _options.SourcePlan,
                target,
                token => Provision(token),
                token => Validate(token), ct).ConfigureAwait(false);
            return;
        }
        await _readiness.ValidateShape(_options.SourcePlan, target, token => Validate(token), ct)
            .ConfigureAwait(false);
    }

    private async Task Provision(CancellationToken ct)
    {
        await using var connection = await OpenOrCreate(ct).ConfigureAwait(false);
        await _schema.EnsureCreatedAsync(
            _plan.Mapping, new NpgsqlDdlExecutor(connection, _plan.Dialect), _features, _schemaPolicy, ct)
            .ConfigureAwait(false);
    }

    private async Task Validate(CancellationToken ct)
    {
        await using var connection = await Open(ct).ConfigureAwait(false);
        var validation = await _schema.ValidateAsync(
            _plan.Mapping, new NpgsqlDdlExecutor(connection, _plan.Dialect), _features, _schemaPolicy, ct)
            .ConfigureAwait(false);
        if (validation.IsServiceable) return;
        throw new SchemaMismatchException(
            typeof(TEntity).FullName ?? typeof(TEntity).Name,
            validation.Plan.Table,
            _options.SchemaMatching,
            validation.Corrective,
            _options.SourcePlan.UsesLegacyProvisioningReadiness);
    }

    private async Task<TEntity?> Get(NpgsqlConnection connection, NpgsqlTransaction? transaction, TKey id, CancellationToken ct)
    {
        var parameters = new SqlParameters();
        var predicate = IdentityPredicate(_plan.Commands.Get(id).Identity, "key_", parameters);
        var rows = await AdoCommands.QueryRowsAsync(
            connection,
            $"SELECT {_plan.Select} FROM {_plan.QualifiedTable} WHERE {predicate}",
            parameters, transaction, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : Materialize(rows[0]);
    }

    private async Task Upsert(NpgsqlConnection connection, NpgsqlTransaction? transaction, TEntity model, CancellationToken ct)
    {
        var generated = _plan.Mapping.Identity.IsGenerated && EqualityComparer<TKey>.Default.Equals(model.Id, default!);
        var command = generated ? _plan.Commands.Insert(model) : _plan.Commands.Update(model);
        var prepared = Insert(command, includeConflict: !generated);
        if (generated)
        {
            var key = await AdoCommands.ExecuteScalarAsync(connection, prepared.Sql + $" RETURNING {NpgsqlDialect.Quote(_plan.IdentityRoots.Single())}", prepared.Parameters, transaction, ct).ConfigureAwait(false);
            _plan.AssignGenerated(model, key);
            return;
        }
        var affected = await AdoCommands.ExecuteAsync(connection, prepared.Sql, prepared.Parameters, transaction, ct).ConfigureAwait(false);
        if (affected == 0 && ManagedFieldWriteScope.Current is { Count: > 0 })
            throw new InvalidOperationException("The write was rejected as a cross-scope write.");
    }

    private PreparedWrite Insert(RelationalCommandPlan command, bool includeConflict)
    {
        var all = command.Identity.Concat(command.Values).ToArray();
        var groups = all.GroupBy(static value => value.Binding.PhysicalPath.Name, StringComparer.Ordinal).ToArray();
        var parameters = new SqlParameters();
        var columns = new List<string>(groups.Length);
        var values = new List<string>(groups.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index].ToArray();
            var name = $"insert_{index}";
            columns.Add(NpgsqlDialect.Quote(group[0].Binding.PhysicalPath.Name));
            if (group.Any(static value => value.Binding.PhysicalPath.IsNested))
            {
                parameters.Add(name, _plan.NestedRoot(group));
                values.Add($"CAST(@{name} AS jsonb)");
            }
            else
            {
                parameters.Add(name, _plan.Parameter(group[0]));
                values.Add(_plan.IsStructuredRoot(group[0].Binding.PhysicalPath.Name)
                    ? $"CAST(@{name} AS jsonb)"
                    : $"@{name}");
            }
        }
        var sql = $"INSERT INTO {_plan.QualifiedTable} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
        if (!includeConflict) return new PreparedWrite(sql, parameters);
        var keys = string.Join(", ", command.Identity.Select(value => NpgsqlDialect.Quote(value.Binding.PhysicalPath.Name)));
        var update = UpdateSet(command.Values, parameters, "update_", qualifyReadRoots: true);
        sql += string.IsNullOrWhiteSpace(update)
            ? $" ON CONFLICT ({keys}) DO NOTHING"
            : $" ON CONFLICT ({keys}) DO UPDATE SET {update}";
        if (ManagedFieldWriteScope.Current is { Count: > 0 } managed)
        {
            var guards = new List<string>(managed.Count);
            var index = 0;
            foreach (var pair in managed)
            {
                var name = $"managed_{index++}";
                parameters.Add(name, ComparableScalarEncoding.EncodeComparand(pair.Value));
                guards.Add($"{_plan.ManagedPath(pair.Key, pair.Value?.GetType() ?? typeof(string), qualify: true)} = @{name}");
            }
            sql += $" WHERE {string.Join(" AND ", guards)}";
        }
        return new PreparedWrite(sql, parameters);
    }

    private string UpdateSet(
        IReadOnlyList<RelationalValue> values,
        SqlParameters parameters,
        string prefix,
        bool qualifyReadRoots = false)
    {
        var assignments = new List<string>();
        var parameterIndex = 0;
        foreach (var group in values.GroupBy(static value => value.Binding.PhysicalPath.Name, StringComparer.Ordinal))
        {
            var root = NpgsqlDialect.Quote(group.Key);
            var readRoot = qualifyReadRoots ? $"{_plan.QualifiedTable}.{root}" : root;
            var nested = group.Where(static value => value.Binding.PhysicalPath.IsNested).ToArray();
            if (nested.Length == 0)
            {
                var value = group.Single();
                var name = $"{prefix}{parameterIndex++}";
                parameters.Add(name, _plan.Parameter(value));
                assignments.Add($"{root} = " + (_plan.IsStructuredRoot(group.Key) ? $"CAST(@{name} AS jsonb)" : $"@{name}"));
                continue;
            }
            var expression = $"COALESCE({readRoot}, '{{}}'::jsonb)";
            foreach (var value in nested)
            {
                var name = $"{prefix}{parameterIndex++}";
                parameters.Add(name, _plan.JsonParameter(value));
                var path = "'{" + string.Join(',', value.Binding.PhysicalPath.Segments.Select(NpgsqlDialect.EscapePath)) + "}'";
                expression = $"jsonb_set({expression}, {path}, CAST(@{name} AS jsonb), true)";
            }
            assignments.Add($"{root} = {expression}");
        }
        return string.Join(", ", assignments);
    }

    private async Task<int> Delete(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<TKey> ids,
        CancellationToken ct)
    {
        var parameters = new SqlParameters();
        var predicates = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
            predicates[index] = IdentityPredicate(_plan.Commands.Delete(ids[index]).Identity, $"d{index}_", parameters);
        return await AdoCommands.ExecuteAsync(connection, $"DELETE FROM {_plan.QualifiedTable} WHERE {string.Join(" OR ", predicates)}", parameters, transaction, ct).ConfigureAwait(false);
    }

    private (string? Sql, IReadOnlyList<object?> Parameters) Where(Filter? filter)
    {
        if (filter is null) return (null, []);
        var translator = new SqlFilterTranslator(_plan.Dialect, _plan.Mapping, _plan.ManagedPath);
        var translated = translator.Translate(filter);
        return (translated.whereSql, translated.parameters);
    }

    private (string Sql, IReadOnlySet<SortSpec> Handled) Order(IReadOnlyList<SortSpec> sort)
    {
        if (sort.Count == 0) return (StableOrder(), RepositoryQueryResult<TEntity>.NoSortHandled);
        var clauses = new List<string>(sort.Count);
        var handled = new List<SortSpec>(sort.Count);
        foreach (var item in sort)
        {
            // An order key that reaches through a collection is an aggregate over a nested array, so it has
            // no binding of its own; the dialect expresses it directly instead of the framework sorting the
            // whole result in memory to answer it.
            if (item.Path.TraversesCollection || item.Aggregation != SortAggregation.None)
            {
                var term = RelationalCollectionOrder.Term(_plan.Dialect, _plan.Mapping, item);
                if (term is null) continue;
                clauses.Add(term);
                handled.Add(item);
                continue;
            }

            try
            {
                var use = _plan.Mapping.Use(
                    MappingPath.Of(item.Path.Members.Select(static member => member.Name).ToArray()),
                    MappingConsumer.Order);
                var binding = use.Bindings.Single();
                clauses.Add($"{RelationalEnumOrder.Read(_plan.Dialect, binding)} {(item.Desc ? "DESC" : "ASC")}");
                handled.Add(item);
            }
            catch (MappingValueException) { }
        }
        return clauses.Count == 0
            ? (StableOrder(), RepositoryQueryResult<TEntity>.NoSortHandled)
            : ("ORDER BY " + string.Join(", ", clauses), handled.ToFrozenSet());
    }

    /// <summary>
    /// The order a page is taken in when the caller named none: the Entity identity.
    ///
    /// <para>This was configurable, defaulting to <c>ORDER BY ctid</c> — the physical tuple address, which
    /// PostgreSQL moves when a row is updated. The string-query path below pages against this, so a write
    /// between two page requests could make them overlap or skip. CockroachDB already overrode the default
    /// to the identity, which is the answer every other relational store gives (DATA-0119).</para>
    /// </summary>
    private string StableOrder() =>
        "ORDER BY " + string.Join(", ", _plan.IdentityRoots.Select(NpgsqlDialect.Quote));

    private async Task<long> Count(
        NpgsqlConnection connection,
        string? where,
        IReadOnlyList<object?> parameters,
        CancellationToken ct) =>
        await AdoCommands.ExecuteScalarInt64Async(connection, $"SELECT COUNT(1) FROM {_plan.QualifiedTable}" + (where is null ? string.Empty : $" WHERE {where}"), Parameters(parameters), null, ct).ConfigureAwait(false);

    private static string IdentityPredicate(
        IEnumerable<RelationalValue> identity,
        string prefix,
        SqlParameters parameters)
    {
        var clauses = new List<string>();
        var index = 0;
        foreach (var value in identity)
        {
            var name = $"{prefix}{index++}";
            parameters.Add(name, value.Value);
            clauses.Add($"{NpgsqlDialect.Quote(value.Binding.PhysicalPath.Name)} = @{name}");
        }
        if (clauses.Count == 0) throw new InvalidOperationException("A relational identity predicate cannot be empty.");
        return "(" + string.Join(" AND ", clauses) + ")";
    }

    private TEntity Materialize(IReadOnlyDictionary<string, object?> row) => _plan.Hydrate(row);

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

    // The server answered and the credentials work; the Koan database does not exist yet. Managed
    // lifecycle creates it against the server's always-present maintenance database before the
    // first schema DDL, so a fresh zero-configuration server provisions instead of failing.
    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"") + '"';

    private async Task<NpgsqlConnection> OpenOrCreate(CancellationToken ct)
    {
        try
        {
            return await Open(ct).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState == "3D000")
        {
            var builder = new NpgsqlConnectionStringBuilder(_options.ConnectionString);
            var database = builder.Database;
            if (string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException(
                    "PostgreSQL reported a missing database, but no database name is configured. Set Database in the source connection string before automatic provisioning.", error);
            builder.Database = "postgres";
            await using var maintenance = new NpgsqlConnection(builder.ConnectionString);
            await maintenance.OpenAsync(ct).ConfigureAwait(false);
            await using var check = new NpgsqlCommand(
                "SELECT 1 FROM pg_database WHERE datname = $1", maintenance)
            { Parameters = { new NpgsqlParameter { Value = database } } };
            if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                try
                {
                    await using var create = new NpgsqlCommand(
                        $"CREATE DATABASE {QuoteIdentifier(database)}", maintenance);
                    await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (PostgresException race) when (race.SqlState == "42P04")
                {
                    // Another provisioner created it concurrently.
                }
            }
            return await Open(ct).ConfigureAwait(false);
        }
    }

    private async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // The caller never receives this connection, so nothing else can dispose it. MySQL and SQLite have
            // always done this; these two did not, and a store refusing connections leaked one per attempt.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
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
        return (TResult)(object)rows.Select(row => new Dictionary<string, object?>((IDictionary<string, object?>)row,
            StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static string InstructionSql(Instruction instruction)
    {
        if (instruction.Payload is string text && !string.IsNullOrWhiteSpace(text)) return text;
        var property = instruction.Payload?.GetType().GetProperty("Sql") ?? instruction.Payload?.GetType().GetProperty("sql");
        if (property?.GetValue(instruction.Payload) is string sql && !string.IsNullOrWhiteSpace(sql)) return sql;
        if (instruction.Parameters?.TryGetValue("sql", out var value) == true && value is string parameterSql)
            return parameterSql;
        throw new ArgumentException("Instruction payload is missing Sql.", nameof(instruction));
    }

    private sealed record PreparedWrite(string Sql, SqlParameters Parameters);

    private sealed class Batch(NpgsqlRepository<TEntity, TKey> repository) : IBatchSet<TEntity, TKey>
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
