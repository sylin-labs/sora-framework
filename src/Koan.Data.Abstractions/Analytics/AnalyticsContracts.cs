using Koan.Data.Abstractions.Filtering;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Abstractions.Analytics;

public enum AnalyticsMeasureKind
{
    Count,
    Sum,
    Min,
    Max,
    Average
}

/// <summary>How an answer reports that its row cap interacted with the result.</summary>
public enum AnalyticsCompletion
{
    /// <summary>Every matching group/value is present.</summary>
    Complete,
    /// <summary>The answer reached the declared row cap; more groups exist than are shown.</summary>
    RowCapped
}

/// <summary>One answer row — the group value and the measure, keyed by their catalog names.</summary>
public sealed class AnalyticsRow
{
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
}

/// <summary>
/// The answer plus everything true about how it was produced. The envelope is not decoration: silent
/// staleness and silent capping are the two documented trust-killers of this feature class, so the
/// engine, the age, and the bounds are part of the answer itself (DATA-0123).
/// </summary>
public sealed class AnalyticsResult
{
    public required string Question { get; init; }
    public required string Engine { get; init; }

    /// <summary>
    /// How old the answer was when it left the store — <c>live</c> for on-demand asks; a duration label
    /// once materialized answers exist (ANL-3).
    /// </summary>
    public required string Age { get; init; }

    /// <summary>Which path produced the answer — <c>materialization</c> or <c>live</c>. The serve-or-compute
    /// decision is visible on every answer, never silent (the documented #1 trust-killer is invisible staleness).</summary>
    public required string ServedFrom { get; init; }

    /// <summary>When the materialization backing a served answer was last refreshed; null for live answers. The freshness a caching layer may derive from.</summary>
    public DateTimeOffset? MaterializedUtc { get; init; }

    public required int RowCap { get; init; }
    public required AnalyticsCompletion Completion { get; init; }
    public required IReadOnlyList<AnalyticsRow> Rows { get; init; }
}

/// <summary>Per-ask knobs that tune the answering path without touching the declaration. Null everywhere means "as declared".</summary>
public sealed class AnalyticsAskOptions
{
    /// <summary>
    /// The caller's freshness tolerance for this ask: a materialization within it is served, anything
    /// older computes live (labeled so). Null means the declared <c>ServeWithin</c> governs.
    /// </summary>
    public TimeSpan? MaxAge { get; init; }
}

/// <summary>
/// The catalog-facing face of a declared question. A question is immutable after declaration: it is the
/// unit of analytics, and everything the answer needs to say about itself (bounds, engine, age) was
/// decided at declaration time, not at the call site. Composers — which live in the connector that owns
/// the entity's mapping — read the same public shape the catalog publishes.
/// </summary>
public abstract class AnalyticsQuestion
{
    protected AnalyticsQuestion(
        string name,
        Type entityType,
        int rowCap,
        LambdaExpression? where,
        IReadOnlyList<AnalyticsParameterDeclaration> parameters,
        string? groupMember,
        AnalyticsMeasureKind measureKind,
        string? measureMember,
        AnalyticsProjectionPolicy? projection,
        IReadOnlyList<AnalyticsProjectionColumn> columns)
    {
        Name = name;
        EntityType = entityType;
        RowCap = rowCap;
        WhereExpression = where;
        Parameters = parameters;
        GroupMember = groupMember;
        MeasureKind = measureKind;
        MeasureMember = measureMember;
        Projection = projection;
        Columns = columns;
    }

    public string Name { get; }
    public Type EntityType { get; }
    public int RowCap { get; }

    /// <summary>
    /// The declared predicate as an expression — uncompiled, so ask-time parameter values substitute
    /// before compilation (the declared question is one artifact answering a family of slices).
    /// </summary>
    public LambdaExpression? WhereExpression { get; }

    /// <summary>The ask-time values this question accepts, by name and type.</summary>
    public IReadOnlyList<AnalyticsParameterDeclaration> Parameters { get; }

    /// <summary>The member answers are grouped by, when the question groups.</summary>
    public string? GroupMember { get; }

    public AnalyticsMeasureKind MeasureKind { get; }

    /// <summary>The member the measure aggregates, when one exists.</summary>
    public string? MeasureMember { get; }

    /// <summary>
    /// The projection policy when this question materializes; null for on-demand questions. Only
    /// materialized questions are answerable through the read-model door.
    /// </summary>
    public AnalyticsProjectionPolicy? Projection { get; }

    /// <summary>The materialization's columns, in declaration order (group first, then the measure).</summary>
    public IReadOnlyList<AnalyticsProjectionColumn> Columns { get; }

