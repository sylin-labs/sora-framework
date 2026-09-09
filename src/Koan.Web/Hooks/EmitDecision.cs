namespace Koan.Web.Hooks;

/// <summary>
/// Emission decision allows replacing or continuing the payload pipeline.
/// </summary>
public abstract record EmitDecision
{
    public sealed record Continue() : EmitDecision;
    public sealed record Replace(object Payload) : EmitDecision;
    public static Continue Next() => new();
    public static Replace With(object payload) => new(payload);

    /// <summary>
    /// Select a terminal collection projection over the exact selected source rows. Mapping is
    /// deferred until the endpoint verifies those sources; later emit hooks do not see the view.
    /// </summary>
    public static EmitDecision Project<TEntity, TView>(IEnumerable<TEntity> rows, Func<TEntity, TView> mapper)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(mapper);
        return new SourceProjection<TEntity, TView>(rows.ToArray(), mapper);
    }

    internal abstract record DeferredProjection : EmitDecision
    {
        internal abstract bool Matches<T>(IReadOnlyList<T> rows) where T : class;
        internal abstract object? Map(Func<bool> sourceUnchanged);
    }

    private sealed record SourceProjection<TEntity, TView>(TEntity[] Sources, Func<TEntity, TView> Mapper)
        : DeferredProjection where TEntity : class
    {
        internal override bool Matches<T>(IReadOnlyList<T> rows)
            => typeof(T) == typeof(TEntity) && rows.Count == Sources.Length
                && Sources.Select((row, index) => ReferenceEquals(row, rows[index])).All(same => same);

        internal override object? Map(Func<bool> sourceUnchanged)
        {
            if (!sourceUnchanged()) return null;
            var views = new TView[Sources.Length];
            for (var index = 0; index < Sources.Length; index++)
                views[index] = Mapper(Sources[index]);
            if (!sourceUnchanged()) return null;
            return views;
        }
    }
}
