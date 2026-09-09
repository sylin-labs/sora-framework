using System;
using System.Collections.Generic;
using System.Security.Claims;
using Koan.Data.Abstractions.Filtering;
using Koan.Web.Hooks;

namespace Koan.Web.Authorization;

/// <summary>
/// SEC-0004 (§C) — the pure per-row capability projector. Prepared ONCE per request from the coarse seam decisions,
/// the entity's <see cref="AccessGate"/>, the principal, the realization's single <c>Owner</c> predicate, and the
/// per-verb <c>Constrain</c> predicates; then <see cref="Can"/> is called for each fetched row. A verb is advertised
/// only when ALL three hold:
/// <list type="number">
///   <item>the coarse gate (the seam, respecting every <see cref="IAuthorize"/> provider) allows it;</item>
///   <item>the gate re-evaluated with the row bound to <c>owner</c> allows it — the row-level realization of
///   <see cref="AccessTrace"/>(OwnerDeferred), so an owner-gated verb appears only for rows the principal owns;</item>
///   <item>the verb's <c>Constrain</c> predicate passes on the row.</item>
/// </list>
/// Custom verbs (<see cref="AccessGate.Custom"/>) participate through the same row-bound gate evaluation. This is the
/// honesty counterweight to allow-by-default: the response STATES the realized authority, never silently assumes it.
/// </summary>
public sealed class RowProjection<TEntity>
{
    private readonly AccessGate _gate;
    private readonly ClaimsPrincipal _principal;
    private readonly bool _coarseRead;
    private readonly bool _coarseWrite;
    private readonly bool _coarseRemove;
    private readonly Func<TEntity, bool>? _owner;
    private readonly bool _authenticatedFallback;
    private readonly Func<TEntity, bool> _read;
    private readonly Func<TEntity, bool> _write;
    private readonly Func<TEntity, bool> _remove;

    public RowProjection(
        AccessGate gate,
        ClaimsPrincipal principal,
        bool coarseRead,
        bool coarseWrite,
        bool coarseRemove,
        Func<TEntity, bool>? owner,
        bool authenticatedFallback,
        Filter? readFilter,
        Filter? writeFilter,
        Filter? removeFilter)
        : this(gate, principal, coarseRead, coarseWrite, coarseRemove, owner, authenticatedFallback,
            readFilter, writeFilter, removeFilter, null)
    {
    }

    internal RowProjection(
        AccessGate gate,
        ClaimsPrincipal principal,
        bool coarseRead,
        bool coarseWrite,
        bool coarseRemove,
        Func<TEntity, bool>? owner,
        bool authenticatedFallback,
        Filter? readFilter,
        Filter? writeFilter,
        Filter? removeFilter,
        Func<TEntity, bool>? provenRead)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _coarseRead = coarseRead;
        _coarseWrite = coarseWrite;
        _coarseRemove = coarseRemove;
        _owner = owner;
        _authenticatedFallback = authenticatedFallback;
        _read = provenRead ?? Compile(readFilter, "Read access projection without query evidence");
        _write = Compile(writeFilter, "Update access projection");
        _remove = Compile(removeFilter, "Delete access projection");
    }

    /// <summary>The verbs this principal may perform on <paramref name="row"/> — read/write/remove (each gated and
    /// constrained) then any permitted custom verbs, in declaration order.</summary>
    public List<string> Can(TEntity row)
    {
        // The owner probe is the row-bound realization of AccessTrace(OwnerDeferred): with a declared Owner it
        // resolves against THIS row; without one it degrades to the coarse "authenticated" result (consistent with
        // the gate, which cannot bind a row it does not have, and with the boot warning Slice A emits for an
        // owner term declared without a realization). The evaluator invokes the probe only for an ActionGate that
        // declares an owner term — at most once per verb (the result is one fact per row: does the principal own
        // it), so a gate with no owner term never runs the predicate.
        Func<bool> ownerProbe = _owner is null ? (() => _authenticatedFallback) : (() => _owner(row));

        var can = new List<string>(4);
        if (_coarseRead && GateAllows(_gate.For(EntityAuthorizeActions.Read), ownerProbe) && _read(row))
            can.Add(EntityAuthorizeActions.Read);
        if (_coarseWrite && GateAllows(_gate.For(EntityAuthorizeActions.Write), ownerProbe) && _write(row))
            can.Add(EntityAuthorizeActions.Write);
        if (_coarseRemove && GateAllows(_gate.For(EntityAuthorizeActions.Remove), ownerProbe) && _remove(row))
            can.Add(EntityAuthorizeActions.Remove);

        // SEC-0004 (§C): a custom toolset/controller verb participates with zero extra wiring — it appears exactly
        // when its own gate permits this principal + row. (Custom verbs have no CRUD-axis Constrain; gate-only.)
        foreach (var kv in _gate.Custom)
        {
            if (GateAllows(kv.Value, ownerProbe)) can.Add(kv.Key);
        }
        return can;
    }

    private bool GateAllows(ActionGate gate, Func<bool> ownerProbe)
        => AccessGateEvaluator.Evaluate(gate, _principal, ownerProbe) is AuthorizeDecision.Allow;

    private static Func<TEntity, bool> Compile(Filter? filter, string operation)
    {
        Filter.RequireRowOnly(filter, operation);
        return filter is null ? _ => true : InMemoryFilterEvaluator.Compile<TEntity>(filter);
    }
}