    /// <summary>Run the question — the closed-generic door captured at declaration.</summary>
    public abstract Task<AnalyticsResult> ExecuteAsync(
        IServiceProvider services,
        int rowCap,
        IReadOnlyDictionary<string, object?>? parameterValues,
        AnalyticsAskOptions? ask,
        CancellationToken cancellationToken);

    /// <summary>Run as declared — the common shape of every call site that has no per-ask knobs.</summary>
    public Task<AnalyticsResult> ExecuteAsync(
        IServiceProvider services,
        int rowCap,
        IReadOnlyDictionary<string, object?>? parameterValues,
        CancellationToken cancellationToken) =>
        ExecuteAsync(services, rowCap, parameterValues, null, cancellationToken);

    /// <summary>
    /// Compose without executing: what this question would answer, refuse, serve, or compute — with
    /// the facts a caller needs to understand it. Side-effect-free by contract: no compute, no rows
    /// written, no projection table created.
    /// </summary>
    public abstract Task<AnalyticsExplanation> ExplainAsync(
        IServiceProvider services,
        IReadOnlyDictionary<string, object?>? parameterValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// Re-materialize a projection: compute over the record store, replace the engine's rows, stamp the
    /// refresh state. On-demand questions refuse — there is nothing to refresh. The trigger names what
    /// caused the refresh and lands in the ledger.
    /// </summary>
    public abstract Task<ProjectionRefreshReceipt> RefreshAsync(IServiceProvider services, CancellationToken cancellationToken, string trigger = "programmatic");
}

/// <summary>What one projection refresh did — the receipt for triggers, loops, and facts.</summary>
public sealed record ProjectionRefreshReceipt(string Recipe, int RowCount, DateTimeOffset RefreshedUtc);

/// <summary>A page of materialized rows — the read-model door's answer.</summary>
public sealed class AnalyticsReadModelResult
{
    public required string Question { get; init; }
    public required AnalyticsCompletion Completion { get; init; }
    public required IReadOnlyList<AnalyticsRow> Rows { get; init; }
}

/// <summary>Which facet question a facet door answered — they are different questions, and the envelope says which ran.</summary>
public enum AnalyticsFacetMode
{
    /// <summary>Counts over every materialized row: "what is the distribution?"</summary>
    Distribution,
    /// <summary>Counts over rows written after a watermark: "what has been moving?" Not a distribution — updates count once at their new value, and deletions are invisible.</summary>
    Movement
}

/// <summary>One distinct value of a facet column with its row count. A null value buckets as null.</summary>
public sealed record AnalyticsFacetBucket(object? Value, long Count);

/// <summary>A page of facet buckets — the sink's mechanical GROUP BY answer.</summary>
public sealed record AnalyticsFacetPage(IReadOnlyList<AnalyticsFacetBucket> Buckets, bool Capped);

/// <summary>The movement-mode facet page: buckets over changed rows, with the coverage the envelope must state.</summary>
public sealed record AnalyticsMovementFacetPage(
    IReadOnlyList<AnalyticsFacetBucket> Buckets,
    bool Capped,
    long ChangesConsidered,
    long CurrentStamp);

/// <summary>A page of changed rows — the sink's mechanical delta answer, stamps already stripped.</summary>
public sealed record AnalyticsDeltaPage(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Capped,
    long CurrentStamp);

/// <summary>
/// A change cursor for the delta doors: opaque to consumers, monotonic in the engine. Every
/// response hands back the cursor for the next poll, so no consumer ever constructs one and the
/// server keeps no per-consumer state.
/// </summary>
public sealed record AnalyticsWatermark(string? Given, string Current)
{
    /// <summary>The issued shape. Versioned so a future cursor encoding can refuse old cursors loudly instead of misreading them.</summary>
    public const string Prefix = "wm1.";

    public static string Encode(long stamp) =>
        Prefix + stamp.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Decode a cursor handed back by a previous response. Null means "from the beginning"; anything else that is not a cursor refuses.</summary>
    public static bool TryDecode(string? watermark, out long stamp)
    {
        stamp = 0;
        return watermark is null ||
               (watermark.StartsWith(Prefix, StringComparison.Ordinal) &&
                long.TryParse(watermark.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out stamp));
    }
}

/// <summary>The facet door's envelope — which question ran, the buckets, and the movement-mode coverage facts.</summary>
public sealed class AnalyticsFacetResult
{
    public required string Question { get; init; }
    public required string Column { get; init; }
    public required AnalyticsFacetMode Mode { get; init; }
    public required IReadOnlyList<AnalyticsFacetBucket> Buckets { get; init; }
    public AnalyticsCompletion Completion { get; init; }

