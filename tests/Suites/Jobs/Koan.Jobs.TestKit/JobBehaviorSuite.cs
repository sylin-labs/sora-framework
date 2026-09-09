using AwesomeAssertions;
using Koan.Core;
using Koan.Core.Observability.Health;
using Koan.Data.Core;
using Koan.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Jobs.TestKit;

/// <summary>
/// The behavioral contract of the orchestrator, identical across every tier (ARCH-0079). A tier provides a
/// <see cref="CreateHostAsync"/>; these specs then run unchanged on in-memory and on each durable adapter — that
/// sameness is the proof of the constant at-least-once + idempotent guarantee.
/// </summary>
public abstract class JobBehaviorSuite
{
    /// <summary>Build a harness for this tier (in-memory, SQLite, Mongo, …).</summary>
    protected abstract Task<JobsHarness> CreateHostAsync(Action<JobsOptions>? configure = null, Action<IServiceCollection>? configureServices = null);

    // --- discovery + edge ---

    [Fact]
    public async Task invalid_work_address_is_dead_lettered_without_blocking_the_next_job()
    {
        await using var host = await CreateHostAsync();
        var now = host.Clock.GetUtcNow();
        var invalid = new JobRecord
        {
            WorkType = typeof(GreetJob).FullName!, WorkId = "invalid-address",
            WorkSource = "Default", WorkAdapter = "mongo", Exclusive = true,
            Status = JobStatus.Queued, Lane = "default", VisibleAt = now, FirstSubmittedAt = now
        };
        await host.Ledger.Append(invalid, default);
        var valid = new GreetJob { Name = "after-invalid-address" };
        await valid.Job.Submit();
        await host.Drain();
        (await host.Ledger.Get(invalid.Id, default))!.Status.Should().Be(JobStatus.Dead);
        (await GreetJob.Get(valid.Id))!.Greeting.Should().Be("Hello, after-invalid-address");
    }

