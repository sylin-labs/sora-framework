using System.Collections.Concurrent;
using System.Text.Json;
using Koan.Data.Core;
using Koan.Jobs.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koan.Jobs;

/// <summary>
/// The one concern that claims, executes, settles, recalls, cancels, and advances jobs (JOBS-0005 §7). It talks only
/// to <see cref="IJobLedger"/> (storage-agnostic) and the <see cref="JobTypeRegistry"/> (bound handlers). Deterministic
/// by design: <see cref="DrainAsync"/> processes all currently-ready work to completion (the test driver); the worker
/// service drives the same loop continuously in production. All time comes from an injected <see cref="TimeProvider"/>.
/// </summary>
internal sealed class JobOrchestrator
{
    private readonly IJobLedger _ledger;
    private readonly JobTypeRegistry _registry;
    private readonly JobsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobOrchestrator> _logger;
    private readonly IServiceScopeFactory _scopes;
    private readonly IReadOnlyList<IJobPoolResolver> _poolResolvers;
    private readonly JobsContextPlan _contextPlan;

    private readonly string _owner = Guid.CreateVersion7().ToString("N");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lanes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unregisteredWorkTypeWarnings = new(StringComparer.Ordinal);
    private readonly JobMetricsRecorder _metrics;
    private DateTimeOffset _lastAssignAt = DateTimeOffset.MinValue;

    public JobOrchestrator(
        IJobLedger ledger, JobTypeRegistry registry, IOptions<JobsOptions> options,
        TimeProvider clock, ILogger<JobOrchestrator> logger, IServiceScopeFactory scopes,
        IEnumerable<IJobPoolResolver> poolResolvers, JobsContextPlan contextPlan)
    {
        _ledger = ledger;
        _registry = registry;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _scopes = scopes;
        _poolResolvers = poolResolvers.ToList();
        _contextPlan = contextPlan;
        _metrics = new JobMetricsRecorder(_options.MetricsEnabled, _owner, _clock);

        // PMC-056 corrective boundary: an inline host executes jobs on the caller — there is no fleet roster to
        // reserve against, so reservation dispatch cannot be honored and must refuse rather than silently degrade.
        if (_options.DispatchMode == JobDispatchMode.Reservation && _options.Mode == JobMode.Inline)
            throw new InvalidOperationException(
                "Koan Jobs cannot honor DispatchMode.Reservation because the host runs Mode.Inline — submission " +
                "executes synchronously on the caller and there is no fleet roster to reserve against. Remove the " +
                "DispatchMode override, or set Mode.Normal.");
    }

    /// <summary>Fold this node's accumulated throughput deltas into its internal metric shard rows (§20.2).
    /// No-op unless <see cref="JobsOptions.MetricsEnabled"/>. Driven by the worker on <c>MetricsFlushInterval</c>.</summary>
    public Task FlushMetricsAsync(CancellationToken ct = default) => _metrics.FlushAsync(ct);

    public string Owner => _owner;

    /// <summary>Process every currently-ready job to completion, including chain follow-ons that become ready.
    /// Jobs with a future <c>VisibleAt</c> (delayed/deferred) are left until the clock advances. Deterministic.</summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        var inflight = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await AssignReservationsAsync(ct);

                // Global cap: don't claim more work than this node can run concurrently.
                if (_options.WorkerConcurrency > 0 && inflight.Count >= _options.WorkerConcurrency)
                {
                    await Task.WhenAny(inflight);
                    inflight.RemoveAll(t => t.IsCompleted);
                    continue;
                }

                var now = _clock.GetUtcNow();
                var pools = await ResolvePoolContextsAsync(ct);
                var claimed = await ClaimBoundNextAsync(now, SaturatedLanes(), pools, ct);
                if (claimed is { } work)
                {
                    var (rec, binding) = work;
                    var policy = binding.ResolvePolicy(rec.Action, _options);
                    var sem = LaneSem(policy.Lane, policy.MaxConcurrency);
                    if (!sem.Wait(0)) await sem.WaitAsync(ct); // claim guarantees a slot; fallback is defensive
                    inflight.Add(ExecuteAndReleaseAsync(rec, binding, policy, sem, ct));
                    continue;
                }