    /// <summary>Movement mode only: how many changed rows the movement summarizes.</summary>
    public long? ChangesConsidered { get; init; }

    /// <summary>Movement mode only: the cursor this answer consumed and the cursor for the next poll.</summary>
    public AnalyticsWatermark? Watermark { get; init; }

    /// <summary>Movement mode only, stated so it can never be implied: deleted source rows leave no trace in a derived store's stamps.</summary>
    public bool? DeletesInvisible { get; init; }
}

/// <summary>The delta door's envelope: changed rows plus the cursor for the next poll.</summary>
public sealed class AnalyticsDeltaResult
{
    public required string Question { get; init; }
    public required AnalyticsWatermark Watermark { get; init; }
    public required AnalyticsCompletion Completion { get; init; }
    public required IReadOnlyList<AnalyticsRow> Rows { get; init; }
}

/// <summary>The shape door's answer: everything about the answer's shape, none of the compute.</summary>
public sealed class AnalyticsShape
{
    public required string Name { get; init; }
    public required string Entity { get; init; }
    public required string MeasureKind { get; init; }
    public string? MeasureMember { get; init; }
    public string? GroupMember { get; init; }

    /// <summary>The output columns in declaration order (group first, then the measure).</summary>
    public required IReadOnlyList<AnalyticsProjectionColumn> Columns { get; init; }

    /// <summary>The ask-time values this question accepts, by name and type.</summary>
    public required IReadOnlyList<AnalyticsParameterDeclaration> Parameters { get; init; }

    public required int RowCap { get; init; }

    /// <summary>Whether the question materializes — the flag that decides which doors answer it.</summary>
    public bool Materialized { get; init; }

    public AnalyticsProjectionPolicy? Policy { get; init; }
}

/// <summary>What an explain door concluded, without executing anything.</summary>
public sealed class AnalyticsExplanation
{
    public required string Question { get; init; }
    public required string Entity { get; init; }

    /// <summary>The elected engine for materialized questions; the composed store's provider otherwise.</summary>
    public required string Engine { get; init; }

    public bool Materialized { get; init; }
    public AnalyticsProjectionPolicy? Policy { get; init; }

    /// <summary>What execution would do — <c>serve</c>, <c>compute</c>, or <c>refuse</c>.</summary>
    public required string Would { get; init; }

    /// <summary>The rationale: the refusal corrective, or why this path was chosen.</summary>
    public string? Reason { get; init; }

    public required int RowCap { get; init; }

    /// <summary>The parameters the question declares.</summary>
    public required IReadOnlyList<AnalyticsParameterDeclaration> Parameters { get; init; }

    /// <summary>The parameter names the caller supplied.</summary>
    public required IReadOnlyList<string> SuppliedParameters { get; init; }

    /// <summary>The composed SQL and its named parameters, when composition succeeded — a select over aggregates, never a secret.</summary>
    public AnalyticsSql? Composed { get; init; }

    /// <summary>Materialization facts, for materialized questions only.</summary>
    public DateTimeOffset? LastRefreshUtc { get; init; }
    public int? MaterializedRows { get; init; }
    public long? LastRefreshDurationMs { get; init; }

    /// <summary>What the elected sink can also do — <c>facets</c>, <c>delta</c>, <c>parquet</c>, <c>history</c>.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }
}

/// <summary>One re-materialization in the refresh ledger.</summary>
public sealed record AnalyticsRefreshLedgerEntry(
    string Recipe,
    DateTimeOffset RanUtc,
    int RowCount,
    long DurationMs,
    string Trigger);

/// <summary>The history door's answer: the projection's recent refreshes, newest first.</summary>
public sealed class AnalyticsHistory
{
    public required string Question { get; init; }
    public required IReadOnlyList<AnalyticsRefreshLedgerEntry> Entries { get; init; }
}

/// <summary>
/// The connector-side half of the analytics grammar: the repository that owns an entity's physical
/// mapping and dialect composes its own SQL for a declared question. The framework owns the question,
/// the bounds, and the envelope; the adapter owns the words (DATA-0119's split, applied to analytics).
/// Implemented by relational repositories and forwarded through the repository facade.
/// </summary>
public interface IAnalyticsQueryComposer<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Compose the bounded ask with the ask's parameter values. <paramref name="corrective"/> explains
    /// the refusal when the question — or the store it would run against — cannot answer it honestly.
    /// </summary>
    bool TryCompose(
        AnalyticsQuestion question,
        IReadOnlyDictionary<string, object?>? parameterValues,
        out AnalyticsSql sql,
        out string? corrective);
}

