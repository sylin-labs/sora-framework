using System.Collections.Concurrent;
using Koan.Data.Core.Model;
using Koan.Jobs;

namespace Koan.Jobs.TestKit;

/// <summary>Action/stage tokens for the test pipeline.</summary>
public static class Stage
{
    public const string Fetch = nameof(Fetch);
    public const string Parse = nameof(Parse);
    public const string Mint = nameof(Mint);
    public const string Publish = nameof(Publish);
    public const string PrepareToFetch = nameof(PrepareToFetch);
}

/// <summary>Single-action job: ignores ctx.Action, mutates the work-item.</summary>
public sealed class GreetJob : Entity<GreetJob>, IKoanJob<GreetJob>
{
    public string Name { get; set; } = "";
    public string? Greeting { get; set; }
    public static int Executions;
    public static string? LastGateKey;

    public static Task Execute(GreetJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        LastGateKey = ctx.State.GateKey;
        job.Greeting = $"Hello, {job.Name}";
        return Task.CompletedTask;
    }

    public static void Reset() { Executions = 0; LastGateKey = null; }
}

/// <summary>Reports one durable progress value so settlement cannot erase the observer-visible update.</summary>
public sealed class ProgressJob : Entity<ProgressJob>, IKoanJob<ProgressJob>
{
    public static Task Execute(ProgressJob job, JobContext ctx, CancellationToken ct)
        => ctx.Progress(0.75, "three quarters");
}

/// <summary>Multi-action linear pipeline (declared chain). Carries saga state across stages.</summary>
[JobChain(Stage.Fetch, Stage.Parse, Stage.Mint, Stage.Publish)]
public sealed class Pipeline : Entity<Pipeline>, IKoanJob<Pipeline>
{
    public string? Fetched { get; set; }
    public string? Parsed { get; set; }
    public bool Minted { get; set; }
    public bool Published { get; set; }
    public List<string> Trail { get; set; } = new();

    public static Task Execute(Pipeline job, JobContext ctx, CancellationToken ct)
    {
        job.Trail.Add(ctx.Action);
        switch (ctx.Action)
        {
            case Stage.Fetch: job.Fetched = "raw"; break;
            case Stage.Parse: job.Parsed = (job.Fetched ?? "") + "-parsed"; break;
            case Stage.Mint: job.Minted = true; break;
            case Stage.Publish: job.Published = true; break;
        }
        return Task.CompletedTask;
    }
}

/// <summary>Fails until its attempt count reaches <see cref="SucceedAtAttempt"/> (retry/backoff/poison testing).</summary>
[JobAction(Action, MaxAttempts = 3)]
public sealed class FlakyJob : Entity<FlakyJob>, IKoanJob<FlakyJob>
{
    public const string Action = "work";
    public static int Executions;
    public static int SucceedAtAttempt = 1;

    public static Task Execute(FlakyJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        if (ctx.State.Attempt < SucceedAtAttempt) throw new InvalidOperationException("transient boom");
        return Task.CompletedTask;
    }

    public static void Reset() { Executions = 0; SucceedAtAttempt = 1; }
}

/// <summary>Cooperatively reschedules until it has been deferred <see cref="RescheduleUntil"/> times.</summary>
public sealed class RescheduleJob : Entity<RescheduleJob>, IKoanJob<RescheduleJob>
{
    public static int Executions;
    public static int RescheduleUntil = 2;

