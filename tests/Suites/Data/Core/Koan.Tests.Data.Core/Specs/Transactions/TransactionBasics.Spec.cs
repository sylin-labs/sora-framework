using Koan.Data.Abstractions;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Data.Core.Transactions;
using Koan.Tests.Data.Core.Support;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Data.Core.Specs.Transactions;

/// <summary>
/// Basic transaction functionality tests.
/// Tests defer-and-commit, rollback, and auto-commit behaviors.
/// </summary>
public sealed class TransactionBasicsSpec
{
    [Fact]
    public async Task Insert_only_rejects_deferred_coordination_without_tracking_or_persistence()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var model = new TodoEntity { Id = Guid.NewGuid().ToString("N"), Title = "Not inserted" };
        var repository = runtime.Services.GetRequiredService<IDataService>().GetRepository<TodoEntity, string>();
        using (EntityContext.Transaction("insert-not-deferred"))
        {
            await ((Func<Task>)(() => ((IInsertOnlyRepository<TodoEntity, string>)repository).Insert(model)))
                .Should().ThrowAsync<NotSupportedException>();
            await EntityContext.Commit();
        }
        (await TodoEntity.Get(model.Id)).Should().BeNull();
    }

    private readonly ITestOutputHelper _output;

    public TransactionBasicsSpec(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    [Fact]
    public async Task Deferred_save_and_delete_restore_default_route_when_commit_has_another_partition_and_adapter()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        var saved = new TodoEntity { Title = "default route" };
        var deleted = new TodoEntity { Title = "delete from default route" };
        await deleted.Save();

        using (EntityContext.Transaction("captured-default-route"))
        {
            await saved.Save();
            await deleted.Remove();
            using (EntityContext.Adapter("json"))
            using (EntityContext.Partition("different-at-commit")) await EntityContext.Commit();
        }

        (await TodoEntity.Get(saved.Id))!.Title.Should().Be("default route");
        (await TodoEntity.Get(deleted.Id)).Should().BeNull();
        using (EntityContext.Adapter("json"))
        using (EntityContext.Partition("different-at-commit"))
            (await TodoEntity.Get(saved.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Transaction_defers_entity_saves_until_commit()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var partition = $"data-core-transactions-{Guid.CreateVersion7():n}";

        var entity1 = new TodoEntity
        {
            Title = "Transaction Test 1",
            Description = "Should be saved after commit"
        };

        var entity2 = new TodoEntity
        {
            Title = "Transaction Test 2",
            Description = "Should also be saved after commit"
        };

        using (var _ = EntityContext.Partition(partition))
        {
            // Inside transaction - saves are deferred
            using (EntityContext.Transaction("test-transaction"))
            {
                await entity1.Save();
                await entity2.Save();

                // Query immediately - entities should NOT be persisted yet
                var countDuringTransaction = await TodoEntity.Count;
                countDuringTransaction.Should().Be(0, "entities should not be persisted during transaction");

                // Commit explicitly
                await EntityContext.Commit();
            }

            // After commit - entities should be persisted
            var countAfterCommit = await TodoEntity.Count;
            countAfterCommit.Should().Be(2, "entities should be persisted after commit");

            var retrieved1 = await TodoEntity.Get(entity1.Id);
            retrieved1.Should().NotBeNull();
            retrieved1!.Title.Should().Be("Transaction Test 1");

            var retrieved2 = await TodoEntity.Get(entity2.Id);
            retrieved2.Should().NotBeNull();
            retrieved2!.Title.Should().Be("Transaction Test 2");
        }

        entity1.Should().NotBeNull();
        entity2.Should().NotBeNull();
    }

    [Fact]
    public async Task Transaction_rollback_discards_pending_changes()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var partition = $"data-core-transactions-{Guid.CreateVersion7():n}";

        var entity = new TodoEntity
        {
            Title = "Should be rolled back",
            Description = "This should never be persisted"
        };

        using (var _ = EntityContext.Partition(partition))
        {
            // Transaction with rollback
            using (EntityContext.Transaction("rollback-test"))
            {
                await entity.Save();

                // Rollback explicitly
                await EntityContext.Rollback();
            }

            // After rollback - entity should NOT be persisted
            var count = await TodoEntity.Count;
            count.Should().Be(0, "entity should not be persisted after rollback");

            var retrieved = await TodoEntity.Get(entity.Id);
            retrieved.Should().BeNull("entity should not exist after rollback");
        }

        entity.Should().NotBeNull();
    }

    [Fact]
    public async Task Transaction_rolls_back_on_dispose_without_explicit_commit()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var partition = $"data-core-transactions-{Guid.CreateVersion7():n}";

        var entity = new TodoEntity
        {
            Title = "Implicit rollback test",
            Description = "Should be discarded on dispose without an explicit Commit"
        };

        using (var _ = EntityContext.Partition(partition))
        {
            // Default contract (TransactionOptions.AutoCommitOnDispose = false, matching .NET
            // TransactionScope): leaving the using-block WITHOUT an explicit Commit() rolls back.
            using (EntityContext.Transaction("dispose-rollback-test"))
            {
                await entity.Save();
                // No explicit Commit/Rollback — dispose rolls back.
            }

            var count = await TodoEntity.Count;
            count.Should().Be(0, "pending work is discarded on dispose unless explicitly committed");

            var retrieved = await TodoEntity.Get(entity.Id);
            retrieved.Should().BeNull("entity should not exist after rollback-on-dispose");
        }
    }

    [Fact]
    public async Task Transaction_tracks_delete_operations()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        var partition = $"data-core-transactions-{Guid.CreateVersion7():n}";

        // First, create an entity outside transaction
        var entity = new TodoEntity
        {
            Title = "To be deleted",
            Description = "This will be deleted in transaction"
        };

        using (var _ = EntityContext.Partition(partition))
        {
            await entity.Save();
            var countBefore = await TodoEntity.Count;
            countBefore.Should().Be(1);

            // Delete in transaction
            using (EntityContext.Transaction("delete-test"))
            {
                var deleted = await entity.Remove();
                deleted.Should().BeTrue();

                // Entity should still exist during transaction
                var countDuringTransaction = await TodoEntity.Count;
                countDuringTransaction.Should().Be(1, "entity should still exist during transaction");

                await EntityContext.Commit();
            }

            // After commit - entity should be deleted
            var countAfter = await TodoEntity.Count;
            countAfter.Should().Be(0, "entity should be deleted after commit");

            var retrieved = await TodoEntity.Get(entity.Id);
            retrieved.Should().BeNull();
        }

        entity.Should().NotBeNull();
    }

    [Fact]
    public async Task Nested_transactions_throw_exception()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        InvalidOperationException? exception = null;

        try
        {
            using (EntityContext.Transaction("outer-transaction"))
            {
                // Attempt to nest transaction - should throw
                using (EntityContext.Transaction("inner-transaction"))
                {
                    // Should never reach here
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        exception.Should().NotBeNull("nested transactions should throw InvalidOperationException");
        exception!.Message.Should().Contain("nested", "error message should mention nested transactions");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Transaction_context_is_accessible()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();

        // Outside transaction
        EntityContext.InTransaction.Should().BeFalse();
        EntityContext.Current?.Transaction.Should().BeNull();

        bool inTransactionFlag = false;
        string? transactionName = null;

        // Inside transaction
        using (EntityContext.Transaction("context-test"))
        {
            inTransactionFlag = EntityContext.InTransaction;
            transactionName = EntityContext.Current?.Transaction;

            await Task.CompletedTask;
        }

        // After transaction
        EntityContext.InTransaction.Should().BeFalse();

        inTransactionFlag.Should().BeTrue("should be in transaction inside using block");
        transactionName.Should().Be("context-test");
    }
}
