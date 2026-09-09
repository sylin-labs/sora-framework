using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Analytics;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Analytics.Infrastructure;
using Koan.Data.Analytics.Recipes;
using Koan.Data.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koan.Data.Analytics.Runtime;

/// <summary>
/// The bounded execution path: resolve the entity's own repository, let it compose the ask in its own
/// dialect, and run it under the declared ceiling. A materialized question prefers its projection —
/// served when the declared tolerance allows, computed live (labeled so, backfilled when declared)
/// otherwise — and every answer says which path produced it.
/// </summary>
internal static class AnalyticsExecution
{

    /// <summary>The read-model door over the elected engine's materialization store.</summary>
    public static async Task<AnalyticsReadModelResult> ReadRowsAsync(
        string name,
        int limit,
        int offset,
        IReadOnlyDictionary<string, object?>? filters,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);
        var question = ResolveMaterialized(name, "the read-model door serves materialized rows", "Run it instead, or add Materialize to the declaration.");
        var services = AmbientHost();
        var sink = GetSink(services, question);
        await sink.EnsureAsync(name, question.Columns, cancellationToken).ConfigureAwait(false);
        var raw = await sink.ReadRowsAsync(name, limit + 1, offset, filters, cancellationToken).ConfigureAwait(false);
        var capped = raw.Count > limit;
        var rows = new List<AnalyticsRow>(Math.Min(raw.Count, limit));
        foreach (var row in raw.Take(limit))
            rows.Add(new AnalyticsRow { Values = row });
        return new AnalyticsReadModelResult
        {
            Question = name,
            Completion = capped ? AnalyticsCompletion.RowCapped : AnalyticsCompletion.Complete,
            Rows = rows
        };
    }

    /// <summary>
    /// The facet door. Without <paramref name="since"/>: the distribution of one materialized column.
    /// With it: the movement — counts over rows a materialization wrote after the cursor, which is a
    /// different question, and the envelope says so (updates count once at their new value; deletions
    /// are invisible in a derived store's stamps).
    /// </summary>
    public static async Task<AnalyticsFacetResult> ReadFacetsAsync(
        string name, string by, string? since, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 200);
        ArgumentException.ThrowIfNullOrWhiteSpace(by);
        var question = ResolveMaterialized(name, "the facet door reads materializations", "Run it instead, or add Materialize to the declaration.");
        var column = question.Columns.FirstOrDefault(c => c.Name.Equals(by, StringComparison.OrdinalIgnoreCase));
        if (column is null)
            throw new NotSupportedException(
                $"Column '{by}' is not a declared column of projection '{name}'. " +
                "Declared columns: " + string.Join(", ", question.Columns.Select(static c => c.Name)) + ".");
        var sink = GetSink(AmbientHost(), question);
        await sink.EnsureAsync(name, question.Columns, cancellationToken).ConfigureAwait(false);

        if (since is null)
        {
            var page = await sink.ReadFacetsAsync(name, column.Name, limit, cancellationToken).ConfigureAwait(false);
            return new AnalyticsFacetResult
            {
                Question = name,
                Column = column.Name,
                Mode = AnalyticsFacetMode.Distribution,
                Buckets = page.Buckets,
                Completion = page.Capped ? AnalyticsCompletion.RowCapped : AnalyticsCompletion.Complete
            };
        }

        if (!AnalyticsWatermark.TryDecode(since, out var stamp))
            throw new NotSupportedException(
                $"'{since}' is not a watermark this door issued. Watermarks look like " +
                $"'{AnalyticsWatermark.Prefix}<milliseconds>' and come from a previous facets or delta response — " +
                "pass the cursor back unchanged, or omit it for the full distribution.");
        if (sink is not IAnalyticsChangeTracking tracking)
            throw new NotSupportedException(
                "Movement facets need a sink that tracks changes; the elected engine's sink does not. " +
                "Distribution facets (no since) remain available.");
        var movement = await tracking.ReadFacetsChangedSinceAsync(name, column.Name, stamp, limit, cancellationToken).ConfigureAwait(false);
        return new AnalyticsFacetResult
        {
            Question = name,
            Column = column.Name,
            Mode = AnalyticsFacetMode.Movement,
            Buckets = movement.Buckets,
            Completion = movement.Capped ? AnalyticsCompletion.RowCapped : AnalyticsCompletion.Complete,
            ChangesConsidered = movement.ChangesConsidered,
            Watermark = new AnalyticsWatermark(since, AnalyticsWatermark.Encode(movement.CurrentStamp)),
            DeletesInvisible = true
        };
    }

    /// <summary>
    /// The delta door: rows written after a cursor, plus the cursor for the next poll — the consumer
    /// holds it, the door hands it back, the server keeps no per-consumer state.
    /// </summary>
    public static async Task<AnalyticsDeltaResult> ReadDeltaAsync(
        string name, string? since, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var question = ResolveMaterialized(name, "the delta door reads materializations", "Run it instead, or add Materialize to the declaration.");
        if (!AnalyticsWatermark.TryDecode(since, out var stamp))
            throw new NotSupportedException(
                $"'{since}' is not a watermark this door issued. Watermarks look like " +
                $"'{AnalyticsWatermark.Prefix}<milliseconds>' and come from a previous delta response — " +
                "pass the cursor back unchanged, or omit it to start from the beginning.");
        var sink = GetSink(AmbientHost(), question);
        if (sink is not IAnalyticsChangeTracking tracking)
            throw new NotSupportedException(
                "The delta door needs a sink that tracks changes; the elected engine's sink does not. " +
                "The read-model door (rows) remains available.");
        await sink.EnsureAsync(name, question.Columns, cancellationToken).ConfigureAwait(false);
        var page = await tracking.ReadDeltaAsync(name, stamp, limit, cancellationToken).ConfigureAwait(false);
        var rows = new List<AnalyticsRow>(page.Rows.Count);
        foreach (var row in page.Rows)
            rows.Add(new AnalyticsRow { Values = row });
        return new AnalyticsDeltaResult
        {
            Question = name,
            Watermark = new AnalyticsWatermark(since, AnalyticsWatermark.Encode(page.CurrentStamp)),
            Completion = page.Capped ? AnalyticsCompletion.RowCapped : AnalyticsCompletion.Complete,
            Rows = rows
        };
    }

    /// <summary>Resolve a declared question and insist it materializes — the shared gate of every read-model door.</summary>
    private static AnalyticsQuestion ResolveMaterialized(string name, string posture, string remedy)
    {
        if (!AnalyticsCatalog.TryGet(name, out var question))
            throw new KeyNotFoundException($"No analytics question named '{name}' is declared.");
        if (question.Projection is null)
            throw new NotSupportedException(
                $"Question '{name}' is an on-demand question; {posture}. {remedy}");
        return question;
    }

    private static IServiceProvider AmbientHost() =>
        Koan.Core.Hosting.App.AppHost.Current
        ?? throw new InvalidOperationException("No Koan host is active; analytics resolves through the ambient host.");

    private static IAnalyticsProjectionSink GetSink(IServiceProvider services, AnalyticsQuestion question)
    {
        return services.GetService<IAnalyticsProjectionSink>()
            ?? throw new NotSupportedException(
                "This question materializes, but the elected engine offers no projection sink. " +
                "The engine connector must implement IAnalyticsProjectionSink (the DuckDB connector does).");
    }

    public static async Task<AnalyticsResult> Run<TEntity, TKey>(
        AnalyticsQuestion<TEntity, TKey> question,
        IServiceProvider services,
        int rowCap,
        IReadOnlyDictionary<string, object?>? parameterValues,
        CancellationToken callerToken,
        AnalyticsAskOptions? ask = null)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var options = services.GetRequiredService<IOptions<AnalyticsOptions>>().Value;
        var data = services.GetRequiredService<IDataService>();
        var repository = data.GetRepository<TEntity, TKey>();

        if (repository is not IAnalyticsQueryComposer<TEntity> composer)
            throw new NotSupportedException(
                $"Analytics questions need a record store that can compose and execute aggregate asks. " +
                $"'{typeof(TEntity).Name}' is routed to '{repository.GetType().Name}', which offers none. " +
                "Reference a relational connector (for example Sylin.Koan.Data.Connector.Sqlite) for the entity's store.");

        if (repository is not IInstructionExecutor<TEntity> executor)
            throw new NotSupportedException(
                $"The store for '{typeof(TEntity).Name}' does not execute raw asks; analytics cannot run there.");

        // Values for parameters the question never declares are a caller mistake, not extra data. Refusing
        // here — before any composition — keeps the guard for questions with no Where clause too, where the
        // binder never runs.
        if (parameterValues is { Count: > 0 })
        {
            var declared = question.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var undeclared = parameterValues.Keys.Where(k => !declared.Contains(k)).ToList();
            if (undeclared.Count > 0)
                throw new NotSupportedException(
                    "Values were supplied for parameter(s) the question does not declare: " +
                    string.Join(", ", undeclared) + ".");
        }

        if (!composer.TryCompose(question, parameterValues, out var sql, out var corrective))
            throw new NotSupportedException(corrective ?? "The store cannot answer this question honestly.");

        var ceiling = rowCap > 0 ? rowCap : options.RowCap;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(options.Timeout);

        // Serve-or-compute: a materialized question prefers its projection within the declared tolerance —
        // or the caller's maxAge when tighter; anything staler (or colder) computes live — and backfills
        // when the question says so.
        if (question.Projection is not null)
        {
            var sink = GetSink(services, question);
            return await RunMaterializedAsync(
                question, sql, executor, sink, options, ceiling, timeout.Token, ask?.MaxAge).ConfigureAwait(false);
        }

        return await ComputeLiveAsync(question, sql, executor, ceiling, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<AnalyticsResult> RunMaterializedAsync<TEntity, TKey>(
        AnalyticsQuestion<TEntity, TKey> question,
        AnalyticsSql composed,
        IInstructionExecutor<TEntity> executor,
        IAnalyticsProjectionSink sink,
        AnalyticsOptions options,
        int ceiling,
        CancellationToken token,
        TimeSpan? callerMaxAge = null)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var policy = question.Projection!;
        var state = await sink.ReadStateAsync(question.Name, token).ConfigureAwait(false);
        // The caller's maxAge is a per-ask tightening of the declared tolerance, never a loosening:
        // a fresher-than-declared demand simply computes live (labeled so) instead of being served.
        var tolerance = callerMaxAge is { } max && (policy.ServeWithin is null || max < policy.ServeWithin)
            ? max
            : policy.ServeWithin;
        var fresh = state?.LastRefreshUtc is { } last &&
                    (tolerance is null || DateTimeOffset.UtcNow - last <= tolerance);

        if (fresh && state is not null)
        {
            var served = await sink.ReadRowsAsync(question.Name, ceiling + 1, 0, null, token).ConfigureAwait(false);
            var capped = served.Count > ceiling;
            var age = DateTimeOffset.UtcNow - (state.LastRefreshUtc ?? DateTimeOffset.UtcNow);
            return new AnalyticsResult
            {
                Question = question.Name,
                Engine = EngineName(sink),
                Age = $"{Math.Max(0, (int)age.TotalSeconds)}s",
                ServedFrom = "materialization",
                MaterializedUtc = state.LastRefreshUtc,
                RowCap = ceiling,
                Completion = capped ? AnalyticsCompletion.RowCapped : AnalyticsCompletion.Complete,
                Rows = served.Take(ceiling).Select(static row => new AnalyticsRow { Values = row }).ToArray()
            };
        }

        // Stale or cold: compute live; backfill when declared. Either way the answer is labeled live.
        var result = await ComputeLiveAsync(question, composed, executor, ceiling, token).ConfigureAwait(false);
        if (policy.BackfillOnRead)
            await BackfillAsync(question, composed, executor, sink, options, token).ConfigureAwait(false);
        return result;
    }

    /// <summary>Re-materialize one projection: compute over the record store, replace the engine's rows.</summary>
    /// <summary>
    /// Refresh one materialized projection end to end: resolve the entity's repository, compose over the
    /// record store, replace the elected engine's rows, stamp the refresh state.
    /// </summary>
    public static async Task<ProjectionRefreshReceipt> RefreshAsync<TEntity, TKey>(
        AnalyticsQuestion<TEntity, TKey> question,
        IServiceProvider services,
        CancellationToken cancellationToken,
        string trigger = "programmatic")
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var data = services.GetRequiredService<IDataService>();
        var repository = data.GetRepository<TEntity, TKey>();
        if (repository is not IAnalyticsQueryComposer<TEntity> composer ||
            repository is not IInstructionExecutor<TEntity> executor)
            throw new NotSupportedException(
                $"Analytics projection refresh needs a record store that composes and executes aggregate asks; " +
                $"'{typeof(TEntity).Name}' is routed to one that offers neither.");
        var sink = GetSink(services, question);
        if (!composer.TryCompose(question, null, out var composed, out var corrective))
            throw new NotSupportedException(corrective ?? "The store cannot answer this question honestly.");
        return await RefreshAsync(question, composed, executor, sink, cancellationToken, trigger).ConfigureAwait(false);
    }

    public static async Task<ProjectionRefreshReceipt> RefreshAsync<TEntity, TKey>(
        AnalyticsQuestion<TEntity, TKey> question,
        AnalyticsSql composed,
        IInstructionExecutor<TEntity> executor,
        IAnalyticsProjectionSink sink,
        CancellationToken token,
        string trigger = "programmatic")
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var started = DateTimeOffset.UtcNow;
        var raw = question.GroupMember is null
            ? [new Dictionary<string, object?>(StringComparer.Ordinal)
               {
                   [MeasureAlias(question.MeasureKind, question.MeasureMember)] =
                       Normalize(await executor.ExecuteAsync<object?>(
                           InstructionSql.Scalar(composed.Text, DataOperationEffect.Read, composed.Parameters),
                           token).ConfigureAwait(false))
               }]
            : await executor.ExecuteAsync<object?>(
                InstructionSql.Query(composed.Text, DataOperationEffect.Read, composed.Parameters),
                token).ConfigureAwait(false) as IReadOnlyList<Dictionary<string, object?>>
            ?? throw new InvalidOperationException(
                "The store answered a projection refresh with an unexpected shape; the composer and the " +
                "executor disagree, which is an adapter defect.");

        await sink.EnsureAsync(question.Name, question.Columns, token).ConfigureAwait(false);
        var refreshUtc = DateTimeOffset.UtcNow;
        await sink.WriteRowsAsync(question.Name, question.Columns, raw, refreshUtc,
            (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, trigger, token).ConfigureAwait(false);
        return new ProjectionRefreshReceipt(question.Name, raw.Count, refreshUtc);
    }

    private static async Task BackfillAsync<TEntity, TKey>(
        AnalyticsQuestion<TEntity, TKey> question,
        AnalyticsSql composed,
        IInstructionExecutor<TEntity> executor,
        IAnalyticsProjectionSink sink,
        AnalyticsOptions options,
        CancellationToken token)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        _ = options;
        await RefreshAsync(question, composed, executor, sink, token, "backfill-on-read").ConfigureAwait(false);
    }

    private static async Task<AnalyticsResult> ComputeLiveAsync<TEntity, TKey>(
        AnalyticsQuestion<TEntity, TKey> question,
        AnalyticsSql sql,
        IInstructionExecutor<TEntity> executor,
        int ceiling,
        CancellationToken token)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        if (question.GroupMember is null)
        {
            var scalar = await executor.ExecuteAsync<object?>(
                InstructionSql.Scalar(sql.Text, DataOperationEffect.Read, sql.Parameters),
                token).ConfigureAwait(false);
            var value = Normalize(scalar);
            return new AnalyticsResult
            {
                Question = question.Name,
                Engine = sql.Provider,
                Age = "live",
                ServedFrom = "live",
                RowCap = ceiling,
                Completion = AnalyticsCompletion.Complete,
                Rows = [new AnalyticsRow { Values = new Dictionary<string, object?>(StringComparer.Ordinal) { [MeasureAlias(question.MeasureKind, question.MeasureMember)] = value } }]
            };
        }

        // Grouped answers fetch one row beyond the cap so a capped answer can say so honestly.
        var text = $"{sql.Text} LIMIT {ceiling + 1}";
        var raw = await executor.ExecuteAsync<object?>(
            InstructionSql.Query(text, DataOperationEffect.Read, sql.Parameters),
            token).ConfigureAwait(false);

        var rows = raw as IReadOnlyList<Dictionary<string, object?>>
            ?? throw new InvalidOperationException(
                "The store answered an analytics ask with an unexpected shape; the composer and the " +
                "executor disagree, which is an adapter defect.");

        var capped = rows.Count > ceiling;
        var result = new List<AnalyticsRow>(Math.Min(rows.Count, ceiling));
        foreach (var row in rows.Take(ceiling))
            result.Add(new AnalyticsRow { Values = row });
        return new AnalyticsResult
        {
            Question = question.Name,
            Engine = sql.Provider,
            Age = "live",
            ServedFrom = "live",
            RowCap = ceiling,
            Completion = capped ? AnalyticsCompletion.RowCapped : AnalyticsCompletion.Complete,
            Rows = result
        };
    }

    /// <summary>
    /// The explain path: compose without executing. Reports what the ask would do — serve, compute, or
    /// refuse, with the same corrective execution would raise — plus the composed SQL, the bounds, and
    /// the elected sink's capabilities. Side-effect-free by contract: no compute, no rows written, no
    /// projection table created.
    /// </summary>
    public static async Task<AnalyticsExplanation> Explain<TEntity, TKey>(
        AnalyticsQuestion<TEntity, TKey> question,
        IServiceProvider services,
        IReadOnlyDictionary<string, object?>? parameterValues,
        CancellationToken token)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var supplied = parameterValues is { Count: > 0 }
            ? parameterValues.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

        string engine = "(not elected)";
        var capabilities = new List<string>();
        ProjectionMaterializationState? state = null;
        AnalyticsSql? composed = null;
        string would;
        string? reason = null;

        if (question.Projection is not null)
        {
            IAnalyticsProjectionSink sink;
            try { sink = GetSink(services, question); }
            catch (NotSupportedException refusal)
            {
                return Refused(question, supplied, refusal.Message);
            }
            capabilities.Add("facets");
            capabilities.Add("history");
            if (sink is IAnalyticsChangeTracking) capabilities.Add("delta");
            if (sink is IAnalyticsParquetExport) capabilities.Add("parquet");
            state = await sink.ReadStateAsync(question.Name, token).ConfigureAwait(false);
        }

        // The same parameter guard execution applies — explain reports the refusal instead of raising it.
        if (parameterValues is { Count: > 0 })
        {
            var declared = question.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var undeclared = parameterValues.Keys.Where(k => !declared.Contains(k)).ToList();
            if (undeclared.Count > 0)
                return Refused(question, supplied,
                    "Values were supplied for parameter(s) the question does not declare: " +
                    string.Join(", ", undeclared) + ".");
        }

        var data = services.GetRequiredService<IDataService>();
        var repository = data.GetRepository<TEntity, TKey>();
        if (repository is not IAnalyticsQueryComposer<TEntity> composer)
            return Refused(question, supplied,
                $"'{typeof(TEntity).Name}' is routed to '{repository.GetType().Name}', which cannot compose aggregate asks. " +
                "Reference a relational connector (for example Sylin.Koan.Data.Connector.Sqlite) for the entity's store.");

        if (!composer.TryCompose(question, parameterValues, out var sql, out var corrective))
            return Refused(question, supplied, corrective ?? "The store cannot answer this question honestly.");
        composed = sql;
        // The elected engine: the sink that would serve a materialization, or the store that would compute.
        engine = question.Projection is not null ? EngineName(GetSink(services, question)) : sql.Provider;

        if (question.Projection is null)
        {
            would = "compute";
        }
        else
        {
            var tolerance = question.Projection.ServeWithin;
            var fresh = state?.LastRefreshUtc is { } last &&
                        (tolerance is null || DateTimeOffset.UtcNow - last <= tolerance);
            would = fresh ? "serve" : "compute";
            reason = would == "serve"
                ? "the materialization is within the declared tolerance"
                : state is null
                    ? "nothing is materialized yet"
                    : "the materialization is older than the declared tolerance";
            if (would == "compute" && question.Projection.BackfillOnRead)
                reason += "; the read would also backfill the materialization";
        }

        return new AnalyticsExplanation
        {
            Question = question.Name,
            Entity = question.EntityType.Name,
            Engine = engine,
            Materialized = question.Projection is not null,
            Policy = question.Projection,
            Would = would,
            Reason = reason,
            RowCap = question.RowCap,
            Parameters = question.Parameters,
            SuppliedParameters = supplied,
            Composed = composed,
            LastRefreshUtc = state?.LastRefreshUtc,
            MaterializedRows = state?.RowCount,
            LastRefreshDurationMs = state?.DurationMs,
            Capabilities = capabilities
        };

        static AnalyticsExplanation Refused(
            AnalyticsQuestion question, string[] supplied, string message) =>
            new()
            {
                Question = question.Name,
                Entity = question.EntityType.Name,
                Engine = "(not elected)",
                Materialized = question.Projection is not null,
                Policy = question.Projection,
                Would = "refuse",
                Reason = message,
                RowCap = question.RowCap,
                Parameters = question.Parameters,
                SuppliedParameters = supplied,
                Capabilities = []
            };
    }

    /// <summary>The history door: the projection's refresh ledger, newest first.</summary>
    public static async Task<AnalyticsHistory> ReadHistoryAsync(
        string name, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        var question = ResolveMaterialized(name, "the history door reads materializations", "Run it instead, or add Materialize to the declaration.");
        var sink = GetSink(AmbientHost(), question);
        var entries = await sink.ReadHistoryAsync(name, take, cancellationToken).ConfigureAwait(false);
        return new AnalyticsHistory { Question = name, Entries = entries };
    }

    /// <summary>
    /// The shape door: everything about the answer's shape, from the declaration alone — no sink, no
    /// compute. On-demand questions shape too, with Materialized false saying which doors apply.
    /// </summary>
    public static AnalyticsShape Shape(string name)
    {
        if (!AnalyticsCatalog.TryGet(name, out var question))
            throw new KeyNotFoundException($"No analytics question named '{name}' is declared.");
        return new AnalyticsShape
        {
            Name = question.Name,
            Entity = question.EntityType.Name,
            MeasureKind = question.MeasureKind.ToString().ToLowerInvariant(),
            MeasureMember = question.MeasureMember,
            GroupMember = question.GroupMember,
            Columns = question.Columns,
            Parameters = question.Parameters,
            RowCap = question.RowCap,
            Materialized = question.Projection is not null,
            Policy = question.Projection
        };
    }

    private static string EngineName(IAnalyticsProjectionSink sink) =>
        sink.GetType().Name.Contains("DuckDb", StringComparison.OrdinalIgnoreCase) ? "duckdb" :
        sink.GetType().Name.Replace("AnalyticsProjectionSink", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static string MeasureAlias(AnalyticsMeasureKind kind, string? member) =>
        kind == AnalyticsMeasureKind.Count ? "count" : $"{kind.ToString().ToLowerInvariant()}_{member}";

    private static object? Normalize(object? value) => value;
}

