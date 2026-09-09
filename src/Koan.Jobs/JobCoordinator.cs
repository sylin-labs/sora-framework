using System.Diagnostics;
using System.Text.Json;
using Koan.Core.Context;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Jobs.Infrastructure;
using Koan.Jobs.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koan.Jobs;

/// <inheritdoc/>
internal sealed class JobCoordinator : IJobCoordinator
{
    private readonly IJobLedger _ledger;
    private readonly JobTypeRegistry _registry;
    private readonly JobOrchestrator _orchestrator;
    private readonly JobWakeCoordinator _wake;
    private readonly IServiceProvider _services;
    private readonly JobsOptions _options;
    private readonly TimeProvider _clock;
    private readonly JobsContextPlan _contextPlan;

    public JobCoordinator(IJobLedger ledger, JobTypeRegistry registry, JobOrchestrator orchestrator,
        JobWakeCoordinator wake, IServiceProvider services, IOptions<JobsOptions> options, TimeProvider clock,
        JobsContextPlan contextPlan)
    {
        _ledger = ledger;
        _registry = registry;
        _orchestrator = orchestrator;
        _wake = wake;
        _services = services;
        _options = options.Value;
        _clock = clock;
        _contextPlan = contextPlan;
    }

    /// <summary>Resolve a job's gate key at submit. A property-based <c>[JobGate]</c> is read inline; a method-based
    /// resolver (§18) runs inside a DI scope so it can use scoped services (or load a related entity).</summary>
    private async Task<string?> ResolveGateKey(JobTypeBinding binding, object workItem, CancellationToken ct)
    {
        if (!binding.HasGateResolver) return binding.GateKey(workItem);
        using var scope = _services.CreateScope();
        return await binding.ResolveGateKey(workItem, scope.ServiceProvider, ct);
    }

    public async Task<JobHandle> SubmitAsync(object workItem, string action, TimeSpan? after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        var scope = CaptureSubmission(workItem.GetType());
        var accepted = await Accept(workItem, action, after, scope, ct).ConfigureAwait(false);
        if (!accepted.Coalesced)
        {
            await Announce(scope.PendingCommit, ct).ConfigureAwait(false);
        }

        return accepted.Handle;
    }