    public static Task Execute(RescheduleJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        if (ctx.State.Reschedules < RescheduleUntil) ctx.Reschedule(TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public static void Reset() { Executions = 0; RescheduleUntil = 2; }
}

/// <summary>Contends for a shared host gate; on the first run it "gets a 429" and backs the whole host off.</summary>
[JobGate(nameof(Host))]
public sealed class GatedJob : Entity<GatedJob>, IKoanJob<GatedJob>
{
    public string Host { get; set; } = "api";
    public static int Executions;
    public static bool Trip429;

    /// <summary>Per-execution observations of ctx.State read AFTER the control verbs ran, keyed by work item (AE-18).</summary>
    public static readonly ConcurrentQueue<(string WorkId, string GateKey)> ClaimedGates = new();

    /// <summary>When set, the 429 backoff overrides the gate key with this explicit key.</summary>
    public static string? OverrideKey;

    public static Task Execute(GatedJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        if (Trip429)
        {
            Trip429 = false;
            ctx.Backoff(TimeSpan.FromMinutes(5), OverrideKey);
        }
        // Read after the verb: the snapshot is unchanged even when this execution backed off.
        ClaimedGates.Enqueue((job.Id, ctx.State.GateKey ?? ""));
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        Executions = 0;
        Trip429 = false;
        OverrideKey = null;
        ClaimedGates.Clear();
    }
}

/// <summary>Coalesces concurrent/duplicate submits by a declared key.</summary>
[JobIdempotent(nameof(Key))]
public sealed class DedupeJob : Entity<DedupeJob>, IKoanJob<DedupeJob>
{
    public string Key { get; set; } = "";
    public static int Executions;

    public static Task Execute(DedupeJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }

    public static void Reset() => Executions = 0;
}

/// <summary>Lane-capped job that records peak observed concurrency.</summary>
[JobAction(Action, Lane = "slow", MaxConcurrency = 2)]
public sealed class SlowJob : Entity<SlowJob>, IKoanJob<SlowJob>
{
    public const string Action = "slow";
    public static int Current;
    public static int Peak;
    public static TimeSpan Hold = TimeSpan.FromMilliseconds(40);

    public static async Task Execute(SlowJob job, JobContext ctx, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref Current);
        InterlockedMax(ref Peak, now);
        try { await Task.Delay(Hold, ct); }
        finally { Interlocked.Decrement(ref Current); }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do { seen = target; if (value <= seen) return; }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    public static void Reset() { Current = 0; Peak = 0; }
}

/// <summary>Cancellation/timeout: optionally waits forever on the token.</summary>
[JobAction(Action, Timeout = "00:00:30", MaxAttempts = 1)]
public sealed class WaitJob : Entity<WaitJob>, IKoanJob<WaitJob>
{
    public const string Action = "wait";
    public static int Executions;
    public static int Cancellations;

    public static async Task Execute(WaitJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        catch (OperationCanceledException) { Interlocked.Increment(ref Cancellations); throw; }
    }

    public static void Reset() { Executions = 0; Cancellations = 0; }
}

/// <summary>Chain steps for the on-failure fixtures.</summary>
public static class Step
{
    public const string One = "one";
    public const string Two = "two";
}

/// <summary>Chain job that can stop or branch from its first step.</summary>
[JobChain("a", "b", "c")]
public sealed class BranchJob : Entity<BranchJob>, IKoanJob<BranchJob>
{
    public List<string> Trail { get; set; } = new();
    public static string Mode = "chain";

    public static Task Execute(BranchJob job, JobContext ctx, CancellationToken ct)
    {
        job.Trail.Add(ctx.Action);
        if (ctx.Action == "a")
        {
            if (Mode == "stop") ctx.StopChain();
            else if (Mode == "branch") ctx.ContinueWith("z");
        }
        return Task.CompletedTask;
    }

    public static void Reset() => Mode = "chain";
}

/// <summary>First step always fails; <c>OnFailure=Continue</c> means the chain still advances to step two.</summary>
[JobChain(Step.One, Step.Two)]
[JobAction(Step.One, MaxAttempts = 1, OnFailure = OnFailure.Continue)]
public sealed class ContinueChain : Entity<ContinueChain>, IKoanJob<ContinueChain>
{
    public bool TwoRan { get; set; }

    public static Task Execute(ContinueChain job, JobContext ctx, CancellationToken ct)
    {
        if (ctx.Action == Step.One) throw new InvalidOperationException("step one fails");
        job.TwoRan = true;
        return Task.CompletedTask;
    }
}

/// <summary>First step always fails; default <c>OnFailure=Abort</c> means step two never runs.</summary>
[JobChain(Step.One, Step.Two)]
[JobAction(Step.One, MaxAttempts = 1)]
public sealed class AbortChain : Entity<AbortChain>, IKoanJob<AbortChain>
{
    public bool TwoRan { get; set; }

    public static Task Execute(AbortChain job, JobContext ctx, CancellationToken ct)
    {
        if (ctx.Action == Step.One) throw new InvalidOperationException("step one fails");
        job.TwoRan = true;
        return Task.CompletedTask;
    }
}

/// <summary>Type-level trigger target: records the action it ran (no caller instance — runs against a singleton).</summary>
public sealed class TickJob : Entity<TickJob>, IKoanJob<TickJob>
{
    public static int Executions;
    public static string? LastAction;

    public static Task Execute(TickJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        LastAction = ctx.Action;
        return Task.CompletedTask;
    }

    public static void Reset() { Executions = 0; LastAction = null; }
}

/// <summary>Idempotent singleton sweep: overlapping type-level triggers coalesce onto one in-flight tick.</summary>
[JobIdempotent(nameof(Key))]
public sealed class SweepTick : Entity<SweepTick>, IKoanJob<SweepTick>
{
    public string Key => "sweep-singleton";
    public static int Executions;

    public static Task Execute(SweepTick job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }

    public static void Reset() => Executions = 0;
}

/// <summary>Interval-scheduled reconcile: the scheduler submits a fresh job on its cadence.</summary>
[JobAction(Stage.PrepareToFetch, Schedule = "00:10:00")]
public sealed class Reconciled : Entity<Reconciled>, IKoanJob<Reconciled>
{
    public static int Executions;

    public static Task Execute(Reconciled job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }

    public static void Reset() => Executions = 0;
}

/// <summary>A <c>@boot</c> action — submitted once at startup.</summary>
[JobAction("init", Schedule = "@boot")]
public sealed class BootOnce : Entity<BootOnce>, IKoanJob<BootOnce>
{
    public static int Executions;

    public static Task Execute(BootOnce job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }

    public static void Reset() => Executions = 0;
}

/// <summary>A <c>@continuous</c> action — submitted on every scheduler tick.</summary>
[JobAction("beat", Schedule = "@continuous")]
public sealed class Heartbeat : Entity<Heartbeat>, IKoanJob<Heartbeat>
{
    public static int Executions;

    public static Task Execute(Heartbeat job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }

    public static void Reset() => Executions = 0;
}

/// <summary>A cron action — daily at 02:00 UTC.</summary>
[JobAction("nightly", Schedule = "0 2 * * *")]
public sealed class NightlyJob : Entity<NightlyJob>, IKoanJob<NightlyJob>
{
    public static int Executions;

    public static Task Execute(NightlyJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }

    public static void Reset() => Executions = 0;
}

/// <summary>Upstream pipeline lane (JOBS-0008 fairness specs): declares Lane="crawl" so the durable tier enumerates it.</summary>
[JobAction(Crawl, Lane = "crawl")]
public sealed class CrawlJob : Entity<CrawlJob>, IKoanJob<CrawlJob>
{
    public const string Crawl = nameof(Crawl);
    public static int Executions;
    public static Task Execute(CrawlJob job, JobContext ctx, CancellationToken ct)
    { Interlocked.Increment(ref Executions); return Task.CompletedTask; }
    public static void Reset() => Executions = 0;
}

/// <summary>Downstream pipeline lane (JOBS-0008 fairness specs): declares Lane="translation".</summary>
[JobAction(Translate, Lane = "translation")]
public sealed class TranslationJob : Entity<TranslationJob>, IKoanJob<TranslationJob>
{
    public const string Translate = nameof(Translate);
    public static int Executions;
    public static Task Execute(TranslationJob job, JobContext ctx, CancellationToken ct)
    { Interlocked.Increment(ref Executions); return Task.CompletedTask; }
    public static void Reset() => Executions = 0;
}

internal static class Probe
{
    public static void Max(ref int target, int value)
    {
        int seen;
        do { seen = target; if (value <= seen) return; }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}

/// <summary>Two-stage chain where step "a" can be held open by the test, ignoring the cancellation token, to
/// simulate a cancel landing in the settle window after the handler has already returned successfully.</summary>
[JobChain("a", "b")]
public sealed class ChainCancelRaceJob : Entity<ChainCancelRaceJob>, IKoanJob<ChainCancelRaceJob>
{
    public bool StepBRan { get; set; }
    public static int StepAExecutions;
    public static TaskCompletionSource? Unblock;

    public static async Task Execute(ChainCancelRaceJob job, JobContext ctx, CancellationToken ct)
    {
        if (ctx.Action == "a")
        {
            Interlocked.Increment(ref StepAExecutions);
            // Deliberately ignore ct: the handler completes normally even when the linked token is cancelled,
            // reproducing the settle-time window where the cancel marker is set AFTER Execute returns.
            if (Unblock is not null) await Unblock.Task;
        }
        else job.StepBRan = true;
    }

    public static void Reset() { StepAExecutions = 0; Unblock = null; }
}

/// <summary>Conditional auto-save footgun (§17.1): the handler edits a SECOND copy and saves it, never touching
/// the passed reference. Naive always-save would clobber this with the unmutated reference.</summary>
public sealed class SelfSaveJob : Entity<SelfSaveJob>, IKoanJob<SelfSaveJob>
{
    public const string Written = "handler-wrote-this";
    public string Result { get; set; } = "";

    public static async Task Execute(SelfSaveJob job, JobContext ctx, CancellationToken ct)
    {
        var fresh = await SelfSaveJob.Get(job.Id, ct);
        fresh!.Result = Written;
        await SelfSaveJob.Upsert(fresh, ct);
        // job.Result stays "" — the framework must NOT auto-save over fresh's write.
    }
}

/// <summary>Two actions on one entity, each in its own lane (so only per-entity serialization can hold them
/// apart). Records peak concurrency to prove exclusivity is the default (§17.2).</summary>
public sealed class ExclusiveJob : Entity<ExclusiveJob>, IKoanJob<ExclusiveJob>
{
    public const string ActionA = "a";
    public const string ActionB = "b";
    public static int Current;
    public static int Peak;

    public static async Task Execute(ExclusiveJob job, JobContext ctx, CancellationToken ct)
    {
        Probe.Max(ref Peak, Interlocked.Increment(ref Current));
        try { await Task.Delay(TimeSpan.FromMilliseconds(60), ct); }
        finally { Interlocked.Decrement(ref Current); }
    }

    public static void Reset() { Current = 0; Peak = 0; }
}

/// <summary>Same shape as <see cref="ExclusiveJob"/> but opts out of per-entity serialization via [ParallelSafe].</summary>
[ParallelSafe]
public sealed class ParallelJob : Entity<ParallelJob>, IKoanJob<ParallelJob>
{
    public const string ActionA = "a";
    public const string ActionB = "b";
    public static int Current;
    public static int Peak;

    public static async Task Execute(ParallelJob job, JobContext ctx, CancellationToken ct)
    {
        Probe.Max(ref Peak, Interlocked.Increment(ref Current));
        try { await Task.Delay(TimeSpan.FromMilliseconds(60), ct); }
        finally { Interlocked.Decrement(ref Current); }
    }

    public static void Reset() { Current = 0; Peak = 0; }
}

/// <summary>A related entity carrying the gate identity (the host) the work-item itself does not declare.</summary>
public sealed class RemoteTarget : Entity<RemoteTarget>
{
    public string Host { get; set; } = "";
}

/// <summary>Runtime-resolved gate key (§18): the host isn't on the work-item — the resolver loads the related
/// <see cref="RemoteTarget"/> at submit. One host's 503 (<c>ctx.Backoff</c>) defers only same-host siblings.</summary>
[JobGate(nameof(ResolveHostGate))]
public sealed class RemoteFetch : Entity<RemoteFetch>, IKoanJob<RemoteFetch>
{
    public const string Refresh = nameof(Refresh);
    public string TargetId { get; set; } = "";
    public static readonly ConcurrentBag<string> Ran = new();
    public static int Executed;
    public static string? Throttled;     // host that currently returns 503 (set by the test)

    public async Task<string?> ResolveHostGate(IServiceProvider sp, CancellationToken ct)
    {
        var target = await RemoteTarget.Get(TargetId, ct);   // async, off-entity resolution
        return target is null ? null : $"host:{target.Host}";
    }

    public static async Task Execute(RemoteFetch job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executed);
        var target = await RemoteTarget.Get(job.TargetId, ct);
        if (target is not null && target.Host == Throttled)
        {
            ctx.Backoff(TimeSpan.FromMinutes(10));   // 503 → back off the resolved host pool (default key = resolved GateKey)
            return;
        }
        Ran.Add(job.Id);
    }

    public static void Reset() { Ran.Clear(); Executed = 0; Throttled = null; }
}

/// <summary>Submission-only probe whose gate resolver can fail inside the Jobs acceptance boundary.</summary>
[JobGate(nameof(ResolveGate))]
public sealed class SubmissionGateJob : Entity<SubmissionGateJob>, IKoanJob<SubmissionGateJob>
{
    public bool Reject { get; set; }

    public Task<string?> ResolveGate(IServiceProvider services, CancellationToken ct)
        => Reject
            ? Task.FromException<string?>(new InvalidOperationException("acceptance failed deliberately"))
            : Task.FromResult<string?>("submission-probe");

    public static Task Execute(SubmissionGateJob job, JobContext ctx, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>Dispatch-time pool job (JOBS-0007): claims a slot from the registered <see cref="TestPoolResolver"/>
/// at claim time, not at submit. GateKey is null at submit and stamped by the ledger on claim.</summary>
[JobPool("test-ai-servers")]
public sealed class PoolJob : Entity<PoolJob>, IKoanJob<PoolJob>
{
    public static int Executions;

    /// <summary>Per-execution observations of ctx.State, keyed by work item (AE-18).</summary>
    public static readonly ConcurrentQueue<(string WorkId, string GateKey)> ClaimedGates = new();

    public static Task Execute(PoolJob job, JobContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref Executions);
        ClaimedGates.Enqueue((job.Id, ctx.State.GateKey ?? ""));
        return Task.CompletedTask;
    }

    public static void Reset()
    {
        Executions = 0;
        ClaimedGates.Clear();
    }
}

/// <summary>Mutable pool resolver for JOBS-0007 tests: the test updates Members at runtime to simulate
/// admin add/remove of AiServer rows without restarting the harness.</summary>
public sealed class TestPoolResolver : IJobPoolResolver
{
    private volatile IReadOnlyList<string> _members;

    public TestPoolResolver(IReadOnlyList<string> members) => _members = members;

    public string PoolName => "test-ai-servers";
    public int CapacityPerMember { get; set; } = 1;
    public IReadOnlyList<string> Members { get => _members; set => _members = value; }

    public Task<IReadOnlyList<string>> GetMembersAsync(CancellationToken ct) => Task.FromResult(_members);
}

/// <summary>Cursor-conveyor (JOBS-0005 §19.4): processes one window of a (fake) source, advances its own cursor, and
/// re-queues ITSELF at the next window via <c>ctx.ContinueWith</c>. Bounded to ~1 active ledger row — it drains a large
/// source without minting a row per item. Uses only existing verbs (ContinueWith + conditional auto-save §17.1).</summary>
public sealed class Conveyor : Entity<Conveyor>, IKoanJob<Conveyor>
{
    public const string Pull = nameof(Pull);
    public int Offset { get; set; }
    public int Window { get; set; } = 100;
    public int Total { get; set; } = 1000;
    public static int ItemsProcessed;
    public static int WindowsRun;

    public static Task Execute(Conveyor c, JobContext ctx, CancellationToken ct)
    {
        var count = Math.Min(c.Window, c.Total - c.Offset);
        Interlocked.Add(ref ItemsProcessed, count);
        Interlocked.Increment(ref WindowsRun);
        c.Offset += count;                          // advance the cursor on THIS entity (auto-saved §17.1)
        if (c.Offset < c.Total) ctx.ContinueWith(Pull);   // re-queue the same work-item at the next window
        return Task.CompletedTask;
    }

    public static void Reset() { ItemsProcessed = 0; WindowsRun = 0; }
}
