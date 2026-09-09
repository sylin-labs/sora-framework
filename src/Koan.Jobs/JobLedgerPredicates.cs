using System.Linq.Expressions;

namespace Koan.Jobs;

/// <summary>
/// Builds the pushed-down predicates the durable ledger hands the data layer (JOBS-0005 §19.3). Conditional
/// composition (only the non-null <see cref="JobQuery"/> axes become clauses) keeps the store-side filter tight —
/// no <c>field == null || ...</c> noise the adapter can't optimize.
/// </summary>
internal static class JobLedgerPredicates
{
    /// <summary>The declarative dashboard/facade query → a single conjunctive predicate (omitting wildcard axes).</summary>
    public static Expression<Func<JobRecord, bool>> ForQuery(JobQuery q)
    {
        Expression<Func<JobRecord, bool>>? p = null;
        if (q.WorkType is { } wt) p = And(p, r => r.WorkType == wt);
        if (q.WorkId is { } wid) p = And(p, r => r.WorkId == wid);
        if (q.Action is { } a) p = And(p, r => r.Action == a);
        if (q.Status is { } s) p = And(p, r => r.Status == s);
        if (q.WorkSource is { } source)
        {
            source = JobDataRoute.NormalizeSource(source) ?? "";
            p = And(p, source.Length == 0 ? r => r.WorkSource == null || r.WorkSource == "" : r => r.WorkSource == source);
        }
        if (q.WorkAdapter is { } adapter)
        {
            adapter = JobDataRoute.NormalizeSelector(adapter) ?? "";
            p = And(p, adapter.Length == 0 ? r => r.WorkAdapter == null || r.WorkAdapter == "" : r => r.WorkAdapter == adapter);
        }
        if (q.WorkPartition is { } partition)
            p = And(p, partition.Length == 0 ? r => r.WorkPartition == null || r.WorkPartition == "" : r => r.WorkPartition == partition);
        return p ?? (static r => true);
    }

    /// <summary>Non-terminal = Created/Queued/Running, as an **equality set**, not <c>Status &lt; Completed</c>:
    /// Mongo persists the enum by NAME, where ordering is lexicographic (not numeric), so an ordering comparison
    /// silently mismatches there. Equality translates on every store (JSON-number on relational, name on Mongo).</summary>
    public static Expression<Func<JobRecord, bool>> NonTerminal()
        => static r => r.Status == JobStatus.Created || r.Status == JobStatus.Queued
                    || r.Status == JobStatus.Running;

    /// <summary>Terminal = Completed/Failed/Cancelled/Dead, as an equality set (portable — see <see cref="NonTerminal"/>).</summary>
    public static Expression<Func<JobRecord, bool>> Terminal()
        => static r => r.Status == JobStatus.Completed || r.Status == JobStatus.Failed
                    || r.Status == JobStatus.Cancelled || r.Status == JobStatus.Dead;

    /// <summary>Active (non-terminal) rows of a work-type.</summary>
    public static Expression<Func<JobRecord, bool>> ActiveOf(string workType)
        => And(r => r.WorkType == workType, NonTerminal());

    /// <summary>Terminal rows of a work-type.</summary>
    public static Expression<Func<JobRecord, bool>> TerminalOf(string workType)
        => And(r => r.WorkType == workType, Terminal());

    public static Expression<Func<JobRecord, bool>> And(
        Expression<Func<JobRecord, bool>>? left, Expression<Func<JobRecord, bool>> right)
    {
        if (left is null) return right;
        var p = Expression.Parameter(typeof(JobRecord), "r");
        var body = Expression.AndAlso(
            new Rebind(left.Parameters[0], p).Visit(left.Body)!,
            new Rebind(right.Parameters[0], p).Visit(right.Body)!);
        return Expression.Lambda<Func<JobRecord, bool>>(body, p);
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