    public async Task<JobSubmission> SubmitSourceAsync<T>(
        IAsyncEnumerable<T> workItems,
        string action,
        CancellationToken ct)
        where T : Entity<T>, IKoanJob<T>
    {
        ArgumentNullException.ThrowIfNull(workItems);

        // One immutable operation snapshot for the complete source. Context is captured before the first await and
        // never rediscovered per item, so a logical submission cannot drift between tenants/subjects mid-stream.
        var scope = CaptureSubmission(typeof(T));
        var enumerated = 0L;
        var submitted = 0L;
        var coalesced = 0L;
        var failed = 0L;
        var pendingWake = 0;
        var sourceCompleted = false;
        var enumerating = true;
        var currentAccepted = false;
        var wakeInterval = _options.Mode == JobMode.Inline
            ? 1
            : Constants.Submission.WakeInterval;

        JobSubmission Snapshot()
            => new(
                typeof(T).FullName!,
                action,
                enumerated,
                submitted,
                coalesced,
                failed,
                sourceCompleted,
                scope.PendingCommit);

        async Task Flush(CancellationToken cancellationToken)
        {
            if (pendingWake == 0)
            {
                return;
            }

            await Announce(scope.PendingCommit, cancellationToken).ConfigureAwait(false);
            pendingWake = 0;
        }

        void SignalAcceptedPrefix()
        {
            if (pendingWake > 0 && !scope.PendingCommit)
            {
                _wake.Notify();
            }
        }

        try
        {
            // Re-establish the terminal's captured logical context for deferred source enumeration and every
            // work-item save. A source cannot accidentally make one submission drift onto the worker flow's axes.
            using var dataScope = KoanContext.Push(scope.DataContext);
            using var contextScope = _contextPlan.RestoreForSubmit(typeof(T), scope.Carrier);
            await foreach (var workItem in workItems.WithCancellation(ct).ConfigureAwait(false))
            {
                enumerating = false;
                currentAccepted = false;
                enumerated++;

                var accepted = await Accept(workItem, action, null, scope, ct).ConfigureAwait(false);
                if (accepted.Coalesced)
                {
                    coalesced++;
                }
                else
                {
                    submitted++;
                    pendingWake++;
                }

                currentAccepted = true;
                if (pendingWake >= wakeInterval)
                {
                    await Flush(ct).ConfigureAwait(false);
                }

                enumerating = true;
            }

            sourceCompleted = true;
            await Flush(ct).ConfigureAwait(false);
            return Snapshot();
        }
        catch (JobSubmissionException)
        {
            throw;
        }
        catch (JobSubmissionCanceledException)
        {
            throw;
        }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested)
        {
            if (!enumerating && !currentAccepted)
            {
                failed++;
            }

            SignalAcceptedPrefix();
            throw new JobSubmissionCanceledException(
                $"Job submission for '{typeof(T).Name}' was canceled; the summary reports the confirmed accepted prefix.",
                Snapshot(),
                error,
                ct);
        }
        catch (Exception error)
        {
            if (!enumerating && !currentAccepted)
            {
                failed++;
            }

            SignalAcceptedPrefix();
            var failure = enumerating && !sourceCompleted
                ? JobSubmissionException.FailureKind.SourceFailed
                : JobSubmissionException.FailureKind.SubmissionFailed;
            var message = failure == JobSubmissionException.FailureKind.SourceFailed
                ? $"The job Entity source '{typeof(T).Name}' failed after {submitted + coalesced} confirmed acceptance(s)."
                : $"Jobs could not accept '{typeof(T).Name}' at source ordinal {Math.Max(0, enumerated - 1)}. " +
                  "Inspect the inner error and ledger readiness; the summary reports the confirmed prefix only.";
            throw new JobSubmissionException(failure, message, Snapshot(), error);
        }
    }

    public async Task<JobHandle> TriggerAsync(string workType, string action, CancellationToken ct)
    {
        var binding = _registry.Require(workType);
        var policy = binding.ResolvePolicy(action, _options);
        var now = _clock.GetUtcNow();
        // The singleton is ephemeral — it must NOT be persisted into the consumer's entity collection. The orchestrator
        // creates a fresh instance via binding.NewSingleton at execution time (no store round-trip on the hot path).
        var workItem = binding.NewSingleton(Constants.Work.SingletonId);
        var carrier = _contextPlan.Capture(binding.ClrType);
        var route = JobDataRoute.From(EntityContext.Current);

        var coalesceKey = JobCoalesce.FoldAmbient(binding.CoalesceKey(workItem, action), carrier, route);
        if (coalesceKey is not null)
        {
            var existing = await _ledger.FindActiveByCoalesceKey(binding.WorkType, coalesceKey, ct);
            if (existing is not null) return Handle(existing.Id);
        }

        var gateKey = await ResolveGateKey(binding, workItem, ct);
        var rec = JobRecordFactory.Create(binding, policy, workItem, Constants.Work.SingletonId, action, now, null, Correlation(), gateKey, carrier, route);
        await _ledger.Append(rec, ct);

        if (!EntityContext.InTransaction) _wake.Notify();
        if (_options.Mode == JobMode.Inline && !EntityContext.InTransaction) await _orchestrator.DrainAsync(ct);
        return Handle(rec.Id);
    }

    public async Task CancelWorkAsync(string workType, string workId, CancellationToken ct)
    {
        var jobs = await _ledger.Query(JobDataRoute.From(EntityContext.Current).Query(workType, workId), ct);
        var now = _clock.GetUtcNow();
        foreach (var rec in jobs)
        {
            if (rec.IsTerminal) continue;
            rec.CancelRequestedAt = now;
            if (rec.Status == JobStatus.Running)
            {
                await _ledger.Update(rec, ct);
                _orchestrator.SignalCancel(rec.Id);
            }
            else
            {
                rec.Transitions.Add(new JobTransition { At = now, From = rec.Status, To = JobStatus.Cancelled, Note = "cancelled (pre-run)" });
                rec.Status = JobStatus.Cancelled;
                rec.LastSettledAt = now;
                await _ledger.Update(rec, ct);
            }
        }
    }

    public async Task<JobStatus?> StatusAsync(string workType, string workId, CancellationToken ct)
    {
        var jobs = await _ledger.Query(JobDataRoute.From(EntityContext.Current).Query(workType, workId), ct);
        if (jobs.Count == 0) return null;
        return jobs.OrderByDescending(j => j.FirstSubmittedAt).First().Status;
    }

    public Task<IReadOnlyList<JobRecord>> WhereAsync(JobQuery query, CancellationToken ct) => _ledger.Query(query, ct);

    public JobHandle Handle(string jobId) => new(jobId, async (timeout, ct) =>
    {
        var deadline = _clock.GetUtcNow() + timeout;
        while (true)
        {
            var r = await _ledger.Get(jobId, ct);
            if (r is null) return new JobOutcome(JobStatus.Dead, "job not found");
            if (r.IsTerminal) return new JobOutcome(r.Status, r.LastError);
            if (_clock.GetUtcNow() >= deadline) return new JobOutcome(r.Status, "timed out waiting for completion");
            await Task.Delay(TimeSpan.FromMilliseconds(50), _clock, ct);
        }
    });

    private static string? Correlation() => Activity.Current?.TraceId.ToString();

    private SubmissionScope CaptureSubmission(Type workType)
        => new(
            _clock.GetUtcNow(),
            Correlation(),
            _contextPlan.Capture(workType),
            EntityContext.Current ?? new EntityContext.ContextState(),
            EntityContext.InTransaction);

    private async Task<AcceptedSubmission> Accept(
        object workItem,
        string action,
        TimeSpan? after,
        SubmissionScope scope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        using var dataScope = KoanContext.Push(scope.DataContext);
        using var contextScope = _contextPlan.RestoreForSubmit(workItem.GetType(), scope.Carrier);
        var route = JobDataRoute.From(scope.DataContext);
        var binding = _registry.Require(workItem.GetType().FullName!);
        var policy = binding.ResolvePolicy(action, _options);
        var workId = binding.GetId(workItem);

        // Coalesce before any Entity save. The captured ambient participates in the key, so distinct logical
        // contexts cannot collapse onto one another merely because a source changes flow while enumerating.
        var coalesceKey = JobCoalesce.FoldAmbient(binding.CoalesceKey(workItem, action), scope.Carrier, route);
        if (coalesceKey is not null)
        {
            var existing = await _ledger.FindActiveByCoalesceKey(binding.WorkType, coalesceKey, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return new AcceptedSubmission(Handle(existing.Id), Coalesced: true);
            }
        }

        // The work Entity is persisted before the record that points at it. A failed ledger call is not counted as
        // accepted; retrying the failed item is the corrective path, with declared idempotency when effects demand it.
        var current = await binding.Load(workId, ct).ConfigureAwait(false);
        var incomingSnapshot = Snapshot(workItem);
        if (current is null || incomingSnapshot is null || Snapshot(current) != incomingSnapshot)
        {
            await binding.Save(workItem, ct).ConfigureAwait(false);
        }

        var gateKey = await ResolveGateKey(binding, workItem, ct).ConfigureAwait(false);
        var record = JobRecordFactory.Create(
            binding,
            policy,
            workItem,
            workId,
            action,
            scope.Now,
            after,
            scope.Correlation,
            gateKey,
            scope.Carrier,
            route);
        await _ledger.Append(record, ct).ConfigureAwait(false);
        return new AcceptedSubmission(Handle(record.Id), Coalesced: false);
    }

    private async Task Announce(bool pendingCommit, CancellationToken ct)
    {
        // Transactional outbox: the work-item Save and ledger Append are not visible until commit. The poll remains
        // the correctness path, so Jobs must not publish a premature hint or inline-drain uncommitted rows.
        if (pendingCommit)
        {
            return;
        }

        _wake.Notify();
        if (_options.Mode == JobMode.Inline)
        {
            await _orchestrator.DrainAsync(ct).ConfigureAwait(false);
        }
    }

    private static string? Snapshot(object workItem)
    {
        try { return JsonSerializer.Serialize(workItem, workItem.GetType()); }
        catch { return null; }
    }

    private sealed record SubmissionScope(
        DateTimeOffset Now,
        string? Correlation,
        IReadOnlyDictionary<string, string>? Carrier,
        EntityContext.ContextState DataContext,
        bool PendingCommit);

    private readonly record struct AcceptedSubmission(JobHandle Handle, bool Coalesced);
}
