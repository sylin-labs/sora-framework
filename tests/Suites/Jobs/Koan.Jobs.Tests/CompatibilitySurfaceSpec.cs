using AwesomeAssertions;
using Koan.Data.Core.Model;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Jobs.Tests;

public sealed class CompatibilitySurfaceSpec
{
    [Fact]
    public void Host_owned_runtime_implementations_are_not_public_surface()
    {
        Type[] runtimeTypes =
        [
            typeof(JobCoordinator),
            typeof(JobOrchestrator),
            typeof(JobScheduler),
            typeof(JobTypeRegistry),
            typeof(JobTypeBinding),
            typeof(DataJobLedger),
            typeof(InMemoryJobLedger),
            typeof(RoutingJobLedger),
            typeof(LaneFairSelector),
        ];

        runtimeTypes.Should().OnlyContain(type => type.IsNotPublic,
            "applications compose IJobCoordinator/IJobLedger while Jobs owns their host runtime");
    }

    [Fact]
    public void Metrics_expose_summary_intent_without_exporting_the_persistence_entity()
    {
        typeof(JobMetrics).IsPublic.Should().BeTrue();
        typeof(JobMetric).IsNotPublic.Should().BeTrue();
        typeof(JobMetric).GetProperty(nameof(JobMetric.Count))!.GetMethod!.IsPublic.Should().BeTrue(
            "the existing persisted field remains unchanged inside the non-exported row");
    }

    [Fact]
    public void JobState_keeps_its_ten_slot_constructor_and_deconstruction()
    {
        // The old call shape is the compatibility contract: GateKey is a non-positional projection member,
        // so neither the constructor arity nor the Deconstruct order gains an eleventh slot.
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var state = new JobState(JobStatus.Running, "stage", 2, 1, at, at, "boom", "retrying", at, "corr-1");

        state.GateKey.Should().BeNull("GateKey is projected by the orchestrator, not passed positionally");

        var (status, action, attempt, reschedules, firstSubmittedAt, lastSettledAt, lastError, deferReason, deadline, correlationId) = state;
        status.Should().Be(JobStatus.Running);
        action.Should().Be("stage");
        attempt.Should().Be(2);
        reschedules.Should().Be(1);
        firstSubmittedAt.Should().Be(at);
        lastSettledAt.Should().Be(at);
        lastError.Should().Be("boom");
        deferReason.Should().Be("retrying");
        deadline.Should().Be(at);
        correlationId.Should().Be("corr-1");

        var retargeted = state with { GateKey = "member-a" };
        retargeted.GateKey.Should().Be("member-a");
        retargeted.Action.Should().Be(state.Action, "non-destructive mutation keeps every positional slot");
    }

    [Fact]
    public void JobState_snapshot_identity_includes_the_captured_gate()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bare = new JobState(JobStatus.Running, "stage", 1, 0, at, null, null, null, null, null);
        var gated = bare with { GateKey = "member-a" };

        gated.Should().NotBe(bare, "the captured gate is part of the snapshot, like every other field");
    }

    [Fact]
    public void DataStore_requirement_rejects_a_host_without_durable_data()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new JobTypeRegistry([typeof(DurableRequirementProbe<int>)]));
        services.AddKoanJobs(options => options.EnableWorker = false);
        using var provider = services.BuildServiceProvider();

        Action resolve = () => provider.GetRequiredService<IJobLedger>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot honor [JobPersistence(DataStore)]*")
            .WithMessage("*no durable Data adapter*");
    }
}

[JobPersistence(JobPersistenceMode.DataStore)]
public sealed class DurableRequirementProbe<T> : Entity<DurableRequirementProbe<T>>, IKoanJob<DurableRequirementProbe<T>>
{
    public static Task Execute(DurableRequirementProbe<T> job, JobContext context, CancellationToken ct)
        => Task.CompletedTask;
}
