using Koan.Core.Context;
using Koan.Data.Core;

namespace Koan.Jobs.Tests;

public sealed class JobDataRouteSpec
{
    [Fact]
    public void explicit_default_source_preserves_legacy_work_identity()
    {
        var route = JobDataRoute.From(new EntityContext.ContextState(source: "DEFAULT"));
        route.Should().Be(default(JobDataRoute));
        route.Fold("legacy-key").Should().Be("legacy-key");
    }

    [Fact]
    public void query_source_filters_use_the_same_normalization_as_submitted_addresses()
    {
        JobLedgerPredicates.ForQuery(new JobQuery { WorkSource = "DEFAULT" }).Compile()(new JobRecord())
            .Should().BeTrue();
        JobLedgerPredicates.ForQuery(new JobQuery { WorkSource = "ANALYTICS" }).Compile()(new JobRecord { WorkSource = "analytics" })
            .Should().BeTrue();
        JobLedgerPredicates.ForQuery(new JobQuery { WorkAdapter = "MONGO" }).Compile()(new JobRecord { WorkAdapter = "mongo" })
            .Should().BeTrue();
    }

    [Fact]
    public void execution_restores_adapter_and_partition_without_a_submission_transaction()
    {
        using var outer = KoanContext.Push(new EntityContext.ContextState(source: "other", partition: "de", transaction: "caller"));
        var route = JobDataRoute.From(new JobRecord { WorkAdapter = "SQLITE", WorkPartition = "fr" });
        using (route.Restore())
        {
            EntityContext.Current!.Source.Should().BeNullOrEmpty();
            EntityContext.Current.Adapter.Should().Be("sqlite");
            EntityContext.Current.Partition.Should().Be("fr");
            EntityContext.InTransaction.Should().BeFalse();
        }
        EntityContext.Current!.Source.Should().Be("other");
        EntityContext.Current.Transaction.Should().Be("caller");
    }

    [Fact]
    public void scheduling_scope_clears_work_route_but_keeps_transaction_state()
    {
        var context = new EntityContext.ContextState(source: "other", partition: "fr", transaction: "submission");
        using var outer = KoanContext.Push(context);
        using (JobsStorageScope.Enter())
        {
            EntityContext.Current!.Source.Should().BeNull();
            EntityContext.Current.Adapter.Should().BeNull();
            EntityContext.Current.Partition.Should().BeNull();
            EntityContext.Current.Transaction.Should().Be("submission");
        }
        EntityContext.Current.Should().BeSameAs(context);
    }

    [Theory]
    [InlineData("invalid/partition", null, null)]
    [InlineData("fr", "named", "mongo")]
    [InlineData("fr", "Default", "mongo")]
    public void invalid_persisted_route_is_rejected_before_work_loading(string partition, string? source, string? adapter)
    {
        Action restore = () =>
        {
            var route = JobDataRoute.From(new JobRecord { WorkSource = source, WorkAdapter = adapter, WorkPartition = partition });
            using var scope = route.Restore();
        };
        restore.Should().Throw<Exception>();
    }
}