                var settled = inflight.RemoveAll(t => t.IsCompleted);
                if (inflight.Count == 0)
                {
                    // A just-finished task may have appended a chain follow-on (SettleSuccess/Failure → Append). Don't
                    // conclude the drain is done until a fresh claim confirms nothing became ready: a successor enqueued
                    // in the window between the claim above and this check would otherwise be missed. The worker's poll
                    // loop hides this in production, but a single Drain on a higher-latency store (Mongo) exposes it.
                    if (settled > 0) continue;
                    break;
                }
                await Task.WhenAny(inflight);
                inflight.RemoveAll(t => t.IsCompleted);
            }
        }
        finally
        {
            await Task.WhenAll(inflight);
        }
    }

    /// <summary>Fire the cancellation token of a job currently running on this node (the durable marker is set by the caller).</summary>
    public void SignalCancel(string jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ExecuteAndReleaseAsync(JobRecord rec, JobTypeBinding binding, ResolvedActionPolicy policy, SemaphoreSlim lane, CancellationToken workerCt)
    {
        try { await ExecuteClaimedAsync(rec, binding, policy, workerCt); }
        catch (Exception ex) { _logger.LogError(ex, "Unhandled error settling job {JobId}", rec.Id); }
        finally { lane.Release(); }
    }

    /// <summary>Claim and execute exactly one ready job through the same path as <see cref="DrainAsync"/>, returning
    /// its execution result. Returns null when nothing is ready or when the work-item could not be loaded.
    /// Designed for in-process stage-handler integration testing via <c>JobStagePilot</c>.</summary>
    public async Task<JobRunResult?> ExecuteNextAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var pools = await ResolvePoolContextsAsync(ct);
        var claimed = await ClaimBoundNextAsync(now, SaturatedLanes(), pools, ct);
        if (claimed is not { } work) return null;

        var (rec, binding) = work;
        var policy = binding.ResolvePolicy(rec.Action, _options);
        var sem = LaneSem(policy.Lane, policy.MaxConcurrency);
        if (!sem.Wait(0)) await sem.WaitAsync(ct);
        JobContext? ctx;
        try
        {
            try { ctx = await ExecuteClaimedAsync(rec, binding, policy, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled error settling job {JobId}", rec.Id); ctx = null; }
        }
        finally { sem.Release(); }

        return ctx is null ? null
            : new JobRunResult(rec.Id, rec.WorkType, rec.Action, ctx.Signal, ctx.DeferUntil, ctx.NextAction, ctx.GateKeyOverride);
    }

    private async Task<JobContext?> ExecuteClaimedAsync(JobRecord rec, JobTypeBinding binding, ResolvedActionPolicy policy, CancellationToken workerCt)
    {
        // PMC-055 fencing: the owner captured at claim time is this execution's authority for every
        // guarded write (lease renewal, settlement). If it stops matching the stored row, this
        // execution has been reclaimed and every write bounces.
        var claimedOwner = rec.Owner ?? throw new InvalidOperationException("Claimed job has no owner.");
        // Restore the Koan context captured at submit BEFORE loading the (possibly tenant-scoped)
        // work-item, and keep them in scope across load + execute + settle (the conditional auto-save included) so
        // every tenant-scoped read/write runs in the submitted tenant. A restore failure (an unregistered axis, or
        // an unknown carrier format) is deterministic and non-retryable → dead-letter; the handler never runs
        // fail-open in a wrong/absent context. A null/empty bag explicitly suppresses every registered axis (the §1b
        // request guard owns the unscoped-write refusal under Closed; dev-fallback under Open).
        IDisposable ambientScope;
        IDisposable? dataScope = null;
        try
        {
            dataScope = JobDataRoute.From(rec).Restore();
            ambientScope = _contextPlan.RestoreForExecution(binding.ClrType, rec.AmbientCarrier);
        }
        catch (Exception ex)
        {
            dataScope?.Dispose();
            await SettleCarrierFailureAsync(rec, claimedOwner, ex);
            return null;
        }
        using var _data = dataScope;
        using var _ambient = ambientScope;

        object? workItem;
        try { workItem = await binding.Load(rec.WorkId, workerCt); }
        catch (Exception ex) { await SettleFailureAsync(rec, binding, policy, ex, claimedOwner); return null; }
        // Type-level triggers (TriggerAsync) use an ephemeral singleton that is never persisted; re-create it here.
        workItem ??= rec.WorkId == Infrastructure.Constants.Work.SingletonId ? binding.NewSingleton(rec.WorkId) : null;
        if (workItem is null) { await SettleFailureAsync(rec, binding, policy, new InvalidOperationException($"Work-item {rec.WorkType}/{rec.WorkId} not found."), claimedOwner); return null; }
        Notify(rec.WorkType, JobEventKind.Claimed, workItem, rec);

        var snapshot = Snapshot(workItem);   // for conditional auto-save (§17.1): only persist if the handler mutates it

        using var timeoutCts = policy.Timeout is { } to
            ? new CancellationTokenSource(to, _clock)
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(workerCt, timeoutCts.Token);
        _running[rec.Id] = linked;
        if (rec.CancelRequestedAt is not null) { try { linked.Cancel(); } catch (ObjectDisposedException) { } }

        using var scope = _scopes.CreateScope();
        Task PersistProgress(string jobId, double fraction, string? message, CancellationToken ct)
        {
            // Progress is written immediately for live observers. Keep the claimed snapshot in sync too, otherwise
            // the subsequent settle Update would overwrite that newer ledger row with its pre-handler values.
            rec.ProgressFraction = fraction;
            rec.ProgressMessage = message;
            return ProgressSink(jobId, fraction, message, ct);
        }

        var ctx = new JobContext(rec.Action, rec.Id, scope.ServiceProvider, _logger, ToState(rec), _clock, linked.Token, PersistProgress);
        // JOBS-0009 lease heartbeat: hold the claim alive while the handler runs. Losing the lease means another
        // claimant owns the row — execution cancels and NOTHING settles (any write here would clobber that owner).
        var lostLease = 0;
        try
        {
            using (_ = StartLeaseRenewal(rec, linked, () => Volatile.Write(ref lostLease, 1), workerCt))
            {
                await binding.Execute(workItem, ctx, linked.Token);
            }
            if (Volatile.Read(ref lostLease) != 0)
            {
                _logger.LogWarning("Koan.Jobs abandoned job {JobId}/{Action} after losing its lease; another claimant owns the row and no settle was written.", rec.Id, rec.Action);
                Notify(rec.WorkType, JobEventKind.Abandoned, workItem, rec);
                return ctx;
            }
            await SettleSuccessAsync(rec, workItem, binding, policy, ctx, snapshot, claimedOwner);
        }
        catch (RescheduleException rex)
        {
            var until = rex.Until ?? _clock.GetUtcNow() + (rex.After ?? TimeSpan.Zero);
            await ApplyDeferralAsync(rec, binding, policy, until, rex.Gate, rex.Gate ? rex.GateKey : null, hasOverride: rex.Gate, "reschedule-exception", claimedOwner);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref lostLease) != 0)
            {
                _logger.LogWarning("Koan.Jobs abandoned job {JobId}/{Action} after losing its lease; another claimant owns the row and no settle was written.", rec.Id, rec.Action);
                Notify(rec.WorkType, JobEventKind.Abandoned, workItem, rec);
            }
            else if (timeoutCts.IsCancellationRequested)
                await SettleFailureAsync(rec, binding, policy, new TimeoutException($"Action '{rec.Action}' exceeded its timeout."), claimedOwner);
            else if (await IsCancelMarkerSet(rec.Id))
                await SettleCancelledAsync(rec, claimedOwner);
            else
                await SettleShutdownAsync(rec, claimedOwner); // worker stopping — revert for reclaim
        }
        catch (Exception ex)
        {
            await SettleFailureAsync(rec, binding, policy, ex, claimedOwner);
        }
        finally
        {
            _running.TryRemove(rec.Id, out _);
        }
        return ctx;
    }

    /// <summary>JOBS-0009: renew the claim's lease on a derived cadence (LeaseDuration/3, 100ms floor) while the
    /// handler runs. A false renewal means another claimant owns the row: signal <paramref name="onLost"/>, cancel
    /// the execution token so the handler stops at its next checkpoint, and stop. The loop swallows its own
    /// cancellation; transient renewal-write failures are retried next tick — the reaper remains the bound.</summary>
    private LeaseHeartbeat StartLeaseRenewal(JobRecord rec, CancellationTokenSource execution, Action onLost, CancellationToken workerCt)
    {
        var duration = _options.LeaseDuration;
        if (duration <= TimeSpan.Zero || rec.Owner is not { } owner || rec.Id is null)
            return default;

        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(100).Ticks, duration.Ticks / 3));
        var cts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        var timer = new PeriodicTimer(interval, _clock);
        var jobId = rec.Id;
        _ = RenewLoopAsync();

        async Task RenewLoopAsync()
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    bool renewed;
                    var now = _clock.GetUtcNow();
                    try { renewed = await _ledger.TryRenewLease(jobId, owner, now + duration, now, cts.Token); }
                    catch (OperationCanceledException) when (cts.Token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Koan.Jobs could not write the lease renewal for job {JobId}; retrying next tick.", jobId);
                        continue;
                    }
                    if (renewed) continue;
                    _logger.LogWarning("Koan.Jobs lost the lease for job {JobId}; cancelling execution so it can abandon without settling.", jobId);
                    onLost();
                    try { execution.Cancel(); } catch (ObjectDisposedException) { }
                    break;
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                timer.Dispose();
                cts.Dispose();
            }
        }

        return new LeaseHeartbeat(cts);
    }

    private readonly struct LeaseHeartbeat : IDisposable
    {
        private readonly CancellationTokenSource? _cts;
        public LeaseHeartbeat(CancellationTokenSource? cts) => _cts = cts;
        public void Dispose()
        {
            if (_cts is null) return;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    // --- settle paths ---

    /// <summary>PMC-056: lifecycle events are pure projection — fired after the durable write, never
    /// on it, and a throwing observer can never affect settlement.</summary>
    private void Notify(string workType, JobEventKind kind, object? model, JobRecord rec, string? error = null)
    {
        try { JobEventRegistry.Publish(workType, kind, model, rec, error); }
        catch (Exception ex) { _logger.LogDebug(ex, "Job lifecycle observer for {WorkType} threw; treated as consumed.", rec.WorkType); }
    }

    /// <summary>PMC-055 fencing at the settle point: the write lands only while the stored row is still
    /// Running and owned by the node that claimed it. A lost settle means another node reclaimed the row —
    /// the revived zombie abandons without writing, and the new claimant's settlement stands.</summary>
    private async Task<bool> SettleOwnedAsync(JobRecord rec, string claimedOwner)
    {
        if (await _ledger.TrySettle(rec, claimedOwner, CancellationToken.None)) return true;
        _logger.LogWarning(
            "Koan.Jobs abandoned settling job {JobId}: the claim was lost to another node (reclaimed); no write was made.",
            rec.Id);
        return false;
    }

    private async Task SettleSuccessAsync(JobRecord rec, object workItem, JobTypeBinding binding, ResolvedActionPolicy policy, JobContext ctx, string? snapshot, string claimedOwner)
    {
        if (ctx.Signal is JobSignal.Reschedule or JobSignal.Backoff)
        {
            var until = ctx.DeferUntil ?? _clock.GetUtcNow();
            await ApplyDeferralAsync(rec, binding, policy, until, ctx.Signal == JobSignal.Backoff, ctx.GateKeyOverride, ctx.GateKeyOverrideSet, "cooperative-backoff", claimedOwner);
            return;
        }

        // Conditional auto-save (§17.1): persist the work-item only if the handler mutated the loaded reference.
        // A handler that worked on its own copy (and saved it) left this one clean — don't clobber its write.
        if (snapshot is null || Snapshot(workItem) != snapshot)
            await binding.Save(workItem, CancellationToken.None);

        var now = _clock.GetUtcNow();
        var next = ctx.Signal switch
        {
            JobSignal.ContinueWith => ctx.NextAction,
            JobSignal.StopChain => null,
            _ => binding.NextInChain(rec.Action),
        };

        // Settle-window cancel check: CancelWorkAsync writes CancelRequestedAt to the durable ledger while the
        // handler runs, but the orchestrator's rec clone was loaded at claim time (before that write). Re-read
        // BEFORE overwriting the record with Completed — otherwise our Update below erases the marker and the
        // subsequent check sees a clean record every time.
        var cancelledInSettleWindow = next is not null && await IsCancelMarkerSet(rec.Id);

        rec.Owner = null;
        rec.LeaseUntil = null;
        rec.ReservedFor = null;
        rec.ReservedUntil = null;
        rec.LastError = null;
        rec.DeferReason = null;
        rec.LastSettledAt = now;
        rec.ExpireAt = ExpiryAt(_options.ArchiveAfter, now);
        SetStatus(rec, JobStatus.Completed, now, "completed");
        if (!await SettleOwnedAsync(rec, claimedOwner)) return;
        _metrics.Record(rec.WorkType, JobStatus.Completed, now);
        Notify(rec.WorkType, JobEventKind.Completed, workItem, rec);

        if (next is not null && !cancelledInSettleWindow)
        {
            var nextPolicy = binding.ResolvePolicy(next, _options);
            // Chain stages inherit the gate key resolved at submit (the chain's gate pool is fixed — §18) and the
            // ambient carrier (ARCH-0100 §7): the successor is appended here by the orchestrator, NOT the
            // coordinator, so capture-at-submit never fires for it — propagate the parent's bag verbatim.
            var nextRec = JobRecordFactory.Create(binding, nextPolicy, workItem, rec.WorkId, next, now, null, rec.CorrelationId, rec.GateKey, rec.AmbientCarrier, JobDataRoute.From(rec));
            await _ledger.Append(nextRec, CancellationToken.None);
        }
    }

    private async Task SettleFailureAsync(JobRecord rec, JobTypeBinding binding, ResolvedActionPolicy policy, Exception ex, string claimedOwner)
    {
        var now = _clock.GetUtcNow();
        rec.Owner = null;
        rec.LeaseUntil = null;
        rec.ReservedFor = null;
        rec.ReservedUntil = null;
        rec.LastError = ex.Message;
        rec.LastSettledAt = now;

        if (rec.Attempt < policy.MaxAttempts)
        {
            rec.VisibleAt = now + RetryDelay(rec.Attempt);
            SetStatus(rec, JobStatus.Queued, now, $"retry {rec.Attempt}/{policy.MaxAttempts}: {ex.GetType().Name}");
            if (!await SettleOwnedAsync(rec, claimedOwner)) return;
            return;
        }

        rec.DeadReason = DeadReason.Poison.ToString();
        rec.ExpireAt = ExpiryAt(_options.FailedAfter, now);
        SetStatus(rec, JobStatus.Failed, now, $"failed after {policy.MaxAttempts} attempts: {ex.Message}");
        if (!await SettleOwnedAsync(rec, claimedOwner)) return;
        _metrics.Record(rec.WorkType, JobStatus.Failed, now);
        Notify(rec.WorkType, JobEventKind.Failed, null, rec, ex.Message);

        if (policy.OnFailure == OnFailure.Continue && binding.NextInChain(rec.Action) is { } next)
        {
            var wi = await binding.Load(rec.WorkId, CancellationToken.None);
            if (wi is not null)
            {
                var nextPolicy = binding.ResolvePolicy(next, _options);
                await _ledger.Append(JobRecordFactory.Create(binding, nextPolicy, wi, rec.WorkId, next, now, null, rec.CorrelationId, rec.GateKey, rec.AmbientCarrier, JobDataRoute.From(rec)), CancellationToken.None);
            }
        }
    }

    /// <summary>The captured Koan context could not be restored (unknown axis, invalid format/version, or insufficient
    /// trust). Deterministic — retrying would fail identically — so dead-letter immediately rather than run the handler
    /// in a wrong/absent context. The work item is never loaded.</summary>
    private async Task SettleCarrierFailureAsync(JobRecord rec, string claimedOwner, Exception ex)
    {
        var now = _clock.GetUtcNow();
        rec.Owner = null;
        rec.LeaseUntil = null;
        rec.ReservedFor = null;
        rec.ReservedUntil = null;
        rec.LastError = ex.Message;
        rec.LastSettledAt = now;
        rec.DeadReason = DeadReason.CarrierRestoreFailed.ToString();
        rec.ExpireAt = ExpiryAt(_options.FailedAfter, now);
        SetStatus(rec, JobStatus.Dead, now, $"ambient carrier restore failed: {ex.Message}");
        Notify(rec.WorkType, JobEventKind.DeadLettered, null, rec, ex.Message);
        if (!await SettleOwnedAsync(rec, claimedOwner)) return;
        _metrics.Record(rec.WorkType, JobStatus.Dead, now);
    }

    private async Task SettleUnregisteredWorkTypeAsync(JobRecord rec, string claimedOwner)
    {
        var now = _clock.GetUtcNow();
        rec.Owner = null;
        rec.LeaseUntil = null;
        rec.ReservedFor = null;
        rec.ReservedUntil = null;
        rec.LastError = $"No job binding is registered for work type '{rec.WorkType}'.";
        rec.LastSettledAt = now;
        rec.DeadReason = DeadReason.UnregisteredWorkType.ToString();
        rec.ExpireAt = ExpiryAt(_options.FailedAfter, now);
        SetStatus(rec, JobStatus.Dead, now, $"unregistered work type: {rec.WorkType}");
        if (!await SettleOwnedAsync(rec, claimedOwner)) return;
        _metrics.Record(rec.WorkType, JobStatus.Dead, now);
        if (_unregisteredWorkTypeWarnings.TryAdd(rec.WorkType, 0))
            _logger.LogWarning(
                "Dead-lettered jobs for unregistered work type {WorkType}; the first observed job was {JobId}",
                rec.WorkType,
                rec.Id);
        else
            _logger.LogDebug(
                "Dead-lettered job {JobId} because work type {WorkType} is not registered",
                rec.Id,
                rec.WorkType);
    }

    private async Task ApplyDeferralAsync(JobRecord rec, JobTypeBinding binding, ResolvedActionPolicy policy,
        DateTimeOffset until, bool gate, string? gateKeyOverride, bool hasOverride, string reason, string claimedOwner)
    {
        var now = _clock.GetUtcNow();
        // reschedule does NOT consume a retry attempt: undo the claim-time increment.
        rec.Attempt = Math.Max(0, rec.Attempt - 1);
        rec.Reschedules++;

        var deadlineHit = rec.Deadline is { } dl && now >= dl;
        var maxHit = policy.MaxReschedules >= 0 && rec.Reschedules > policy.MaxReschedules;
        rec.Owner = null;
        rec.LeaseUntil = null;
        rec.ReservedFor = null;
        rec.ReservedUntil = null;
        rec.LastSettledAt = now;

        if (deadlineHit || maxHit)
        {
            rec.DeadReason = DeadReason.PerpetuallyDeferred.ToString();
            rec.ExpireAt = ExpiryAt(_options.FailedAfter, now);
            SetStatus(rec, JobStatus.Dead, now, deadlineHit ? "deadline exceeded" : "max reschedules exceeded");
        Notify(rec.WorkType, JobEventKind.DeadLettered, null, rec);
            if (!await SettleOwnedAsync(rec, claimedOwner)) return;
            _metrics.Record(rec.WorkType, JobStatus.Dead, now);
            return;
        }

        rec.DeferReason = reason;
        rec.VisibleAt = ApplyJitter(until);
        SetStatus(rec, JobStatus.Queued, now, $"deferred to {rec.VisibleAt:O} ({reason})");
        Notify(rec.WorkType, JobEventKind.Rescheduled, null, rec);
        if (!await SettleOwnedAsync(rec, claimedOwner)) return;

        if (gate)
        {
            var key = hasOverride ? (gateKeyOverride ?? rec.GateKey) : rec.GateKey;
            if (!string.IsNullOrEmpty(key))
                await _ledger.SetGate(key!, until, reason, CancellationToken.None);
        }
    }

    private async Task SettleCancelledAsync(JobRecord rec, string claimedOwner)
    {
        var now = _clock.GetUtcNow();
        rec.Owner = null;
        rec.LeaseUntil = null;
        rec.ReservedFor = null;
        rec.ReservedUntil = null;
        rec.LastSettledAt = now;
        rec.ExpireAt = ExpiryAt(_options.ArchiveAfter, now);
        SetStatus(rec, JobStatus.Cancelled, now, "cancelled");
        Notify(rec.WorkType, JobEventKind.Cancelled, null, rec);
        if (!await SettleOwnedAsync(rec, claimedOwner)) return;
        _metrics.Record(rec.WorkType, JobStatus.Cancelled, now);
    }

    private async Task SettleShutdownAsync(JobRecord rec, string claimedOwner)
    {
        var now = _clock.GetUtcNow();
        rec.Attempt = Math.Max(0, rec.Attempt - 1); // worker stopped before completing; don't penalize
        rec.Owner = null;
        rec.LeaseUntil = null;
        rec.ReservedFor = null;
        rec.ReservedUntil = null;
        rec.VisibleAt = now;
        SetStatus(rec, JobStatus.Queued, now, "requeued (worker shutdown)");
        if (!await SettleOwnedAsync(rec, claimedOwner)) return;
    }

    /// <summary>Archival sweep (§19.3): purge Completed/Cancelled past <c>ArchiveAfter</c>, Failed/Dead past
    /// <c>FailedAfter</c>, then trim each work-type's terminal rows to <c>RetainPerWorkType</c>. Each is independently
    /// gated; all off → no-op. Returns the total rows removed.</summary>
    public async Task<int> ArchiveAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var purged = 0;
        if (_options.ArchiveAfter > TimeSpan.Zero)
            purged += await _ledger.PurgeArchivable(now - _options.ArchiveAfter, ct);
        if (_options.FailedAfter > TimeSpan.Zero)
            purged += await _ledger.PurgeFailed(now - _options.FailedAfter, ct);
        if (_options.RetainPerWorkType > 0)
            foreach (var binding in _registry.All)
                purged += await _ledger.TrimTerminal(binding.WorkType, _options.RetainPerWorkType, ct);

        // §20.2 metrics rollup: bucket-age retention of the node-sharded JobMetric rows.
        if (_metrics.Enabled && _options.MetricsRetention > TimeSpan.Zero)
            purged += await _metrics.PurgeAsync(now - _options.MetricsRetention, ct);

        // §19.4 self-reporting guardrail: name the job-per-row anti-pattern when a work-type's active set is huge.
        if (_options.JobPerRowWarnThreshold > 0)
            foreach (var binding in _registry.All)
            {
                var active = await _ledger.CountActive(binding.WorkType, ct);
                if (active > _options.JobPerRowWarnThreshold)
                    _logger.LogWarning(
                        "[Koan.Jobs] WorkType '{WorkType}' has {Active:N0} active rows (> {Threshold:N0}) — this looks " +
                        "like job-per-row. Window the source with a cursor-conveyor (jobs-howto §8.1; JOBS-0005 §19.4).",
                        binding.WorkType, active, _options.JobPerRowWarnThreshold);
            }
        return purged;
    }

    /// <summary>Reclaim jobs whose lease lapsed (reaper sweep): revert Running → Queued for re-dispatch.</summary>
    public async Task ReapAsync(CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        var now = _clock.GetUtcNow();
        foreach (var stuck in await _ledger.Stuck(now, ct))
        {
            stuck.Owner = null;
            stuck.LeaseUntil = null;
            stuck.ReservedFor = null;
            stuck.ReservedUntil = null;
            stuck.VisibleAt = now;
            SetStatus(stuck, JobStatus.Queued, now, "reclaimed (lease lapsed)");
            Notify(stuck.WorkType, JobEventKind.Stalled, null, stuck);
            await _ledger.Update(stuck, ct);
        }

        // PMC-055: a worker confirmed dead by roster silence forfeits its running jobs immediately —
        // ahead of lease lapse. Safe because the revived node's renewal and settlement are
        // ownership-guarded: it abandons without writing. This node never death-reclaims its own
        // in-flight jobs (its heartbeat governs them; the lease is the only bound).
        if (_options.ReclaimFromConfirmedDead && _options.WorkerDeathTimeout > TimeSpan.Zero)
        {
            var cutoff = now - _options.WorkerDeathTimeout;
            var alive = (await WorkerNode.Query(w => w.LastSeenAt >= cutoff, ct))
                .Select(w => w.Id)
                .ToHashSet(StringComparer.Ordinal);
            alive.Add(_owner);

            foreach (var orphan in (await _ledger.Running(ct)).Where(r => r.Owner is not null && !alive.Contains(r.Owner!)))
            {
                orphan.Owner = null;
                orphan.LeaseUntil = null;
                orphan.ReservedFor = null;
                orphan.ReservedUntil = null;
                orphan.VisibleAt = now;
                SetStatus(orphan, JobStatus.Queued, now, "reclaimed (worker confirmed dead)");
                Notify(orphan.WorkType, JobEventKind.Stalled, null, orphan);
                await _ledger.Update(orphan, ct);
            }

            foreach (var stale in await WorkerNode.Query(w => w.LastSeenAt < cutoff, ct))
                await stale.Remove(ct);
        }
    }

    /// <summary>PMC-055: register this worker in the fleet roster, or heartbeat its existing entry.
    /// Self-throttling to <c>WorkerHeartbeatInterval</c>; safe to call every loop iteration.</summary>
    public async Task BeatAsync(CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        var now = _clock.GetUtcNow();
        var node = await WorkerNode.Get(_owner, ct);
        if (node is null)
        {
            await WorkerNode.Upsert(new WorkerNode { Id = _owner, StartedAt = now, LastSeenAt = now, Machine = Environment.MachineName }, ct);
            return;
        }

        if (now - node.LastSeenAt >= _options.WorkerHeartbeatInterval)
        {
            node.LastSeenAt = now;
            node.Machine = Environment.MachineName;
            await WorkerNode.Upsert(node, ct);
        }
    }

    /// <summary>PMC-055: graceful departure — peers see the resignation immediately instead of
    /// waiting out the death timeout on a planned shutdown.</summary>
    public async Task ResignAsync(CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        var node = await WorkerNode.Get(_owner, ct);
        if (node is not null)
            await node.Remove(ct);
    }

    /// <summary>PMC-056 reservation dispatch ("the jar hands a cookie to a named hand"). In
    /// <see cref="JobDispatchMode.Reservation"/> only, the senior live roster node — derived from the shared roster,
    /// no election protocol — releases reservations whose hand died or whose stamp lapsed, then assigns the oldest
    /// due unreserved work onto the least-loaded live hands, bounded by fleet concurrency. Self-throttled to
    /// <c>WorkerHeartbeatInterval</c>; a no-op in <see cref="JobDispatchMode.Pull"/>.
    ///
    /// <para>Safety: assignment is ledger-verifiable dispatch metadata on the row itself (<see cref="JobRecord.ReservedFor"/>);
    /// stamps are guarded writes that apply only while the row is Queued and unreserved; claims still CAS and respect a
    /// fresh foreign reservation; releases re-verify the stored row immediately before writing. A lost release race is the
    /// same at-least-once window the optimistic claim floor already carries. No transition rows and no events: assignment is
    /// not lifecycle.</para></summary>
    public async Task AssignReservationsAsync(CancellationToken ct = default)
    {
        using var storageScope = JobsStorageScope.Enter();
        if (_options.DispatchMode != JobDispatchMode.Reservation) return;
        if (_options.WorkerDeathTimeout <= TimeSpan.Zero) return;

        var now = _clock.GetUtcNow();
        if (now - _lastAssignAt < _options.WorkerHeartbeatInterval) return;
        _lastAssignAt = now;

        // The hands: fresh roster entries plus me (my heartbeat may be pending; I am alive by definition).
        var cutoff = now - _options.WorkerDeathTimeout;
        var alive = (await WorkerNode.Query(w => w.LastSeenAt >= cutoff, ct))
            .Select(w => w.Id)
            .Append(_owner)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var aliveSet = alive.ToHashSet(StringComparer.Ordinal);

        // Seniority before action: exactly one node coordinates per pass. Oldest join wins; ties break by owner id.
        // Concurrent-seniority windows (between heartbeats) are absorbed by the guarded TryReserve stamps.
        var nodes = await WorkerNode.Query(w => w.LastSeenAt >= cutoff, ct);
        var seniorId = nodes
            .OrderBy(n => n.StartedAt)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => n.Id)
            .DefaultIfEmpty(_owner)     // empty roster → I coordinate (single-node host or pending beats)
            .First();
        if (!string.Equals(seniorId, _owner, StringComparison.Ordinal)) return;

        // 1. Release: lapsed stamps return to the shelf; so do confirmed-dead hands' assignments.
        foreach (var reserved in await _ledger.Reservations(ct))
        {
            var handLive = reserved.ReservedFor is { } hand && aliveSet.Contains(hand);
            var lapsed = reserved.ReservedUntil is { } until && until < now;
            if (handLive && !lapsed) continue;

            // Re-verify against the stored row so we never overwrite a claim that landed since the read.
            var stored = await _ledger.Get(reserved.Id!, ct);
            if (stored is not { Status: JobStatus.Queued }
                || !string.Equals(stored.ReservedFor, reserved.ReservedFor, StringComparison.Ordinal)) continue;
            _logger.LogDebug("Koan.Jobs released job {JobId} from reservation for {Hand} ({Reason}).",
                stored.Id, stored.ReservedFor, lapsed ? "reservation lapsed" : "hand confirmed dead");
            stored.ReservedFor = null;
            stored.ReservedUntil = null;
            await _ledger.Update(stored, ct);
        }

        // 2. Assign: oldest due first, least-loaded hand wins ties by roster order. Outstanding cookies cap at
        //    fleet capacity (hands × WorkerConcurrency) and one scan batch per pass.
        var load = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var runningRow in await _ledger.Running(ct))
            if (runningRow.Owner is { } o) load[o] = load.GetValueOrDefault(o) + 1;
        foreach (var res in await _ledger.Reservations(ct))
            if (res.ReservedFor is { } hand && aliveSet.Contains(hand)) load[hand] = load.GetValueOrDefault(hand) + 1;

        var capacity = alive.Sum(h => Math.Max(0, _options.WorkerConcurrency - load.GetValueOrDefault(h)));
        if (capacity <= 0) return;
        var limit = Math.Min(capacity, Math.Max(1, _options.ClaimScanBatch));
        foreach (var candidate in await _ledger.ReservationCandidates(now, limit, ct))
        {
            if (candidate.Id is null) continue;
            var hand = alive
                .Where(h => load.GetValueOrDefault(h) < _options.WorkerConcurrency)
                .OrderBy(h => load.GetValueOrDefault(h))
                .ThenBy(h => h, StringComparer.Ordinal)
                .FirstOrDefault();
            if (hand is null) break;   // every hand is at capacity
            if (await _ledger.TryReserve(candidate.Id, hand, now + _options.ReservationDuration, now, ct))
                load[hand] = load.GetValueOrDefault(hand) + 1;
        }
    }

    // --- helpers ---

    /// <summary>
    /// Claims the next executable row. Durable rows can outlive the application code that registered their work type;
    /// those rows are deterministic poison, so the orchestrator terminalizes them and keeps looking instead of leaking
    /// a Running lease or making a caller mistake a retired row for an empty queue.
    /// </summary>
    private async Task<(JobRecord Record, JobTypeBinding Binding)?> ClaimBoundNextAsync(
        DateTimeOffset now,
        IReadOnlyCollection<string> saturatedLanes,
        IReadOnlyDictionary<string, PoolDispatchContext>? pools,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var rec = await _ledger.ClaimNext(
                _owner,
                now,
                now + _options.LeaseDuration,
                saturatedLanes,
                ct,
                pools);
            if (rec is null) return null;

            var binding = _registry.Get(rec.WorkType);
            if (binding is not null) return (rec, binding);

            await SettleUnregisteredWorkTypeAsync(rec, rec.Owner!);
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<string, PoolDispatchContext>?> ResolvePoolContextsAsync(CancellationToken ct)
    {
        if (_poolResolvers.Count == 0) return null;
        var dict = new Dictionary<string, PoolDispatchContext>(_poolResolvers.Count, StringComparer.Ordinal);
        foreach (var resolver in _poolResolvers)
        {
            var members = await resolver.GetMembersAsync(ct);
            dict[resolver.PoolName] = new PoolDispatchContext(resolver.PoolName, members, resolver.CapacityPerMember);
        }
        return dict;
    }

    private IReadOnlyCollection<string> SaturatedLanes()
    {
        List<string>? saturated = null;
        foreach (var kv in _lanes)
            if (kv.Value.CurrentCount == 0)
                (saturated ??= new()).Add(kv.Key);
        return (IReadOnlyCollection<string>?)saturated ?? Array.Empty<string>();
    }

    private SemaphoreSlim LaneSem(string lane, int maxConcurrency)
        => _lanes.GetOrAdd(lane, _ => new SemaphoreSlim(maxConcurrency, maxConcurrency));

    private Task ProgressSink(string jobId, double fraction, string? message, CancellationToken ct)
        => _ledger.Progress(jobId, fraction, message, ct);

    private async Task<bool> IsCancelMarkerSet(string jobId)
        => (await _ledger.Get(jobId, CancellationToken.None))?.CancelRequestedAt is not null;

    private DateTimeOffset ApplyJitter(DateTimeOffset releaseAt)
    {
        if (_options.RescheduleJitter <= TimeSpan.Zero) return releaseAt;
        var ms = Random.Shared.Next(0, (int)Math.Max(1, _options.RescheduleJitter.TotalMilliseconds));
        return releaseAt + TimeSpan.FromMilliseconds(ms);
    }

    private TimeSpan RetryDelay(int attempt)
    {
        var factor = Math.Pow(2, Math.Min(attempt - 1, 16));
        var ticks = (long)Math.Min(_options.RetryBaseDelay.Ticks * factor, TimeSpan.FromMinutes(5).Ticks);
        return TimeSpan.FromTicks(Math.Max(ticks, 0));
    }

    private static JobState ToState(JobRecord r) => new(
        r.Status, r.Action, r.Attempt, r.Reschedules, r.FirstSubmittedAt, r.LastSettledAt,
        r.LastError, r.DeferReason, r.Deadline, r.CorrelationId);

    private static void SetStatus(JobRecord r, JobStatus to, DateTimeOffset at, string? note)
    {
        r.Transitions.Add(new JobTransition { At = at, From = r.Status, To = to, Note = note });
        r.Status = to;
    }

    /// <summary>Absolute expiry for a terminal row (§20.4): <c>now + window</c>, or null when the window is disabled
    /// (≤ 0) so the row is retained indefinitely.</summary>
    private static DateTimeOffset? ExpiryAt(TimeSpan window, DateTimeOffset now) => window > TimeSpan.Zero ? now + window : null;

    /// <summary>Deterministic snapshot of a work-item's serialized state for conditional auto-save (§17.1). The
    /// comparison is internal (load vs. settle, same serializer), so it needs only determinism + public-state
    /// coverage. Returns null when the entity can't be serialized (cyclic/exotic) → the caller degrades to
    /// always-save, never failing the job over a snapshot.</summary>
    private static string? Snapshot(object workItem)
    {
        try { return JsonSerializer.Serialize(workItem, workItem.GetType()); }
        catch { return null; }
    }
}