/// <summary>
/// Optional engine capability: export a materialization as Parquet. Engines whose storage can emit
/// Parquet server-side (DuckDB via COPY) implement this; the read-model door advertises the format
/// only when the sink does, so the export is never a silent empty file.
/// </summary>
public interface IAnalyticsParquetExport
{
    Task<byte[]> ExportParquetAsync(
        string recipe,
        IReadOnlyDictionary<string, object?>? equalityFilters,
        CancellationToken cancellationToken);
}

/// <summary>One composed ask: the SQL text, its named parameters, and the output column order.</summary>
public sealed record AnalyticsSql(
    string Text,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> OutputNames,
    string Provider);

/// <summary>The declared refresh/serving policy of a materialized projection.</summary>
public sealed record AnalyticsProjectionPolicy
{
    /// <summary>Scheduled refresh cadence. Null means refresh is manual/trigger-only.</summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>
    /// Declared freshness tolerance: a materialization no older than this is served instead of computed.
    /// Null means the materialization is always served when it exists.
    /// </summary>
    public TimeSpan? ServeWithin { get; init; }

    /// <summary>When serving found the answer stale, re-materialize as part of answering.</summary>
    public bool BackfillOnRead { get; init; }
}

/// <summary>One column of a materialization, as it is written into and read from the engine.</summary>
public sealed record AnalyticsProjectionColumn(string Name, Type ClrType);

/// <summary>
/// A declared ask-time value for one question — name and type, plus an optional default that binds
/// when no ask-time value arrives (which is exactly how a parameterized projection refreshes: there
/// is no ask-time value at refresh time).
/// </summary>
public sealed record AnalyticsParameterDeclaration(string Name, Type ClrType, object? Default = null, bool HasDefault = false);

/// <summary>
/// The parameter marker used inside analytics Where clauses
/// (<c>Analytics.P&lt;T&gt;("name")</c>). Never invoke it — declaration captures it as an expression
/// node; ask-time binding substitutes the value.
/// </summary>
public static class AnalyticsParameter
{
    public static T Value<T>(string name) =>
        throw new InvalidOperationException(
            $"Analytics parameter '{name}' is a marker and is only valid inside a Where clause.");
}

/// <summary>
/// Substitutes parameter markers inside a declared question's Where expression with the ask's bound
/// values, before the filter is compiled. Substitution happens here — at the analytics layer — so the
/// shared filter compiler never learns about parameters, and every relational adapter composes
/// parameterized questions through the exact code path it already trusts.
/// </summary>
public static class AnalyticsParameterBinder
{
    public static Expression<Func<TEntity, bool>> Bind<TEntity>(
        Expression<Func<TEntity, bool>> where,
        IReadOnlyList<AnalyticsParameterDeclaration> declarations,
        IReadOnlyDictionary<string, object?>? values,
        out string? corrective)
        where TEntity : class
    {
        corrective = null;
        var visitor = new Binder(declarations, values ?? new Dictionary<string, object?>(StringComparer.Ordinal));
        var body = visitor.Visit(where.Body);
        if (visitor.Missing.Count > 0)
        {
            corrective = "Missing parameter value(s): " + string.Join(", ", visitor.Missing) + ".";
            return where;
        }
        if (visitor.Undeclared.Count > 0)
        {
            corrective = "Values were supplied for parameter(s) the question does not declare: " +
                         string.Join(", ", visitor.Undeclared) + ".";
            return where;
        }
        return Expression.Lambda<Func<TEntity, bool>>(body, where.Parameters);
    }

    private sealed class Binder(
        IReadOnlyList<AnalyticsParameterDeclaration> declarations,
        IReadOnlyDictionary<string, object?> values) : ExpressionVisitor
    {
        public readonly List<string> Missing = [];
        public readonly List<string> Undeclared = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (IsMarker(node))
            {
                var name = (string)((ConstantExpression)node.Arguments[0]).Value!;
                var declaration = declarations.FirstOrDefault(d => d.Name == name);
                if (declaration is null)
                {
                    Undeclared.Add(name);
                    return node;
                }
                if (!values.TryGetValue(name, out var value))
                {
                    // A declared default binds when no ask-time value arrives — that is exactly how a
                    // parameterized projection refreshes (there is no ask-time value at refresh).
                    if (declaration.HasDefault) value = declaration.Default;
                    else
                    {
                        Missing.Add(name);
                        return node;
                    }
                }
                var converted = value is null || declaration.ClrType.IsInstanceOfType(value)
                    ? value
                    : Convert.ChangeType(value, declaration.ClrType, System.Globalization.CultureInfo.InvariantCulture);
                return Expression.Constant(converted, declaration.ClrType);
            }
            return base.VisitMethodCall(node);
        }