    [Fact]
    public async Task data_address_scopes_coalescing_status_and_cancellation()
    {
        await using var host = await CreateHostAsync();
        var work = new DedupeJob { Key = Guid.NewGuid().ToString("N") };
        await work.Job.Submit();
        using (EntityContext.Partition("fr"))
        {
            await work.Job.Submit();
            await work.Job.Submit();
            await work.Job.Cancel();
            (await work.Job.Status()).Should().Be(JobStatus.Cancelled);
        }
        (await work.Job.Status()).Should().Be(JobStatus.Queued);
        var records = await host.Ledger.Query(new JobQuery(WorkId: work.Id), default);
        records.Should().HaveCount(2);
        records.Single(r => r.WorkPartition is null).CoalesceKey.Should().Be(
            host.Registry.Require(typeof(DedupeJob).FullName!).CoalesceKey(work, ""),
            "default-address coalescing remains compatible with existing records");
        await host.Drain();
        (await work.Job.Status()).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task async_source_cannot_change_the_captured_data_partition()
    {
        await using var host = await CreateHostAsync();
        var first = new GreetJob { Name = "first" };
        var second = new GreetJob { Name = "second" };
        async IAsyncEnumerable<GreetJob> Source()
        {
            yield return first;
            using (EntityContext.Partition("de")) yield return second;
            await Task.CompletedTask;
        }
        using (EntityContext.Partition("fr")) await Source().Submit();
        await host.Drain();
        using (EntityContext.Partition("fr"))
        {
            (await GreetJob.Get(first.Id))!.Greeting.Should().Be("Hello, first");
            (await GreetJob.Get(second.Id))!.Greeting.Should().Be("Hello, second");
        }
        using (EntityContext.Partition("de")) (await GreetJob.Get(second.Id)).Should().BeNull();
    }

    [Fact]
    public async Task partitioned_success_and_failure_chains_retain_the_work_address()
    {
        await using var host = await CreateHostAsync();
        var pipeline = new Pipeline();
        var continuation = new ContinueChain();
        using (EntityContext.Partition("fr"))
        {
            await pipeline.Job.Submit(Stage.Fetch);
            await continuation.Job.Submit(Step.One);
        }
        await host.Drain();
        using (EntityContext.Partition("fr"))
        {
            (await Pipeline.Get(pipeline.Id))!.Published.Should().BeTrue();
            (await ContinueChain.Get(continuation.Id))!.TwoRan.Should().BeTrue();
        }
        var records = await host.Ledger.Query(new JobQuery(WorkId: pipeline.Id), default);
        records.Should().HaveCount(4).And.OnlyContain(r => r.WorkPartition == "fr" && r.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task exclusivity_distinguishes_same_id_in_different_data_partitions()
    {
        await using var host = await CreateHostAsync();
        var work = new GreetJob();
        await work.Job.Submit();
        using (EntityContext.Partition("fr")) await work.Job.Submit();
        var now = host.Clock.GetUtcNow();
        var first = await host.Ledger.ClaimNext("one", now, now.AddMinutes(1), [], default);
        var second = await host.Ledger.ClaimNext("two", now, now.AddMinutes(1), [], default);
        first.Should().NotBeNull();
        second.Should().NotBeNull("different physical work items do not share the exclusivity lock");
        second!.WorkPartition.Should().NotBe(first!.WorkPartition);
    }

    [Fact]
    public async Task partitioned_submission_uses_central_ledger_and_executes_at_original_address()
    {
        await using var host = await CreateHostAsync();
        var root = new GreetJob { Name = "root" };
        var localized = new GreetJob { Id = root.Id, Name = "localized" };
        await root.Job.Submit();
        using (EntityContext.Partition("fr")) await localized.Job.Submit();

        var records = await host.Ledger.Query(new JobQuery(WorkType: typeof(GreetJob).FullName, WorkId: root.Id), default);
        records.Should().HaveCount(2, "all scheduling rows must be visible to the central worker");
        using (EntityContext.Partition("unrelated")) await host.Drain();

        (await host.Ledger.Query(new JobQuery(WorkId: root.Id), default))
            .Should().OnlyContain(record => record.Status == JobStatus.Completed);

        (await GreetJob.Get(root.Id))!.Greeting.Should().Be("Hello, root");
        using (EntityContext.Partition("fr"))
        {
            (await GreetJob.Get(root.Id))!.Greeting.Should().Be("Hello, localized");
            (await localized.Job.Status()).Should().Be(JobStatus.Completed);
            (await JobRecord.Query(r => r.WorkId == root.Id)).Should().BeEmpty();
        }
        (await host.Ledger.Query(new JobQuery(WorkId: root.Id), default))
            .Should().OnlyContain(record => record.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task discovery_binds_job_types()
    {
        await using var host = await CreateHostAsync();
        host.Registry.Count.Should().BeGreaterThan(0);
        host.Registry.Get(typeof(GreetJob).FullName!).Should().NotBeNull();
    }

    [Fact]
    public async Task retired_work_types_are_dead_lettered_without_blocking_valid_work()
    {
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var now = host.Clock.GetUtcNow();
        const string retiredJobId = "retired-work-type";
        // Older than the work behind it, so the claim order is the one this spec is about rather than an
        // accident. The subject is head-of-line blocking: a retired row the conveyor reaches first must be
        // settled and stepped over, not left to stall the lane. Seeding it at the same instant as the valid
        // job left the two tied on (VisibleAt, FirstSubmittedAt) and the winner unspecified, which is what
        // made this spec fail on one store and then another as tie-breaking changed underneath it.
        var earlier = now - TimeSpan.FromMinutes(1);
        await host.Ledger.Append(new JobRecord
        {
            Id = retiredJobId,
            WorkType = "Example.RetiredWork",
            WorkId = "old-work",
            Action = "run",
            Status = JobStatus.Queued,
            Lane = "default",
            VisibleAt = earlier,
            FirstSubmittedAt = earlier
        }, TestContext.Current.CancellationToken);

        var valid = new GreetJob { Name = "still-runs" };
        await valid.Job.Submit();
        var run = await host.Driver.RunOneAsync(TestContext.Current.CancellationToken);

        var retired = await host.Ledger.Get(retiredJobId, TestContext.Current.CancellationToken);
        retired.Should().NotBeNull();
        retired!.Status.Should().Be(JobStatus.Dead);
        retired.DeadReason.Should().Be(nameof(DeadReason.UnregisteredWorkType));
        retired.Owner.Should().BeNull();
        retired.LeaseUntil.Should().BeNull();
        retired.Attempt.Should().Be(1);
        retired.Transitions.Should().ContainSingle(value => value.To == JobStatus.Dead);
        run.Should().NotBeNull("one-stage execution must continue past retired durable rows");
        run!.WorkType.Should().Be(typeof(GreetJob).FullName);
        GreetJob.Executions.Should().Be(1, "retired durable rows must not make a drain look empty");
    }

    [Fact]
    public async Task single_action_runs_and_mutates_work_item()
    {
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var j = new GreetJob { Name = "Koan" };
        var id = j.Id;
        await j.Job.Submit();
        await host.Drain();
        GreetJob.Executions.Should().Be(1);
        (await GreetJob.Get(id))!.Greeting.Should().Be("Hello, Koan");
        (await host.StatusOf<GreetJob>(id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task terminal_settlement_preserves_reported_progress()
    {
        await using var host = await CreateHostAsync();
        var work = new ProgressJob();

        await work.Job.Submit();
        await host.Drain();

        var record = await host.JobFor<ProgressJob>(work.Id);
        record.Should().NotBeNull();
        record!.Status.Should().Be(JobStatus.Completed);
        record.ProgressFraction.Should().Be(0.75);
        record.ProgressMessage.Should().Be("three quarters");
    }

    [Fact]
    public async Task finite_source_submit_runs_every_work_item()
    {
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var jobs = Enumerable.Range(0, 25).Select(i => new GreetJob { Name = $"n{i}" }).ToList();
        var submission = await jobs.Submit();
        submission.Enumerated.Should().Be(25);
        submission.Accepted.Should().Be(25);
        submission.Submitted.Should().Be(25);
        submission.Coalesced.Should().Be(0);
        submission.Failed.Should().Be(0);
        submission.SourceCompleted.Should().BeTrue();
        submission.PendingCommit.Should().BeFalse();
        await host.Drain();
        GreetJob.Executions.Should().Be(25);
    }

    [Fact]
    public async Task idempotent_submit_coalesces_duplicates()
    {
        DedupeJob.Reset();
        await using var host = await CreateHostAsync();
        await new DedupeJob { Key = "k1" }.Job.Submit();
        await new DedupeJob { Key = "k1" }.Job.Submit();
        await new DedupeJob { Key = "k2" }.Job.Submit();
        await host.Drain();
        DedupeJob.Executions.Should().Be(2);
    }

    [Fact]
    public async Task coalesced_submit_does_not_save_work_item()
    {
        // A coalesced-away submit must not touch the entity store at all.
        DedupeJob.Reset();
        await using var host = await CreateHostAsync();

        var first = new DedupeJob { Key = "k1" };
        await first.Job.Submit();

        // A distinct entity with the same coalesce key is absorbed — its row must never be written.
        var second = new DedupeJob { Key = "k1" };
        await second.Job.Submit();

        (await DedupeJob.Get(second.Id)).Should().BeNull("a coalesced-away submit must not persist its work-item");

        await host.Drain();
        DedupeJob.Executions.Should().Be(1);
    }

    [Fact]
    public async Task trailing_submit_during_run_queues_a_follow_up_execution()
    {
        // With queued-only coalescing, a submit arriving while a job is Running queues a trailing execution
        // rather than being silently absorbed. This gives the debounce / trailing-edge pattern.
        DedupeJob.Reset();
        await using var host = await CreateHostAsync();

        var first = new DedupeJob { Key = "trail" };
        await first.Job.Submit();

        // Advance the job to Running state to simulate in-flight execution.
        var now = host.Clock.GetUtcNow();
        _ = await host.Ledger.ClaimNext("test-claim-owner", now, now + TimeSpan.FromMinutes(1), Array.Empty<string>(), default);

        // A submit arriving while the job is Running must queue a follow-up record, not be silently absorbed.
        var second = new DedupeJob { Key = "trail" };
        await second.Job.Submit();

        var records = await host.Ledger.Query(new JobQuery(WorkType: typeof(DedupeJob).FullName!), default);
        records.Should().HaveCount(2, "one Running + one Queued trailing-edge follow-up");
        records.Should().ContainSingle(r => r.Status == JobStatus.Running);
        records.Should().ContainSingle(r => r.Status == JobStatus.Queued, "trailing submit must create a follow-up record");
    }

    // --- chain ---

    [Fact]
    public async Task linear_chain_advances_and_carries_saga_state()
    {
        await using var host = await CreateHostAsync();
        var p = new Pipeline();
        var id = p.Id;
        await p.Job.Submit(Stage.Fetch);
        await host.Drain();
        var saved = await Pipeline.Get(id);
        saved!.Fetched.Should().Be("raw");
        saved.Parsed.Should().Be("raw-parsed");
        saved.Minted.Should().BeTrue();
        saved.Published.Should().BeTrue();
        saved.Trail.Should().Equal(Stage.Fetch, Stage.Parse, Stage.Mint, Stage.Publish);
    }

    [Fact]
    public async Task stop_chain_halts_after_current_step()
    {
        BranchJob.Reset();
        BranchJob.Mode = "stop";
        await using var host = await CreateHostAsync();
        var j = new BranchJob();
        var id = j.Id;
        await j.Job.Submit("a");
        await host.Drain();
        (await BranchJob.Get(id))!.Trail.Should().Equal("a");
    }

    [Fact]
    public async Task continue_with_branches_to_an_off_chain_action()
    {
        BranchJob.Reset();
        BranchJob.Mode = "branch";
        await using var host = await CreateHostAsync();
        var j = new BranchJob();
        var id = j.Id;
        await j.Job.Submit("a");
        await host.Drain();
        (await BranchJob.Get(id))!.Trail.Should().Equal("a", "z");
    }

    [Fact]
    public async Task on_failure_continue_advances_the_chain()
    {
        await using var host = await CreateHostAsync();
        var j = new ContinueChain();
        var id = j.Id;
        await j.Job.Submit(Step.One);
        await host.Drain();
        (await ContinueChain.Get(id))!.TwoRan.Should().BeTrue();
    }

    [Fact]
    public async Task on_failure_abort_stops_the_chain()
    {
        await using var host = await CreateHostAsync();
        var j = new AbortChain();
        var id = j.Id;
        await j.Job.Submit(Step.One);
        await host.Drain();
        (await AbortChain.Get(id))!.TwoRan.Should().BeFalse();
    }

    // --- cooperative backoff ---

    [Fact]
    public async Task reschedule_defers_without_consuming_an_attempt()
    {
        RescheduleJob.Reset();
        RescheduleJob.RescheduleUntil = 2;
        await using var host = await CreateHostAsync();
        var j = new RescheduleJob();
        var id = j.Id;
        await j.Job.Submit();
        await host.Drain();
        RescheduleJob.Executions.Should().Be(1);
        await host.Drain();
        RescheduleJob.Executions.Should().Be(1);
        host.Advance(TimeSpan.FromMinutes(5)); await host.Drain();
        host.Advance(TimeSpan.FromMinutes(5)); await host.Drain();
        RescheduleJob.Executions.Should().Be(3);
        var rec = await host.JobFor<RescheduleJob>(id);
        rec!.Status.Should().Be(JobStatus.Completed);
        rec.Reschedules.Should().Be(2);
        rec.Attempt.Should().Be(1);
    }

    [Fact]
    public async Task max_reschedules_dead_letters()
    {
        RescheduleJob.Reset();
        RescheduleJob.RescheduleUntil = 99;
        await using var host = await CreateHostAsync(o => o.DefaultMaxReschedules = 1);
        var j = new RescheduleJob();
        var id = j.Id;
        await j.Job.Submit();
        await host.Drain();
        host.Advance(TimeSpan.FromMinutes(5)); await host.Drain();
        var rec = await host.JobFor<RescheduleJob>(id);
        rec!.Status.Should().Be(JobStatus.Dead);
        rec.DeadReason.Should().Be(nameof(DeadReason.PerpetuallyDeferred));
    }

    [Fact]
    public async Task backoff_gate_defers_peers_at_dispatch_without_running()
    {
        GatedJob.Reset();
        GatedJob.Trip429 = true;
        await using var host = await CreateHostAsync();
        var a = new GatedJob { Host = "api" };
        var b = new GatedJob { Host = "api" };
        var c = new GatedJob { Host = "api" };
        await a.Job.Submit();
        await host.Drain();
        GatedJob.Executions.Should().Be(1);
        await b.Job.Submit();
        await c.Job.Submit();
        await host.Drain();
        GatedJob.Executions.Should().Be(1);
        host.Advance(TimeSpan.FromMinutes(5));
        await host.Drain();
        (await host.StatusOf<GatedJob>(a.Id)).Should().Be(JobStatus.Completed);
        (await host.StatusOf<GatedJob>(b.Id)).Should().Be(JobStatus.Completed);
        (await host.StatusOf<GatedJob>(c.Id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task state_gatekey_is_null_for_an_ungated_job()
    {
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var j = new GreetJob { Name = "ungated" };
        await j.Job.Submit();
        await host.Drain();
        GreetJob.Executions.Should().Be(1);
        GreetJob.LastGateKey.Should().BeNull("an ungated job claims no gate");
    }

    [Fact]
    public async Task claimed_gate_state_survives_a_persisted_routing_change()
    {
        // The snapshot's GateKey is the key captured with THIS claim, not a re-resolution of the work item's
        // current routing: even after the TEST changes and persists the routing between attempts, both claims
        // retain the gate recorded on the ledger row at submit. (Deferral performs no work-item auto-save; the
        // routing change is written by the test, not the handler.)
        GatedJob.Reset();
        await using var host = await CreateHostAsync();
        var j = new GatedJob { Host = "api-a" };
        await j.Job.Submit();

        GatedJob.Trip429 = true;   // attempt 1 takes the existing backoff deferral path
        await host.Drain();

        var stored = await GatedJob.Get(j.Id);
        stored!.Host = "api-b";
        await GatedJob.Upsert(stored);

        host.Advance(TimeSpan.FromMinutes(5));
        await host.Drain();

        (await GatedJob.Get(j.Id))!.Host.Should().Be("api-b", "the work item's routing actually changed on disk");
        (await host.StatusOf<GatedJob>(j.Id)).Should().Be(JobStatus.Completed);
        GatedJob.ClaimedGates.Where(o => o.WorkId == j.Id).Select(o => o.GateKey)
            .Should().Equal(new[] { "api-a", "api-a" },
                "both claims project the gate captured with the claim, not the work item's new routing");
    }

    [Fact]
    public async Task backoff_override_gates_peers_while_the_snapshot_keeps_the_claimed_gate()
    {
        // An explicit Backoff key redirects the shared gate WITHOUT rewriting the claimed row: peers of the
        // override key defer at dispatch, while every execution of the original job still sees its own gate.
        GatedJob.Reset();
        await using var host = await CreateHostAsync();
        var a = new GatedJob { Host = "api" };
        var b = new GatedJob { Host = "manual:hot-key" };   // its declared gate IS the override key
        await a.Job.Submit();

        GatedJob.Trip429 = true;                  // attempt 1 takes the 429 path
        GatedJob.OverrideKey = "manual:hot-key";  // and backs off the override key instead of "api"
        await host.Drain();

        await b.Job.Submit();
        await host.Drain();

        (await host.StatusOf<GatedJob>(b.Id)).Should().Be(JobStatus.Queued, "the override gate defers peers at dispatch");

        host.Advance(TimeSpan.FromMinutes(5));
        await host.Drain();

        (await host.StatusOf<GatedJob>(a.Id)).Should().Be(JobStatus.Completed);
        (await host.StatusOf<GatedJob>(b.Id)).Should().Be(JobStatus.Completed);
        GatedJob.ClaimedGates.Where(o => o.WorkId == a.Id).Select(o => o.GateKey)
            .Should().Equal(new[] { "api", "api" },
                "both claims project the claimed gate, even after Backoff(override) ran in the first execution");
        GatedJob.ClaimedGates.Single(o => o.WorkId == b.Id).GateKey.Should().Be("manual:hot-key",
            "the peer's snapshot is its own declared gate, not the shared gate state");
    }

    // --- retry / cancel / timeout ---

    [Fact]
    public async Task retries_then_succeeds()
    {
        FlakyJob.Reset();
        FlakyJob.SucceedAtAttempt = 3;
        await using var host = await CreateHostAsync();
        var job = new FlakyJob();
        var id = job.Id;
        await job.Job.Submit(FlakyJob.Action);
        await host.Drain();
        host.Advance(TimeSpan.FromMinutes(1)); await host.Drain();
        host.Advance(TimeSpan.FromMinutes(1)); await host.Drain();
        FlakyJob.Executions.Should().Be(3);
        (await host.StatusOf<FlakyJob>(id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task poison_fails_after_exhausting_retries()
    {
        FlakyJob.Reset();
        FlakyJob.SucceedAtAttempt = 99;
        await using var host = await CreateHostAsync();
        var job = new FlakyJob();
        var id = job.Id;
        await job.Job.Submit(FlakyJob.Action);
        for (var i = 0; i < 3; i++) { await host.Drain(); host.Advance(TimeSpan.FromMinutes(1)); }
        FlakyJob.Executions.Should().Be(3);
        var rec = await host.JobFor<FlakyJob>(id);
        rec!.Status.Should().Be(JobStatus.Failed);
        rec.DeadReason.Should().Be(nameof(DeadReason.Poison));
    }

    [Fact]
    public async Task cancel_queued_job_never_runs()
    {
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var job = new GreetJob { Name = "x" };
        var id = job.Id;
        await job.Job.Submit();
        await job.Job.Cancel();
        (await host.StatusOf<GreetJob>(id)).Should().Be(JobStatus.Cancelled);
        await host.Drain();
        GreetJob.Executions.Should().Be(0);
    }

    [Fact]
    public async Task cancel_running_job_cooperatively()
    {
        WaitJob.Reset();
        await using var host = await CreateHostAsync();
        var job = new WaitJob();
        var id = job.Id;
        await job.Job.Submit(WaitJob.Action);
        var drain = host.Drain();
        await Wait.Until(() => WaitJob.Executions == 1);
        await host.Coordinator.CancelWorkAsync(typeof(WaitJob).FullName!, id, default);
        await drain;
        (await host.StatusOf<WaitJob>(id)).Should().Be(JobStatus.Cancelled);
        WaitJob.Cancellations.Should().Be(1);
    }

    [Fact]
    public async Task timeout_fails_the_job()
    {
        WaitJob.Reset();
        await using var host = await CreateHostAsync();
        var job = new WaitJob();
        var id = job.Id;
        await job.Job.Submit(WaitJob.Action);
        var drain = host.Drain();
        await Wait.Until(() => WaitJob.Executions == 1);
        host.Advance(TimeSpan.FromSeconds(31));
        await drain;
        (await host.StatusOf<WaitJob>(id)).Should().Be(JobStatus.Failed);
        WaitJob.Cancellations.Should().Be(1);
    }

    [Fact]
    public async Task cancel_at_settle_window_stops_chain()
    {
        // Regression: a cancel request landing between handler completion and the chain-record append must
        // prevent the chain from advancing. The handler deliberately ignores the cancellation token to reproduce
        // the settle-time window where Execute already returned but SettleSuccessAsync hasn't appended the next.
        ChainCancelRaceJob.Reset();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChainCancelRaceJob.Unblock = tcs;
        await using var host = await CreateHostAsync();

        var j = new ChainCancelRaceJob();
        var id = j.Id;
        await j.Job.Submit("a");

        var drain = host.Drain();
        await Wait.Until(() => ChainCancelRaceJob.StepAExecutions == 1);  // handler has entered Execute
        await host.Coordinator.CancelWorkAsync(typeof(ChainCancelRaceJob).FullName!, id, default);
        tcs.SetResult();   // let the handler return (it ignores ct — models the settle-time race)
        await drain;

        (await ChainCancelRaceJob.Get(id))?.StepBRan.Should().BeFalse(
            "a cancel landing in the settle window must stop the chain from advancing");
    }

    // --- lanes ---

    [Fact]
    public async Task lane_caps_concurrency()
    {
        SlowJob.Reset();
        await using var host = await CreateHostAsync();
        var jobs = Enumerable.Range(0, 6).Select(_ => new SlowJob()).ToList();
        await jobs.Submit(SlowJob.Action);
        await host.Drain();
        SlowJob.Peak.Should().BeLessThanOrEqualTo(2);
        var done = await host.Coordinator.WhereAsync(new JobQuery(WorkType: typeof(SlowJob).FullName!, Status: JobStatus.Completed), default);
        done.Should().HaveCount(6);
    }

    [Fact]
    public async Task worker_concurrency_caps_total_in_flight()
    {
        ExclusiveJob.Reset();
        await using var host = await CreateHostAsync(o =>
        {
            o.WorkerConcurrency = 2;
            o.DefaultMaxConcurrency = 8;
        });
        // 8 distinct entities — no per-entity serialization constraint — only WorkerConcurrency limits throughput.
        var jobs = Enumerable.Range(0, 8).Select(_ => new ExclusiveJob()).ToList();
        await jobs.Submit(ExclusiveJob.ActionA);
        await host.Drain();
        ExclusiveJob.Peak.Should().BeLessThanOrEqualTo(2, "WorkerConcurrency caps total in-flight concurrency");
        (await host.Coordinator.WhereAsync(new JobQuery(WorkType: typeof(ExclusiveJob).FullName!, Status: JobStatus.Completed), default))
            .Should().HaveCount(8);
    }

    // --- scheduling (initiator submits on cadence) ---

    [Fact]
    public async Task scheduled_action_runs_when_the_scheduler_ticks()
    {
        Reconciled.Reset();
        await using var host = await CreateHostAsync();
        await host.Drain();
        Reconciled.Executions.Should().Be(0);
        await host.TriggerDue();
        await host.Drain();
        Reconciled.Executions.Should().Be(1);
    }

    [Fact]
    public async Task recurring_schedule_actually_recurs()
    {
        Reconciled.Reset();
        await using var host = await CreateHostAsync();
        await host.TriggerDue(); await host.Drain();
        host.Advance(TimeSpan.FromMinutes(10)); await host.TriggerDue(); await host.Drain();
        host.Advance(TimeSpan.FromMinutes(10)); await host.TriggerDue(); await host.Drain();
        Reconciled.Executions.Should().Be(3);
    }

    [Fact]
    public async Task schedule_respects_its_interval()
    {
        Reconciled.Reset();
        await using var host = await CreateHostAsync();
        await host.TriggerDue(); await host.Drain();
        Reconciled.Executions.Should().Be(1);
        await host.TriggerDue(); await host.Drain();
        Reconciled.Executions.Should().Be(1);
        host.Advance(TimeSpan.FromMinutes(10));
        await host.TriggerDue(); await host.Drain();
        Reconciled.Executions.Should().Be(2);
    }

    [Fact]
    public async Task continuous_schedule_fires_every_tick()
    {
        Heartbeat.Reset();
        await using var host = await CreateHostAsync();
        await host.TriggerDue(); await host.Drain();
        await host.TriggerDue(); await host.Drain();
        await host.TriggerDue(); await host.Drain();
        Heartbeat.Executions.Should().Be(3);
    }

    [Fact]
    public async Task cron_schedule_fires_at_its_occurrence_and_recurs_daily()
    {
        NightlyJob.Reset();
        await using var host = await CreateHostAsync();
        await host.TriggerDue(); await host.Drain();
        NightlyJob.Executions.Should().Be(0);
        host.Advance(TimeSpan.FromHours(3));
        await host.TriggerDue(); await host.Drain();
        NightlyJob.Executions.Should().Be(1);
        host.Advance(TimeSpan.FromHours(1));
        await host.TriggerDue(); await host.Drain();
        NightlyJob.Executions.Should().Be(1);
        host.Advance(TimeSpan.FromHours(23));
        await host.TriggerDue(); await host.Drain();
        NightlyJob.Executions.Should().Be(2);
    }

    [Fact]
    public async Task boot_action_runs_once_at_boot()
    {
        BootOnce.Reset();
        await using var host = await CreateHostAsync();
        await host.Boot();
        await host.Drain();
        BootOnce.Executions.Should().Be(1);
    }

    [Fact]
    public async Task reaper_reclaims_a_lapsed_lease()
    {
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var g = new GreetJob { Name = "recovered" };
        var id = g.Id;
        await GreetJob.Upsert(g);
        var now = host.Clock.GetUtcNow();
        await host.Ledger.Append(new JobRecord
        {
            WorkType = typeof(GreetJob).FullName!,
            WorkId = id,
            Action = "",
            Status = JobStatus.Running,
            Attempt = 1,
            Lane = "default",
            FirstSubmittedAt = now,
            VisibleAt = now,
            LeaseUntil = now - TimeSpan.FromMinutes(1),
            Owner = "dead-worker",
        }, default);
        await host.Reap();
        await host.Drain();
        GreetJob.Executions.Should().Be(1);
        (await host.StatusOf<GreetJob>(id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task facade_query_filters_converge_across_tiers()
    {
        // PMC-060: the JobQuery surface is a filter-only, unordered contract identical on every tier — no
        // sort/paging axis exists to diverge. These asserts pin the semantics: same filters, same record set,
        // regardless of dictionary insertion order (in-memory) or store-side layout (durable).
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var ids = new List<string>();
        foreach (var (name, status) in new[] { ("a", JobStatus.Queued), ("b", JobStatus.Queued), ("c", JobStatus.Completed) })
        {
            var g = new GreetJob { Name = name };
            ids.Add(g.Id);
            await GreetJob.Upsert(g);
            var now = host.Clock.GetUtcNow();
            await host.Ledger.Append(new JobRecord
            {
                WorkType = typeof(GreetJob).FullName!,
                WorkId = g.Id,
                Action = "",
                Status = status,
                Attempt = status == JobStatus.Completed ? 1 : 0,
                Lane = "default",
                FirstSubmittedAt = now,
                VisibleAt = now,
                LastSettledAt = status == JobStatus.Completed ? now : null,
            }, default);
        }
        var workType = typeof(GreetJob).FullName!;

        var all = await host.Ledger.Query(new JobQuery(WorkType: workType), default);
        all.Select(r => r.WorkId).OrderBy(x => x).Should().Equal(ids.OrderBy(x => x),
            "the wildcard-axis query returns the whole filtered set, unordered");

        var queued = await host.Ledger.Query(new JobQuery(WorkType: workType, Status: JobStatus.Queued), default);
        queued.Select(r => r.WorkId).OrderBy(x => x).Should().HaveCount(2);

        var single = await host.Ledger.Query(new JobQuery(WorkType: workType, WorkId: ids[0]), default);
        single.Should().ContainSingle(r => r.WorkId == ids[0]);
    }

    [Fact]
    public async Task claim_honors_reservations_on_every_tier()
    {
        // PMC-056: the reservation eligibility clause converges across tiers (ARCH-0079) — a fresh foreign
        // cookie hides the row, restamping is refused, and the named hand claims through the normal path.
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var g = new GreetJob { Name = "reserved" };
        var id = g.Id;
        await GreetJob.Upsert(g);
        var now = host.Clock.GetUtcNow();
        var record = new JobRecord
        {
            WorkType = typeof(GreetJob).FullName!,
            WorkId = id,
            Action = "",
            Status = JobStatus.Queued,
            Lane = "default",
            FirstSubmittedAt = now,
            VisibleAt = now,
        };
        await host.Ledger.Append(record, default);
        var jobId = (await host.JobFor<GreetJob>(id))!.Id;

        (await host.Ledger.TryReserve(jobId, "peer-hand", now + TimeSpan.FromMinutes(5), now, default)).Should().BeTrue();

        (await host.Ledger.ClaimNext("me", now, now + TimeSpan.FromMinutes(1), Array.Empty<string>(), default))
            .Should().BeNull("a fresh foreign reservation hides the row from another hand");
        (await host.Ledger.TryReserve(jobId, "second-hand", now + TimeSpan.FromMinutes(5), now, default))
            .Should().BeFalse("a row already carrying a fresh reservation cannot be re-stamped");

        var claimed = await host.Ledger.ClaimNext("peer-hand", now, now + TimeSpan.FromMinutes(1), Array.Empty<string>(), default);
        claimed.Should().NotBeNull();
        claimed!.Owner.Should().Be("peer-hand");
    }

    [Fact]
    public async Task a_running_job_renews_its_lease_and_outlives_the_original_window()
    {
        // JOBS-0009: a live handler holds its lease past the original window; the reaper never sees it lapse and
        // no second claim mints Attempt=2.
        WaitJob.Reset();
        await using var host = await CreateHostAsync(o => o.LeaseDuration = TimeSpan.FromSeconds(2));
        var job = new WaitJob();
        var id = job.Id;
        var start = host.Clock.GetUtcNow();
        await job.Job.Submit(WaitJob.Action);
        var drain = host.Drain();
        await Wait.Until(() => WaitJob.Executions == 1);

        for (var i = 0; i < 5; i++)   // 3.5s wall-of-fake-time ≫ the 2s lease; each step fires a renewal tick
            host.Advance(TimeSpan.FromMilliseconds(700));

        DateTimeOffset renewedUntil = default;
        await Wait.Until(() =>
        {
            var r = host.JobFor<WaitJob>(id).GetAwaiter().GetResult();
            renewedUntil = r?.LeaseUntil ?? default;
            return renewedUntil > start + TimeSpan.FromSeconds(2);
        });
        (await host.JobFor<WaitJob>(id))!.Status.Should().Be(JobStatus.Running);
        (await host.JobFor<WaitJob>(id))!.Attempt.Should().Be(1);

        await host.Coordinator.CancelWorkAsync(typeof(WaitJob).FullName!, id, default);
        await drain;
        (await host.StatusOf<WaitJob>(id)).Should().Be(JobStatus.Cancelled);
        (await host.JobFor<WaitJob>(id))!.Attempt.Should().Be(1);
        renewedUntil.Should().BeOnOrAfter(start + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task losing_the_lease_abandons_without_settling()
    {
        // JOBS-0009: when another claimant takes the row mid-run, the loser cancels its handler and writes
        // NOTHING — the winner's ownership and state survive untouched.
        WaitJob.Reset();
        await using var host = await CreateHostAsync(o => o.LeaseDuration = TimeSpan.FromSeconds(2));
        var job = new WaitJob();
        var id = job.Id;
        await job.Job.Submit(WaitJob.Action);
        var drain = host.Drain();
        await Wait.Until(() => WaitJob.Executions == 1);

        var stolen = await host.JobFor<WaitJob>(id);
        stolen!.Owner = "node-b";
        stolen.LeaseUntil = host.Clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        await host.Ledger.Update(stolen, default);

        host.Advance(TimeSpan.FromMilliseconds(700));   // next renewal tick discovers the foreign owner
        await Wait.Until(() => WaitJob.Cancellations >= 1);
        await drain;

        var final = await host.JobFor<WaitJob>(id);
        final!.Owner.Should().Be("node-b");
        final.Status.Should().Be(JobStatus.Running);
        final.Attempt.Should().Be(1);
    }

    // --- type-level trigger ---

    [Fact]
    public async Task trigger_runs_a_type_level_action_without_an_instance()
    {
        TickJob.Reset();
        await using var host = await CreateHostAsync();
        await TickJob.Jobs.Trigger("sweep");
        await host.Drain();
        TickJob.Executions.Should().Be(1);
        TickJob.LastAction.Should().Be("sweep");
    }

    [Fact]
    public async Task overlapping_triggers_coalesce_on_an_idempotent_singleton()
    {
        SweepTick.Reset();
        await using var host = await CreateHostAsync();
        await SweepTick.Jobs.Trigger("sweep");
        await SweepTick.Jobs.Trigger("sweep");
        await host.Drain();
        SweepTick.Executions.Should().Be(1);
    }

    [Fact]
    public async Task trigger_does_not_persist_scheduler_singleton()
    {
        // TriggerAsync must not write a __koan_job_singleton__ entity row into the consumer's collection.
        TickJob.Reset();
        await using var host = await CreateHostAsync();
        await TickJob.Jobs.Trigger("sweep");
        await host.Drain();
        TickJob.Executions.Should().Be(1);
        (await TickJob.Get(Koan.Jobs.Infrastructure.Constants.Work.SingletonId)).Should().BeNull(
            "the scheduler singleton must not be persisted to the consumer's entity collection");
    }

    // --- archival ---

    [Fact]
    public async Task archival_purges_completed_past_retention_keeps_failed()
    {
        GreetJob.Reset();
        FlakyJob.Reset();
        FlakyJob.SucceedAtAttempt = 99;
        await using var host = await CreateHostAsync(o => o.ArchiveAfter = TimeSpan.FromHours(1));

        var ok = new GreetJob { Name = "x" };
        var okId = ok.Id;
        await ok.Job.Submit();
        await host.Drain();

        var bad = new FlakyJob();
        var badId = bad.Id;
        await bad.Job.Submit(FlakyJob.Action);
        for (var i = 0; i < 3; i++) { await host.Drain(); host.Advance(TimeSpan.FromMinutes(1)); }
        (await host.StatusOf<FlakyJob>(badId)).Should().Be(JobStatus.Failed);

        host.Advance(TimeSpan.FromHours(2));
        await host.Archive();

        (await host.StatusOf<GreetJob>(okId)).Should().BeNull("completed past retention is purged");
        (await host.StatusOf<FlakyJob>(badId)).Should().Be(JobStatus.Failed, "failed is retained");
    }

    // --- work-item write safety (§17) ---

    [Fact]
    public async Task autosave_persists_a_mutation_to_the_passed_reference()
    {
        GreetJob.Reset();
        await using var host = await CreateHostAsync();
        var g = new GreetJob { Name = "Ada" };
        var id = g.Id;
        await g.Job.Submit();
        await host.Drain();
        (await GreetJob.Get(id))!.Greeting.Should().Be("Hello, Ada", "mutating the passed reference persists (the 80% path)");
    }

    [Fact]
    public async Task conditional_autosave_does_not_clobber_a_handlers_own_write()
    {
        await using var host = await CreateHostAsync();
        var j = new SelfSaveJob();
        var id = j.Id;
        await j.Job.Submit();
        await host.Drain();
        // The handler saved its own copy and never touched the passed reference; the framework must not overwrite it.
        (await SelfSaveJob.Get(id))!.Result.Should().Be(SelfSaveJob.Written);
    }

    [Fact]
    public async Task exclusive_is_the_default_one_action_per_entity_at_a_time()
    {
        ExclusiveJob.Reset();
        await using var host = await CreateHostAsync();
        var e = new ExclusiveJob();
        await e.Job.Submit(ExclusiveJob.ActionA);
        await e.Job.Submit(ExclusiveJob.ActionB);   // same entity, different action, different lane
        await host.Drain();
        ExclusiveJob.Peak.Should().Be(1, "different actions on one entity must not run concurrently by default");
    }

    [Fact]
    public async Task parallel_safe_allows_concurrent_actions_on_one_entity()
    {
        ParallelJob.Reset();
        await using var host = await CreateHostAsync();
        var e = new ParallelJob();
        await e.Job.Submit(ParallelJob.ActionA);
        await e.Job.Submit(ParallelJob.ActionB);
        await host.Drain();
        ParallelJob.Peak.Should().Be(2, "[ParallelSafe] lifts per-entity serialization");
    }

    [Fact]
    public async Task different_entities_run_concurrently()
    {
        ExclusiveJob.Reset();
        await using var host = await CreateHostAsync();
        var e1 = new ExclusiveJob();
        var e2 = new ExclusiveJob();
        await e1.Job.Submit(ExclusiveJob.ActionA);
        await e2.Job.Submit(ExclusiveJob.ActionA);   // same action/lane, different entities
        await host.Drain();
        ExclusiveJob.Peak.Should().Be(2, "serialization is per-entity, not global — different entities still parallelize");
    }

    // --- runtime-resolved gate keys (§18) ---

    [Fact]
    public async Task a_resolved_gate_key_defers_only_same_host_siblings()
    {
        RemoteFetch.Reset();
        await using var host = await CreateHostAsync();
        var xma = new RemoteTarget { Host = "xivmodarchive.com" }; await RemoteTarget.Upsert(xma);
        var gh  = new RemoteTarget { Host = "github.com" };        await RemoteTarget.Upsert(gh);
        RemoteFetch.Throttled = "xivmodarchive.com";

        // 1. one xivmodarchive fetch hits the 503 and backs off the resolved host pool.
        var first = new RemoteFetch { TargetId = xma.Id };
        await first.Job.Submit(RemoteFetch.Refresh);
        await host.Drain();
        RemoteFetch.Executed.Should().Be(1);
        RemoteFetch.Ran.Should().NotContain(first.Id, "it backed off, it didn't complete");

        // 2. siblings submitted while the gate is active: same host is gated at dispatch (never runs); other host runs.
        var xmaSibling = new RemoteFetch { TargetId = xma.Id };
        var ghJob      = new RemoteFetch { TargetId = gh.Id };
        await xmaSibling.Job.Submit(RemoteFetch.Refresh);
        await ghJob.Job.Submit(RemoteFetch.Refresh);
        await host.Drain();
        RemoteFetch.Ran.Should().Contain(ghJob.Id, "a different host is unaffected by the xivmodarchive gate");
        RemoteFetch.Ran.Should().NotContain(xmaSibling.Id);
        RemoteFetch.Executed.Should().Be(2, "the gated sibling never ran — only the github fetch executed");

        // 3. host recovers, gate releases → the xivmodarchive jobs run.
        RemoteFetch.Throttled = null;
        host.Advance(TimeSpan.FromMinutes(11));
        await host.Drain();
        RemoteFetch.Ran.Should().Contain(first.Id);
        RemoteFetch.Ran.Should().Contain(xmaSibling.Id);
    }

    // --- high-throughput / bulk (§19): the same guarantees on every tier ---
    // Seeded through the LEDGER (not JobRecord.Save) so the in-memory tier — whose ledger is a dictionary, not the
    // data store — stays consistent. Volume is enough to exercise a real index, small enough to stay fast on containers.

    /// <summary>Backlog size for the §19 push-down specs; override per tier if a store needs a lighter touch.</summary>
    protected virtual int ScaleVolume => 2000;

    [Fact]
    public async Task query_returns_only_the_matching_status_among_a_backlog()
    {
        await using var host = await CreateHostAsync();
        var wt = typeof(GreetJob).FullName!;
        var now = host.Clock.GetUtcNow();
        await host.Ledger.AppendMany(Seed(wt, ScaleVolume, JobStatus.Completed, now, "c"), default);   // noise
        await host.Ledger.AppendMany(Seed(wt, 5, JobStatus.Queued, now, "q"), default);                // needles

        var active = await host.Ledger.Query(new JobQuery(WorkType: wt, Status: JobStatus.Queued), default);

        active.Should().HaveCount(5);                                  // the status filter is applied on the store
        active.Should().OnlyContain(r => r.Status == JobStatus.Queued);
    }

    [Fact]
    public async Task claim_takes_the_fifo_head_of_a_large_backlog()
    {
        await using var host = await CreateHostAsync();
        var now = host.Clock.GetUtcNow();
        var wt = typeof(GreetJob).FullName!;
        await host.Ledger.AppendMany(Seed(wt, ScaleVolume, JobStatus.Queued, now.AddDays(-1), "q", visible: true), default);

        var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), Array.Empty<string>(), default);

        claimed.Should().NotBeNull();
        claimed!.WorkId.Should().Be("q0");            // earliest FirstSubmittedAt → ordered claim, true FIFO head
        claimed.Status.Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task retention_purges_old_failed_keeps_recent_and_active()
    {
        await using var host = await CreateHostAsync(o =>
        {
            o.ArchiveAfter = TimeSpan.FromDays(7);
            o.FailedAfter = TimeSpan.FromDays(30);
            o.RetainPerWorkType = 0;
        });
        var wt = typeof(GreetJob).FullName!;
        var now = host.Clock.GetUtcNow();
        await host.Ledger.AppendMany(new[]
        {
            Rec(wt, "c-old", JobStatus.Completed, now.AddDays(-10)),  // > 7d  → purged
            Rec(wt, "c-new", JobStatus.Completed, now.AddDays(-1)),   // < 7d  → kept
            Rec(wt, "f-old", JobStatus.Failed, now.AddDays(-40)),     // > 30d → purged (formerly retained forever)
            Rec(wt, "f-new", JobStatus.Failed, now.AddDays(-5)),      // < 30d → kept
            Rec(wt, "active", JobStatus.Queued, now),                 // active → never purged
        }, default);

        var purged = await host.Archive();

        purged.Should().Be(2);
        var ids = (await host.Ledger.Query(new JobQuery(WorkType: wt), default)).Select(r => r.WorkId).ToHashSet();
        ids.Should().BeEquivalentTo(new[] { "c-new", "f-new", "active" });
    }

    [Fact]
    public async Task retention_cap_trims_terminal_to_the_newest_per_worktype()
    {
        await using var host = await CreateHostAsync(o =>
        {
            o.ArchiveAfter = TimeSpan.Zero;   // windows off — exercise the cap alone
            o.FailedAfter = TimeSpan.Zero;
            o.RetainPerWorkType = 10;
        });
        var wt = typeof(GreetJob).FullName!;
        var now = host.Clock.GetUtcNow();
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 50).Select(i => Rec(wt, $"t{i:D3}", JobStatus.Completed, now.AddSeconds(-i))).ToList(), default);

        var purged = await host.Archive();

        purged.Should().Be(40);   // keep newest 10
        (await host.Ledger.Query(new JobQuery(WorkType: wt, Status: JobStatus.Completed), default)).Should().HaveCount(10);
    }

    [Fact]
    public async Task count_active_excludes_terminal_rows()
    {
        await using var host = await CreateHostAsync();
        var wt = typeof(GreetJob).FullName!;
        var now = host.Clock.GetUtcNow();
        await host.Ledger.AppendMany(Seed(wt, 30, JobStatus.Queued, now, "a"), default);
        await host.Ledger.AppendMany(Seed(wt, 70, JobStatus.Completed, now, "t"), default);

        (await host.Ledger.CountActive(wt, default)).Should().Be(30);
    }

    [Fact]
    public async Task cursor_conveyor_drains_a_source_with_a_bounded_ledger()
    {
        Conveyor.Reset();
        await using var host = await CreateHostAsync();
        await new Conveyor { Total = 2000, Window = 100 }.Job.Submit(Conveyor.Pull);
        await host.Drain();

        Conveyor.ItemsProcessed.Should().Be(2000);
        Conveyor.WindowsRun.Should().Be(20);
        var rows = await host.Ledger.Query(new JobQuery(WorkType: typeof(Conveyor).FullName!), default);
        rows.Should().HaveCount(20);                  // one ledger row per window, not per item
        rows.Should().OnlyContain(r => r.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task metrics_rollup_survives_a_retention_purge()
    {
        // §20.2: terminal outcomes accrue into a node-sharded JobMetric and SURVIVE a retention purge that deletes the
        // very JobRecords they counted — the throughput-history half (active counts come from the indexed ledger).
        GreetJob.Reset();
        await using var host = await CreateHostAsync(o =>
        {
            o.MetricsEnabled = true;
            o.ArchiveAfter = TimeSpan.FromMinutes(1);   // small window: a short advance makes the Completed rows purgeable
        });
        var wt = typeof(GreetJob).FullName!;

        await Enumerable.Range(0, 5).Select(i => new GreetJob { Name = $"n{i}" }).ToList().Submit();
        await host.Drain();              // 5 complete → 5 in-memory deltas
        await host.FlushMetrics();       // fold into this node's JobMetric shard row

        var from = host.Clock.GetUtcNow().AddHours(-1);
        (await JobMetrics.Summary(wt, from, host.Clock.GetUtcNow().AddHours(1))).GetValueOrDefault("Completed").Should().Be(5);

        // Purge the ledger: the 5 Completed JobRecords are deleted, but the rollup (retained 30d) is not.
        host.Advance(TimeSpan.FromMinutes(2));
        await host.Archive();
        (await host.Ledger.Query(new JobQuery(WorkType: wt, Status: JobStatus.Completed), default)).Should().BeEmpty();

        (await JobMetrics.Summary(wt, from, host.Clock.GetUtcNow().AddHours(1))).GetValueOrDefault("Completed").Should().Be(5);
    }

    [Fact]
    public async Task concurrent_claimers_take_distinct_jobs_no_double_claim()
    {
        // §20.3: under competing consumers the atomic claim hands each ready job to exactly ONE claimer — no double
        // claim. On durable tiers this exercises the conditional compare-and-set; the in-memory ledger is already
        // single-claim under its lock. (Without an atomic claim, last-write-wins lets two claimers grab the same row.)
        await using var host = await CreateHostAsync();
        var wt = typeof(GreetJob).FullName!;
        const int jobCount = 24;
        await host.Ledger.AppendMany(Seed(wt, jobCount, JobStatus.Queued, host.Clock.GetUtcNow(), "c", visible: true), default);
        host.Advance(TimeSpan.FromSeconds(1));               // all seeded VisibleAt are now in the past → claimable
        var now = host.Clock.GetUtcNow();

        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            while (await host.Ledger.ClaimNext($"owner-{i}", now, now.AddMinutes(1), Array.Empty<string>(), default) is { } rec)
                claimed.Add(rec.Id);
        })));

        // Checked first, because it separates the two ways the counts below can be wrong. A claim handed to two
        // owners leaves nothing queued and shows up as MORE than jobCount — measured at 104 of 24 with the
        // conditional write removed. A claim nobody took leaves rows queued and shows up as fewer: a claimer
        // treats a CAS loss as "no work available" and stops for good, so a shortfall is the drive loop giving
        // up, not the ledger handing a job out twice. Same assertion, opposite directions, unrelated causes.
        (await host.Ledger.Query(new JobQuery(WorkType: wt, Status: JobStatus.Queued), default))
            .Should().BeEmpty("every seeded job was claimable and no claimer should have stopped early");

        claimed.Should().HaveCount(jobCount);                // every job claimed
        claimed.Distinct().Should().HaveCount(jobCount);     // each claimed exactly once — no double-claim
    }

    [Fact]
    public async Task a_settled_job_gets_an_absolute_expiry()
    {
        // §20.4: a terminal row carries ExpireAt = LastSettledAt + its per-outcome window (Completed → ArchiveAfter).
        // TTL-capable stores (Mongo) expire on it automatically via the [Index(Ttl)]; the rest purge by it on the sweep.
        GreetJob.Reset();
        await using var host = await CreateHostAsync(o => o.ArchiveAfter = TimeSpan.FromDays(3));
        var j = new GreetJob { Name = "x" };
        var id = j.Id;
        await j.Job.Submit();
        await host.Drain();

        var rec = await host.JobFor<GreetJob>(id);
        rec!.Status.Should().Be(JobStatus.Completed);
        rec.ExpireAt.Should().Be(rec.LastSettledAt!.Value + TimeSpan.FromDays(3));
    }

    // --- dispatch-time pool gates (JOBS-0007) ---

    [Fact]
    public async Task pool_job_is_dispatched_to_a_free_member()
    {
        PoolJob.Reset();
        var resolver = new TestPoolResolver(new[] { "server-a", "server-b" });
        await using var host = await CreateHostAsync(
            configureServices: s => s.AddSingleton<IJobPoolResolver>(resolver));
        var j = new PoolJob();
        var id = j.Id;
        await j.Job.Submit();
        await host.Drain();
        PoolJob.Executions.Should().Be(1);
        (await host.StatusOf<PoolJob>(id)).Should().Be(JobStatus.Completed);
        var rec = await host.JobFor<PoolJob>(id);
        rec!.GateKey.Should().BeOneOf("server-a", "server-b");
        rec.PoolKey.Should().Be("test-ai-servers");
    }

    [Fact]
    public async Task pool_gatekey_is_null_at_submit_and_stamped_at_claim()
    {
        var resolver = new TestPoolResolver(new[] { "server-a" });
        await using var host = await CreateHostAsync(
            configureServices: s => s.AddSingleton<IJobPoolResolver>(resolver));
        var j = new PoolJob();
        var id = j.Id;
        await j.Job.Submit();

        var queued = await host.JobFor<PoolJob>(id);
        queued!.GateKey.Should().BeNull("pool job GateKey is unresolved at submit");
        queued.PoolKey.Should().Be("test-ai-servers");

        await host.Drain();

        var done = await host.JobFor<PoolJob>(id);
        done!.GateKey.Should().NotBeNull("pool job GateKey is stamped at claim time");
        done.GateKey.Should().Be("server-a");
    }

    [Fact]
    public async Task pooled_job_sees_the_elected_member_as_its_claimed_gate()
    {
        // The queued row has no gate key, yet the handler's snapshot carries the member elected at claim:
        // State.GateKey projects THIS claim, so pool work can self-identify its dispatch target without
        // querying the ledger.
        PoolJob.Reset();
        var resolver = new TestPoolResolver(new[] { "server-a" });
        await using var host = await CreateHostAsync(
            configureServices: s => s.AddSingleton<IJobPoolResolver>(resolver));
        var j = new PoolJob();
        var id = j.Id;
        await j.Job.Submit();

        var queued = await host.JobFor<PoolJob>(id);
        queued!.GateKey.Should().BeNull("the member is elected only at claim time");

        await host.Drain();

        PoolJob.ClaimedGates.Single(o => o.WorkId == j.Id).GateKey.Should().Be("server-a",
            "the snapshot carries the elected member although the queued key was null");
        (await host.StatusOf<PoolJob>(id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task pool_all_slots_full_blocks_claim()
    {
        var resolver = new TestPoolResolver(new[] { "server-a", "server-b" }) { CapacityPerMember = 1 };
        await using var host = await CreateHostAsync(
            configureServices: s => s.AddSingleton<IJobPoolResolver>(resolver));

        var j = new PoolJob();
        await j.Job.Submit();

        var now = host.Clock.GetUtcNow();
        var wt = typeof(PoolJob).FullName!;
        await host.Ledger.AppendMany(new[]
        {
            new JobRecord { WorkType = wt, WorkId = "occupied-a", Action = "", Status = JobStatus.Running, Lane = "default", VisibleAt = now, FirstSubmittedAt = now, Exclusive = true, GateKey = "server-a", Owner = "other", LeaseUntil = now.AddMinutes(10) },
            new JobRecord { WorkType = wt, WorkId = "occupied-b", Action = "", Status = JobStatus.Running, Lane = "default", VisibleAt = now, FirstSubmittedAt = now, Exclusive = true, GateKey = "server-b", Owner = "other", LeaseUntil = now.AddMinutes(10) },
        }, default);

        var pools = new Dictionary<string, PoolDispatchContext>
        {
            ["test-ai-servers"] = new PoolDispatchContext("test-ai-servers", new[] { "server-a", "server-b" }, 1),
        };
        var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(5), Array.Empty<string>(), default, pools);
        claimed.Should().BeNull("no slots are available when all pool members are at capacity");
    }

    [Fact]
    public async Task pool_dynamic_member_update_unblocks_queued_jobs()
    {
        PoolJob.Reset();
        var resolver = new TestPoolResolver(Array.Empty<string>());
        await using var host = await CreateHostAsync(
            configureServices: s => s.AddSingleton<IJobPoolResolver>(resolver));
        var j = new PoolJob();
        var id = j.Id;
        await j.Job.Submit();

        await host.Drain();
        PoolJob.Executions.Should().Be(0, "empty pool: no member to dispatch to");
        (await host.StatusOf<PoolJob>(id)).Should().Be(JobStatus.Queued);

        resolver.Members = new[] { "server-a" };
        await host.Drain();

        PoolJob.Executions.Should().Be(1, "pool member added at runtime unblocks the queued job");
        (await host.StatusOf<PoolJob>(id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task pool_capacity_per_member_allows_multiple_concurrent_claims()
    {
        var resolver = new TestPoolResolver(new[] { "server-a" }) { CapacityPerMember = 2 };
        await using var host = await CreateHostAsync(
            configureServices: s => s.AddSingleton<IJobPoolResolver>(resolver));

        var j1 = new PoolJob();
        var j2 = new PoolJob();
        await j1.Job.Submit();
        await j2.Job.Submit();

        var now = host.Clock.GetUtcNow();
        var pools = new Dictionary<string, PoolDispatchContext>
        {
            ["test-ai-servers"] = new PoolDispatchContext("test-ai-servers", new[] { "server-a" }, 2),
        };
        var first  = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(5), Array.Empty<string>(), default, pools);
        var second = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(5), Array.Empty<string>(), default, pools);

        first.Should().NotBeNull();
        second.Should().NotBeNull("capacity=2 allows two concurrent claims on the same member");
        first!.GateKey.Should().Be("server-a");
        second!.GateKey.Should().Be("server-a");
    }

    [Fact]
    public async Task pool_job_admission_is_governed_by_pool_capacity_not_resource_gates()
    {
        // ARCH-0079 convergence: a [JobPool] row is admitted by pool-member capacity, not by [JobGate] resource gates
        // (its GateKey is the elected member, not a backoff key). Both ledger tiers must agree — a gate that happens to
        // name a pool member key must not block a pool job the pool can still serve.
        PoolJob.Reset();
        var resolver = new TestPoolResolver(new[] { "server-a" });
        await using var host = await CreateHostAsync(
            configureServices: s => s.AddSingleton<IJobPoolResolver>(resolver));
        var now = host.Clock.GetUtcNow();
        var poolWt = typeof(PoolJob).FullName!;

        await host.Ledger.SetGate("server-a", now.AddMinutes(10), "unrelated gate on the member key", default);

        // a queued pool job that (atypically) already carries a GateKey equal to the gated member key.
        await host.Ledger.Append(new JobRecord
        {
            Id = $"{poolWt}:g0", WorkType = poolWt, WorkId = "g0", Action = "",
            Status = JobStatus.Queued, PoolKey = "test-ai-servers", GateKey = "server-a", Exclusive = true,
            VisibleAt = now, FirstSubmittedAt = now,
        }, default);

        var pools = new Dictionary<string, PoolDispatchContext>
        {
            ["test-ai-servers"] = new("test-ai-servers", new[] { "server-a" }, 1),
        };
        var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), Array.Empty<string>(), default, pools);

        claimed.Should().NotBeNull("a pool job is governed by pool capacity, not a resource gate on its member key");
        claimed!.WorkId.Should().Be("g0");
    }

    // --- head-of-line starvation: the claim scan must page past unclaimable work (JOBS-0007 regression) ---
    // A window's worth of unclaimable rows at the FIFO head (an exhausted pool, a saturated lane, gated keys, or a
    // busy exclusive work-item) must not consume the durable ledger's bounded scan and strand runnable work queued
    // behind it. These specs set a tiny ClaimScanBatch and seed MORE than one window of unclaimable rows ahead of the
    // runnable work, so the windowed durable ledger reproduces the stall; the in-memory ledger (full scan) is already
    // immune, which is exactly the convergence the fix restores.

    [Fact]
    public async Task exhausted_pool_does_not_starve_other_work_in_the_claim_scan()
    {
        // The live incident: a [JobPool] with zero available members backed up 1000+ unclaimable jobs at the oldest
        // VisibleAt; runnable non-pool jobs behind them never got claimed and the whole pipeline stalled for ~18h.
        PoolJob.Reset();
        GreetJob.Reset();
        var resolver = new TestPoolResolver(Array.Empty<string>());   // exhausted: no members
        await using var host = await CreateHostAsync(
            o => o.ClaimScanBatch = 3,
            s => s.AddSingleton<IJobPoolResolver>(resolver));

        var now = host.Clock.GetUtcNow();
        var poolWt = typeof(PoolJob).FullName!;

        // 8 unclaimable pool jobs hold the oldest VisibleAt — more than two claim-scan windows.
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 8).Select(i => new JobRecord
            {
                Id = $"{poolWt}:stuck{i}", WorkType = poolWt, WorkId = $"stuck{i}", Action = "",
                Status = JobStatus.Queued, PoolKey = "test-ai-servers", Exclusive = true,
                VisibleAt = now.AddMinutes(-10).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-10).AddMilliseconds(i),
            }).ToList(), default);

        // 3 runnable non-pool jobs queued behind them (newer VisibleAt).
        var greets = Enumerable.Range(0, 3).Select(i => new GreetJob { Name = $"n{i}" }).ToList();
        foreach (var g in greets) await g.Job.Submit();

        await host.Drain();

        GreetJob.Executions.Should().Be(3, "runnable non-pool work must not be starved by an exhausted pool's backlog");
        PoolJob.Executions.Should().Be(0, "the pool is exhausted — its jobs stay queued");
        foreach (var g in greets)
            (await host.StatusOf<GreetJob>(g.Id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task saturated_lane_does_not_starve_claims_for_other_lanes()
    {
        await using var host = await CreateHostAsync(o => o.ClaimScanBatch = 3);
        var now = host.Clock.GetUtcNow();
        var wt = typeof(GreetJob).FullName!;

        // 8 jobs on the saturated "slow" lane hold the oldest VisibleAt.
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 8).Select(i => new JobRecord
            {
                Id = $"{wt}:slow{i}", WorkType = wt, WorkId = $"slow{i}", Action = "",
                Status = JobStatus.Queued, Lane = "slow", Exclusive = true,
                VisibleAt = now.AddMinutes(-10).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-10).AddMilliseconds(i),
            }).ToList(), default);

        // one job on a free lane, queued behind them.
        await host.Ledger.Append(new JobRecord
        {
            Id = $"{wt}:free0", WorkType = wt, WorkId = "free0", Action = "",
            Status = JobStatus.Queued, Lane = "default", Exclusive = true,
            VisibleAt = now, FirstSubmittedAt = now,
        }, default);

        var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), new[] { "slow" }, default);

        claimed.Should().NotBeNull("a saturated lane must not block claims for other lanes");
        claimed!.WorkId.Should().Be("free0");
        claimed.Lane.Should().Be("default");
    }

    // --- cross-lane fairness (JOBS-0008): a perpetually-fed / older upstream lane must not monopolize the claim budget
    // and starve a downstream lane. Under the old global (VisibleAt, FirstSubmittedAt) order the oldest-N claims were
    // ALL the upstream lane's (a downstream chain job is stamped VisibleAt=now on each advance, so it is structurally
    // newer and never entered the oldest-N window). Weighted fair queuing per lane makes each lane's share guaranteed.
    // Runs on every tier — in-memory full-scan and durable per-lane indexed seek converge on the same selection.

    [Fact]
    public async Task fed_lane_does_not_starve_a_backlog_lane()
    {
        await using var host = await CreateHostAsync();
        var now = host.Clock.GetUtcNow();
        var crawlWt = typeof(CrawlJob).FullName!;
        var trWt = typeof(TranslationJob).FullName!;

        // upstream "crawl": 10 jobs holding the OLDEST VisibleAt (a continuously-fed head stage is always older).
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 10).Select(i => new JobRecord
            {
                Id = $"{crawlWt}:crawl{i}", WorkType = crawlWt, WorkId = $"crawl{i}", Action = CrawlJob.Crawl,
                Status = JobStatus.Queued, Lane = "crawl", Exclusive = true,
                VisibleAt = now.AddMinutes(-10).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-10).AddMilliseconds(i),
            }).ToList(), default);

        // downstream "translation": 5 jobs with NEWER VisibleAt (produced later in the pipeline).
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 5).Select(i => new JobRecord
            {
                Id = $"{trWt}:tr{i}", WorkType = trWt, WorkId = $"tr{i}", Action = TranslationJob.Translate,
                Status = JobStatus.Queued, Lane = "translation", Exclusive = true,
                VisibleAt = now.AddMinutes(-1).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-1).AddMilliseconds(i),
            }).ToList(), default);

        var lanes = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), Array.Empty<string>(), default);
            if (claimed is null) break;
            lanes.Add(claimed.Lane);
        }

        lanes.Count(l => l == "translation").Should().BeGreaterThan(0,
            "a downstream lane must get a fair share of the claim budget, not be starved by an older upstream lane");
    }

    [Fact]
    public async Task lane_weights_give_the_heavier_lane_a_larger_share()
    {
        await using var host = await CreateHostAsync(o =>
        {
            o.LaneWeights["crawl"] = 3;          // crawl gets ~3x translation's dispatch share
            o.LaneWeights["translation"] = 1;
        });
        var now = host.Clock.GetUtcNow();
        var crawlWt = typeof(CrawlJob).FullName!;
        var trWt = typeof(TranslationJob).FullName!;

        await host.Ledger.AppendMany(
            Enumerable.Range(0, 20).Select(i => new JobRecord
            {
                Id = $"{crawlWt}:c{i}", WorkType = crawlWt, WorkId = $"c{i}", Action = CrawlJob.Crawl,
                Status = JobStatus.Queued, Lane = "crawl", Exclusive = true,
                VisibleAt = now.AddMinutes(-5).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-5).AddMilliseconds(i),
            }).ToList(), default);
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 20).Select(i => new JobRecord
            {
                Id = $"{trWt}:t{i}", WorkType = trWt, WorkId = $"t{i}", Action = TranslationJob.Translate,
                Status = JobStatus.Queued, Lane = "translation", Exclusive = true,
                VisibleAt = now.AddMinutes(-5).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-5).AddMilliseconds(i),
            }).ToList(), default);

        int crawl = 0, tr = 0;
        for (var i = 0; i < 8; i++)
        {
            var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), Array.Empty<string>(), default);
            if (claimed is null) break;
            if (claimed.Lane == "crawl") crawl++; else if (claimed.Lane == "translation") tr++;
        }

        crawl.Should().BeGreaterThan(tr, "the higher-weighted lane gets the larger share (no strict priority — translation still runs)");
        tr.Should().BeGreaterThan(0, "weighting is proportional, not strict priority — the lighter lane is never starved");
    }

    // --- self-reporting (JOBS-0008): the Jobs pillar now ships an IHealthContributor so a stalled/starved lane is a
    // first-class signal (per-lane depth + oldest-queued-age), not an inference hours later. Resolved + Check()ed
    // directly (the same path StartupProbeService uses) so we assert on HealthReport.State, not the async aggregator.

    private static IHealthContributor JobsHealth(JobsHarness host)
        => host.Services.GetServices<IHealthContributor>().Single(c => c.Name == "Koan.Jobs");

    [Fact]
    public async Task jobs_health_reports_queued_and_running_facts()
    {
        await using var host = await CreateHostAsync();
        var now = host.Clock.GetUtcNow();
        var wt = typeof(CrawlJob).FullName!;
        await host.Ledger.AppendMany(Enumerable.Range(0, 3).Select(i => new JobRecord
        {
            Id = $"{wt}:q{i}", WorkType = wt, WorkId = $"q{i}", Action = CrawlJob.Crawl,
            Status = JobStatus.Queued, Lane = "crawl", VisibleAt = now.AddMinutes(-1), FirstSubmittedAt = now.AddMinutes(-1),
        }).ToList(), default);
        await host.Ledger.AppendMany(Enumerable.Range(0, 2).Select(i => new JobRecord
        {
            Id = $"{wt}:r{i}", WorkType = wt, WorkId = $"r{i}", Action = CrawlJob.Crawl,
            Status = JobStatus.Running, Lane = "crawl", Owner = "n", LeaseUntil = now.AddMinutes(1),
            VisibleAt = now.AddMinutes(-2), FirstSubmittedAt = now.AddMinutes(-2),
        }).ToList(), default);

        var r = await JobsHealth(host).Check();

        r.State.Should().Be(HealthState.Healthy);
        r.Data!["queued"].Should().Be(3L);
        r.Data!["running"].Should().Be(2L);
    }

    [Fact]
    public async Task jobs_health_flips_degraded_when_oldest_queued_age_exceeds_budget()
    {
        await using var host = await CreateHostAsync(o => o.QueueAgeWarning = TimeSpan.FromMinutes(5));
        var now = host.Clock.GetUtcNow();
        var wt = typeof(CrawlJob).FullName!;
        await host.Ledger.Append(new JobRecord
        {
            Id = $"{wt}:old", WorkType = wt, WorkId = "old", Action = CrawlJob.Crawl,
            Status = JobStatus.Queued, Lane = "crawl", VisibleAt = now.AddMinutes(-10), FirstSubmittedAt = now.AddMinutes(-10),
        }, default);

        var r = await JobsHealth(host).Check();

        r.State.Should().Be(HealthState.Degraded, "an oldest queued job past the budget is the starvation tripwire");
        r.Description.Should().Contain("budget");
    }

    [Fact]
    public async Task jobs_health_stays_healthy_when_budget_disabled_even_with_old_queued_work()
    {
        await using var host = await CreateHostAsync();   // QueueAgeWarning default = Zero (off)
        var now = host.Clock.GetUtcNow();
        var wt = typeof(CrawlJob).FullName!;
        await host.Ledger.Append(new JobRecord
        {
            Id = $"{wt}:old", WorkType = wt, WorkId = "old", Action = CrawlJob.Crawl,
            Status = JobStatus.Queued, Lane = "crawl", VisibleAt = now.AddMinutes(-30), FirstSubmittedAt = now.AddMinutes(-30),
        }, default);

        var r = await JobsHealth(host).Check();

        r.State.Should().Be(HealthState.Healthy, "the budget is opt-in — facts are always published, but no Degraded without one");
        r.Data!["oldestQueuedAgeSeconds"].Should().Be(1800L);
    }

    [Fact]
    public async Task jobs_health_counts_lapsed_lease_reclaim_backlog()
    {
        await using var host = await CreateHostAsync();
        var now = host.Clock.GetUtcNow();
        var wt = typeof(CrawlJob).FullName!;
        await host.Ledger.Append(new JobRecord
        {
            Id = $"{wt}:stuck", WorkType = wt, WorkId = "stuck", Action = CrawlJob.Crawl,
            Status = JobStatus.Running, Lane = "crawl", Owner = "dead-node", LeaseUntil = now.AddMinutes(-1),
            VisibleAt = now.AddMinutes(-5), FirstSubmittedAt = now.AddMinutes(-5),
        }, default);

        var r = await JobsHealth(host).Check();

        r.Data!["reclaimBacklog"].Should().Be(1L);
    }

    [Fact]
    public async Task active_gate_does_not_starve_ungated_claims()
    {
        await using var host = await CreateHostAsync(o => o.ClaimScanBatch = 3);
        var now = host.Clock.GetUtcNow();
        var wt = typeof(GreetJob).FullName!;

        await host.Ledger.SetGate("host:throttled", now.AddMinutes(10), "429", default);

        // 8 gated jobs (under the active gate) hold the oldest VisibleAt.
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 8).Select(i => new JobRecord
            {
                Id = $"{wt}:gated{i}", WorkType = wt, WorkId = $"gated{i}", Action = "",
                Status = JobStatus.Queued, GateKey = "host:throttled", Exclusive = true,
                VisibleAt = now.AddMinutes(-10).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-10).AddMilliseconds(i),
            }).ToList(), default);

        await host.Ledger.Append(new JobRecord
        {
            Id = $"{wt}:ungated0", WorkType = wt, WorkId = "ungated0", Action = "",
            Status = JobStatus.Queued, Exclusive = true, VisibleAt = now, FirstSubmittedAt = now,
        }, default);

        var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), Array.Empty<string>(), default);

        claimed.Should().NotBeNull("an active gate must not block claims for ungated work");
        claimed!.WorkId.Should().Be("ungated0");
    }

    [Fact]
    public async Task busy_exclusive_work_item_does_not_starve_other_work_items()
    {
        // The exclusive/busy vector is a set of (WorkType, WorkId) TUPLES — it can't be a scalar store-side filter, so
        // paging (not predicate-pushdown) is what keeps it from starving. One work-item runs; a backlog of its own
        // queued duplicates must not block a different work-item behind them.
        await using var host = await CreateHostAsync(o => o.ClaimScanBatch = 3);
        var now = host.Clock.GetUtcNow();
        var wt = typeof(GreetJob).FullName!;

        await host.Ledger.Append(new JobRecord
        {
            Id = $"{wt}:hot:running", WorkType = wt, WorkId = "hot", Action = "",
            Status = JobStatus.Running, Exclusive = true, Owner = "other", LeaseUntil = now.AddMinutes(10),
            VisibleAt = now.AddMinutes(-20), FirstSubmittedAt = now.AddMinutes(-20),
        }, default);

        // 8 more exclusive jobs queued for the SAME (busy) work-item, oldest VisibleAt.
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 8).Select(i => new JobRecord
            {
                Id = $"{wt}:hot:q{i}", WorkType = wt, WorkId = "hot", Action = "",
                Status = JobStatus.Queued, Exclusive = true,
                VisibleAt = now.AddMinutes(-10).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-10).AddMilliseconds(i),
            }).ToList(), default);

        // a different work-item, queued behind them.
        await host.Ledger.Append(new JobRecord
        {
            Id = $"{wt}:cold:q0", WorkType = wt, WorkId = "cold", Action = "",
            Status = JobStatus.Queued, Exclusive = true, VisibleAt = now, FirstSubmittedAt = now,
        }, default);

        var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), Array.Empty<string>(), default);

        claimed.Should().NotBeNull("a busy exclusive work-item must not block claims for other work-items");
        claimed!.WorkId.Should().Be("cold");
    }

    [Fact]
    public async Task claim_preserves_fifo_among_claimable_jobs_behind_an_unclaimable_head()
    {
        // Paging past the unclaimable head must not disturb FIFO order among the claimable jobs it reaches.
        await using var host = await CreateHostAsync(
            o => o.ClaimScanBatch = 3,
            s => s.AddSingleton<IJobPoolResolver>(new TestPoolResolver(Array.Empty<string>())));
        var now = host.Clock.GetUtcNow();
        var poolWt = typeof(PoolJob).FullName!;
        var wt = typeof(GreetJob).FullName!;

        await host.Ledger.AppendMany(
            Enumerable.Range(0, 8).Select(i => new JobRecord
            {
                Id = $"{poolWt}:stuck{i}", WorkType = poolWt, WorkId = $"stuck{i}", Action = "",
                Status = JobStatus.Queued, PoolKey = "test-ai-servers", Exclusive = true,
                VisibleAt = now.AddMinutes(-10).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-10).AddMilliseconds(i),
            }).ToList(), default);

        // claimable non-pool jobs in a known order: newer than the stuck head, but still visible (in the past).
        await host.Ledger.AppendMany(
            Enumerable.Range(0, 3).Select(i => new JobRecord
            {
                Id = $"{wt}:r{i}", WorkType = wt, WorkId = $"r{i}", Action = "",
                Status = JobStatus.Queued, Exclusive = true,
                VisibleAt = now.AddMinutes(-1).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-1).AddMilliseconds(i),
            }).ToList(), default);

        var pools = new Dictionary<string, PoolDispatchContext>
        {
            ["test-ai-servers"] = new("test-ai-servers", Array.Empty<string>(), 1),
        };
        var first  = await host.Ledger.ClaimNext("o", now, now.AddMinutes(1), Array.Empty<string>(), default, pools);
        var second = await host.Ledger.ClaimNext("o", now, now.AddMinutes(1), Array.Empty<string>(), default, pools);

        first!.WorkId.Should().Be("r0", "the FIFO head of the claimable set is claimed first");
        second!.WorkId.Should().Be("r1");
    }

    [Fact]
    public async Task exhausted_pool_backlog_larger_than_scan_window_returns_null_without_claiming()
    {
        // The paging scan must terminate at end-of-ready-set and return null when nothing is claimable — it must not
        // hang, and it must not over-claim an unclaimable pool job.
        PoolJob.Reset();
        var resolver = new TestPoolResolver(Array.Empty<string>());
        await using var host = await CreateHostAsync(
            o => o.ClaimScanBatch = 3,
            s => s.AddSingleton<IJobPoolResolver>(resolver));
        var now = host.Clock.GetUtcNow();
        var poolWt = typeof(PoolJob).FullName!;

        await host.Ledger.AppendMany(
            Enumerable.Range(0, 10).Select(i => new JobRecord
            {
                Id = $"{poolWt}:p{i}", WorkType = poolWt, WorkId = $"p{i}", Action = "",
                Status = JobStatus.Queued, PoolKey = "test-ai-servers", Exclusive = true,
                VisibleAt = now.AddMinutes(-5).AddMilliseconds(i), FirstSubmittedAt = now.AddMinutes(-5).AddMilliseconds(i),
            }).ToList(), default);

        var pools = new Dictionary<string, PoolDispatchContext>
        {
            ["test-ai-servers"] = new("test-ai-servers", Array.Empty<string>(), 1),
        };
        var claimed = await host.Ledger.ClaimNext("owner", now, now.AddMinutes(1), Array.Empty<string>(), default, pools);

        claimed.Should().BeNull("no member is available — the scan reaches end-of-ready-set and returns null");
        (await host.Ledger.Query(new JobQuery(WorkType: poolWt), default))
            .Should().OnlyContain(r => r.Status == JobStatus.Queued, "nothing was claimed");
    }

    private static IReadOnlyCollection<JobRecord> Seed(string workType, int count, JobStatus status, DateTimeOffset baseTime, string idPrefix, bool visible = false)
    {
        var list = new List<JobRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var at = status >= JobStatus.Completed ? baseTime.AddSeconds(-i - 1)
                   : visible ? baseTime.AddMilliseconds(i)   // all in the past, strictly ordered (q0 = FIFO head)
                   : baseTime;
            list.Add(Rec(workType, $"{idPrefix}{i}", status, at));
        }
        return list;
    }

    private static JobRecord Rec(string workType, string workId, JobStatus status, DateTimeOffset at) => new()
    {
        Id = $"{workType}:{workId}",
        WorkType = workType,
        WorkId = workId,
        Action = "",
        Status = status,
        VisibleAt = at,
        FirstSubmittedAt = at,
        LastSettledAt = status >= JobStatus.Completed ? at : null,
        Exclusive = true,
    };
}
