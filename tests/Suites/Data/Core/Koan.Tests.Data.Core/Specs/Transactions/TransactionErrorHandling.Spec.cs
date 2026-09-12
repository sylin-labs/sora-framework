using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Core;
using Koan.Data.Core.Lifecycle;
using Koan.Data.Core.Model;
using Koan.Data.Core.Transactions;
using Koan.Tests.Data.Core.Support;
using AwesomeAssertions;

namespace Koan.Tests.Data.Core.Specs.Transactions;

/// <summary>
/// Transaction error handling and edge case tests.
/// Tests exception handling, partial failures, and edge cases.
/// </summary>
public sealed class TransactionErrorHandlingSpec
{
    private readonly ITestOutputHelper _output;

    public TransactionErrorHandlingSpec(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    // NOTE: invalid-transition behavior (Commit/Rollback without an active transaction, double-commit,
    // commit-after-rollback) is an idempotent NO-OP — see TransactionStateValidationSpec. The former
    // "*_throws_exception" tests here were removed when that contract was settled (no-op, not throw).

    [Fact]
    public async Task Empty_transaction_commits_successfully()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var success = false;

        // Transaction with no operations
        using (EntityContext.Transaction("empty-transaction"))
        {
            await EntityContext.Commit();
            success = true;
        }

        success.Should().BeTrue("empty transaction should commit successfully");
    }

    [Fact]
    public async Task Failed_commit_reports_and_preserves_the_completed_prefix_without_replay()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync(configureServices: _ =>
            DeferredFailureEntity.Lifecycle.BeforeUpsert(context =>
            {
                if (context.Current.FailOnSave)
                    throw new InvalidOperationException("deliberate second-operation failure");
                return EntityLifecycleResult.Proceed();
            }));

        var partition = $"tx-partial-{Guid.CreateVersion7():n}";
        var first = new DeferredFailureEntity { Id = "first" };
        var second = new DeferredFailureEntity { Id = "second", FailOnSave = true };
        var third = new DeferredFailureEntity { Id = "third" };
        TransactionException failure;

        using (EntityContext.Partition(partition))
        using (EntityContext.Transaction("partial-prefix"))
        {
            await first.Save();
            await second.Save();
            await third.Save();

            var commit = () => EntityContext.Commit();
            failure = (await commit.Should().ThrowAsync<TransactionException>()).Which;
        }

        failure.CompletedOperationCount.Should().Be(1);
        failure.CommitOutcome.Should().Be(DataCommitOutcome.Unknown);
        failure.RetryDisposition.Should().Be(DataRetryDisposition.Never);
        failure.ReplayDisposition.Should().Be(DataReplayDisposition.Never);

        using (EntityContext.Partition(partition))
        {
            (await DeferredFailureEntity.Get(first.Id)).Should().NotBeNull(
                "deferred coordination does not roll back an operation completed before a later failure");
            (await DeferredFailureEntity.Get(second.Id)).Should().BeNull();
            (await DeferredFailureEntity.Get(third.Id)).Should().BeNull(
                "the coordinator stops at the failed operation and never dispatches or replays the suffix");
        }
    }

    [Fact]
    public async Task Transaction_with_same_entity_saved_multiple_times()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var partition = $"tx-error-handling-{Guid.CreateVersion7():n}";

        var entity = new TodoEntity { Title = "Original", Description = "Version 1" };

        using (EntityContext.Partition(partition))
        {
            using (EntityContext.Transaction("multiple-saves"))
            {
                await entity.Save();

                entity.Description = "Version 2";
                await entity.Save();

                entity.Description = "Version 3";
                await entity.Save();

                await EntityContext.Commit();
            }

            // Verify final version was persisted
            var retrieved = await TodoEntity.Get(entity.Id);
            retrieved.Should().NotBeNull();
            retrieved!.Description.Should().Be("Version 3", "last save should win");
        }

        entity.Should().NotBeNull();
    }

    [Fact]
    public async Task Transaction_context_restored_after_exception()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var beforeTransaction = EntityContext.InTransaction;

        try
        {
            using (EntityContext.Transaction("exception-test"))
            {
                // Throw exception inside transaction
                throw new InvalidOperationException("Simulated error");
            }
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        var afterTransaction = EntityContext.InTransaction;

        beforeTransaction.Should().BeFalse();
        afterTransaction.Should().BeFalse("context should be restored after exception");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Transaction_with_partition_routing()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var executionId = Guid.CreateVersion7().ToString("n");
        var partition1 = $"partition-1-{executionId}";
        var partition2 = $"partition-2-{executionId}";

        var entity1 = new TodoEntity { Title = "Partition 1", Description = "In partition 1" };
        var entity2 = new TodoEntity { Title = "Partition 2", Description = "In partition 2" };

        // Transaction across multiple partitions
        using (EntityContext.Transaction("multi-partition"))
        {
            using (EntityContext.Partition(partition1))
            {
                await entity1.Save();
            }

            using (EntityContext.Partition(partition2))
            {
                await entity2.Save();
            }

            await EntityContext.Commit();
        }

        // Verify entities in respective partitions
        using (EntityContext.Partition(partition1))
        {
            var retrieved1 = await TodoEntity.Get(entity1.Id);
            retrieved1.Should().NotBeNull("entity should be in partition 1");

            var count1 = await TodoEntity.Count;
            count1.Should().Be(1);
        }

        using (EntityContext.Partition(partition2))
        {
            var retrieved2 = await TodoEntity.Get(entity2.Id);
            retrieved2.Should().NotBeNull("entity should be in partition 2");

            var count2 = await TodoEntity.Count;
            count2.Should().Be(1);
        }

        entity1.Should().NotBeNull();
        entity2.Should().NotBeNull();
    }

    [Fact]
    public async Task Transaction_with_adapter_and_partition_routing()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var partition = $"tx-error-handling-{Guid.CreateVersion7():n}";

        var entity1 = new TodoEntity { Title = "SQLite + Partition", Description = "Default adapter with partition" };
        var entity2 = new TodoEntity { Title = "JSON + Partition", Description = "JSON adapter with partition" };

        // Transaction with adapter AND partition routing
        using (EntityContext.Transaction("adapter-partition"))
        {
            // Default adapter with partition
            using (EntityContext.Partition(partition))
            {
                await entity1.Save();
            }

            // JSON adapter with partition
            using (EntityContext.Adapter("json"))
            using (EntityContext.Partition(partition))
            {
                await entity2.Save();
            }

            await EntityContext.Commit();
        }

        // Verify entities
        using (EntityContext.Partition(partition))
        {
            var retrieved1 = await TodoEntity.Get(entity1.Id);
            retrieved1.Should().NotBeNull();
        }

        using (EntityContext.Adapter("json"))
        using (EntityContext.Partition(partition))
        {
            var retrieved2 = await TodoEntity.Get(entity2.Id);
            retrieved2.Should().NotBeNull();
        }

        entity1.Should().NotBeNull();
        entity2.Should().NotBeNull();
    }

    private sealed class DeferredFailureEntity : Entity<DeferredFailureEntity>
    {
        public bool FailOnSave { get; set; }
    }
}
