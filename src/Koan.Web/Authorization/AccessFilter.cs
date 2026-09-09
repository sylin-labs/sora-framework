using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Koan.Data.Abstractions.Filtering;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 (§B) — the default <see cref="IAccessFilter{TEntity}"/> accumulator. The endpoint creates one, passes
/// it to <c>EntityAccess&lt;T&gt;.Constrain</c> (which appends via <c>Where</c>/<see cref="Stamp{TProp}"/>),
/// then reads <see cref="Filter"/> (narrow / verify / bound) and applies <see cref="ApplyStamps"/> (create /
/// update). Mutable and single-use per request.
/// </summary>
public sealed class AccessFilter<TEntity> : IAccessFilter<TEntity>
{
    private readonly List<Action<TEntity>> _stamps = new();

    /// <summary>A fresh empty accumulator (e.g. the read path, which only collects predicates).</summary>
    public static AccessFilter<TEntity> Empty => new();

    public Filter? Filter { get; private set; }

    /// <summary>True when at least one server-truth stamp is pending (create/update wrote the owner).</summary>
    internal bool HasStamps => _stamps.Count > 0;

    public IAccessFilter<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Filter = Koan.Data.Abstractions.Filtering.Filter.And(Filter,
            Koan.Data.Abstractions.Filtering.Filter.Snapshot(LinqFilterCompiler.Compile(predicate)));
        return this;
    }

    public IAccessFilter<TEntity> Where(Expression<Func<TEntity, bool>> predicate, string? partition)
    {
        Filter = Koan.Data.Abstractions.Filtering.Filter.And(Filter,
            Koan.Data.Abstractions.Filtering.Filter.Snapshot(
                Koan.Data.Abstractions.Filtering.Filter.SameIdIn(predicate, partition)));
        return this;
    }

    public IAccessFilter<TEntity> Stamp<TProp>(Expression<Func<TEntity, TProp>> selector, TProp value)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var property = ResolveWritableProperty(selector);
        _stamps.Add(model => property.SetValue(model, value));
        return this;
    }

    /// <summary>Apply every recorded owner write to <paramref name="model"/> — server-truth, overwriting any client
    /// value. The create-stamp / freeze-ownership step.</summary>
    internal void ApplyStamps(TEntity model)
    {
        foreach (var stamp in _stamps) stamp(model);
    }

    private static PropertyInfo ResolveWritableProperty<TProp>(Expression<Func<TEntity, TProp>> selector)
    {
        var body = selector.Body;
        // Unwrap an implicit Convert (e.g. a value type widened to object / a nullable).
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            body = convert.Operand;
        }
        if (body is MemberExpression { Member: PropertyInfo property } && property.CanWrite)
        {
            return property;
        }
        throw new InvalidOperationException(
            $"Access Stamp selector must be a writable property access (e.g. o => o.OwnerId); got: {selector.Body}.");
    }
}