        /// <summary>
        /// The marker set is a closed contract: the Abstractions spelling
        /// (<c>AnalyticsParameter.Value&lt;T&gt;</c>) and the module's ergonomic spelling
        /// (<c>Analytics.P&lt;T&gt;</c>, which this assembly cannot reference). A call is a marker when it
        /// is static, takes exactly one string literal, and comes from one of those two types. An unknown
        /// spelling is never substituted — the marker's own throw surfaces loudly at filter compile time.
        /// </summary>
        private static bool IsMarker(MethodCallExpression node)
        {
            if (!node.Method.IsStatic ||
                node.Arguments.Count != 1 ||
                node.Arguments[0] is not ConstantExpression literal ||
                literal.Value is not string)
                return false;
            return node.Method.DeclaringType?.FullName is "Koan.Data.Abstractions.Analytics.AnalyticsParameter"
                or "Koan.Data.Analytics.Analytics"
                && node.Method.Name is nameof(AnalyticsParameter.Value) or "P";
        }
    }
}

/// <summary>What the engine knows about one materialization's last refresh.</summary>
public sealed class ProjectionMaterializationState
{
    public required string Recipe { get; init; }
    public DateTimeOffset? LastRefreshUtc { get; init; }
    public int? RowCount { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>
/// The elected engine's materialization surface: where declared projections are stored, refreshed, and
/// read back. Implemented by the engine connector (DuckDB first — the per-host derived store is exactly
/// what its single-writer model wants).
/// </summary>
public interface IAnalyticsProjectionSink
{
    /// <summary>Refresh bookkeeping for one projection — what facts and the serve-or-compute decision read.</summary>
    Task<ProjectionMaterializationState?> ReadStateAsync(string recipe, CancellationToken cancellationToken);

    /// <summary>Create the materialization table when absent.</summary>
    Task EnsureAsync(string recipe, IReadOnlyList<AnalyticsProjectionColumn> columns, CancellationToken cancellationToken);

    /// <summary>
    /// Replace the materialization's rows with <paramref name="rows"/> and stamp the refresh state.
    /// <paramref name="trigger"/> names what caused the refresh and is recorded in the ledger — a
    /// refresh without its history entry is an adapter defect.
    /// </summary>
    Task WriteRowsAsync(
        string recipe,
        IReadOnlyList<AnalyticsProjectionColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        DateTimeOffset refreshUtc,
        long durationMs,
        string trigger,
        CancellationToken cancellationToken);

    /// <summary>Read materialized rows, optionally equality-filtered on declared columns.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsAsync(
        string recipe,
        int limit,
        int offset,
        IReadOnlyDictionary<string, object?>? equalityFilters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Distinct values of one materialized column with counts — the filter-building shape. Any
    /// tabular sink can answer this; the movement variant lives on <see cref="IAnalyticsChangeTracking"/>.
    /// </summary>
    Task<AnalyticsFacetPage> ReadFacetsAsync(
        string recipe,
        string column,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>The projection's recent refreshes, newest first — the ledger the history door reads.</summary>
    Task<IReadOnlyList<AnalyticsRefreshLedgerEntry>> ReadHistoryAsync(
        string recipe,
        int take,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional engine capability: per-row change stamps under the delta doors. The same pattern as
/// <see cref="IAnalyticsParquetExport"/> — doors advertise what the elected engine actually offers
/// and refuse what it does not, so an engine without stamps degrades loudly, not wrongly. "Changed"
/// means "written by a materialization after the given stamp": refreshes rewrite wholesale, so the
/// stamp is the refresh's time, not a source-side modification feed.
/// </summary>
public interface IAnalyticsChangeTracking
{
    /// <summary>Rows written after <paramref name="sinceStamp"/>, oldest first, plus the current stamp for the next poll.</summary>
    Task<AnalyticsDeltaPage> ReadDeltaAsync(
        string recipe,
        long sinceStamp,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Facet buckets over changed rows, with the count of rows the movement summarizes.</summary>
    Task<AnalyticsMovementFacetPage> ReadFacetsChangedSinceAsync(
        string recipe,
        string column,
        long sinceStamp,
        int limit,
        CancellationToken cancellationToken);
}
