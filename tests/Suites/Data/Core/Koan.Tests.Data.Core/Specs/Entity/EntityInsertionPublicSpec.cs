using Koan.Data.Abstractions.Failures;
using Koan.Data.Core.Model;
using Koan.Tests.Data.Core.Support;

namespace Koan.Tests.Data.Core.Specs.Entity;

public sealed class EntityInsertionPublicSpec
{
    [Fact]
    public async Task Empty_partition_save_targets_default_and_restores_ambient()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var adapter = EntityContext.Adapter("inmemory");
        var id = Guid.NewGuid().ToString("N");
        using (EntityContext.Partition(""))
            await new TodoEntity { Id = id, Title = "original default" }.Save();
        using var ambient = EntityContext.Partition("locale");
        await new TodoEntity { Id = id, Title = "locale content" }.Save();

        await new TodoEntity { Id = id, Title = "updated default" }.Save(partition: "");

        EntityContext.Current!.Partition.Should().Be("locale");
        (await TodoEntity.Get(id, ""))!.Title.Should().Be("updated default");
        (await TodoEntity.Get(id))!.Title.Should().Be("locale content");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("selected")]
    public async Task Insert_receipt_and_collision_preserve_the_selected_partition(string? selected)
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var adapter = EntityContext.Adapter("inmemory");
        using var ambient = EntityContext.Partition("ambient");
        var model = new TodoEntity { Id = Guid.NewGuid().ToString("N"), Title = "original" };

        var inserted = await model.Insert(partition: selected);
        inserted.Key.Should().Be(model.Id);
        inserted.Outcome.Should().Be(MutationOutcome.Inserted);
        inserted.CommitOutcome.Should().Be(DataCommitOutcome.Committed);
        inserted.Entity.Should().BeSameAs(model);
        EntityContext.Current!.Partition.Should().Be("ambient");

        var conflict = await TodoEntity.Insert(new TodoEntity { Id = model.Id, Title = "must not replace" }, selected);
        conflict.Key.Should().Be(model.Id);
        conflict.Outcome.Should().Be(MutationOutcome.Conflict);
        conflict.CommitOutcome.Should().Be(DataCommitOutcome.NotCommitted);
        conflict.Entity.Should().BeNull();
        EntityContext.Current!.Partition.Should().Be("ambient");

        foreach (var partition in new[] { "", "selected", "ambient" })
        {
            using var scope = EntityContext.Partition(partition);
            var stored = await TodoEntity.Get(model.Id);
            if (partition == (selected ?? "ambient")) stored!.Title.Should().Be("original");
            else stored.Should().BeNull();
        }
        EntityContext.Current!.Partition.Should().Be("ambient");
    }

    [Fact]
    public async Task One_identity_can_be_inserted_in_default_and_locale_without_replacing_either()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var route = EntityContext.With(adapter: "inmemory", partition: "locale");
        var id = Guid.NewGuid().ToString("N");
        (await new TodoEntity { Id = id, Title = "locale" }.Insert()).Outcome.Should().Be(MutationOutcome.Inserted);
        (await new TodoEntity { Id = id, Title = "default" }.Insert(partition: "")).Outcome.Should().Be(MutationOutcome.Inserted);
        (await TodoEntity.Get(id))!.Title.Should().Be("locale");
        (await TodoEntity.Get(id, ""))!.Title.Should().Be("default");
        EntityContext.Current!.Partition.Should().Be("locale");
    }

    [Fact]
    public async Task Root_static_inherited_static_and_variant_receiver_keep_existing_family_typing()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var rootCandidate = new GeneratedFamilyAnime { Kind = "root call", Episodes = 12 };
        var inheritedCandidate = new GeneratedFamilyAnime { Kind = "inherited call", Episodes = 24 };
        var receiverCandidate = new GeneratedFamilyAnime { Kind = "receiver call", Episodes = 36 };

        // These assignments are compile-time evidence, not a new generator typing promise.
        Task<MutationResult<GeneratedFamilyMedia, string>> rootCall = GeneratedFamilyMedia.Insert(rootCandidate);
        var root = await rootCall;
        Task<MutationResult<GeneratedFamilyMedia, string>> inheritedCall = GeneratedFamilyAnime.Insert(inheritedCandidate);
        var inherited = await inheritedCall;
        Task<MutationResult<GeneratedFamilyAnime, string>> receiverCall = receiverCandidate.Insert();
        var receiver = await receiverCall;

        root.Entity.Should().BeSameAs(rootCandidate);
        inherited.Entity.Should().BeSameAs(inheritedCandidate);
        receiver.Entity.Should().BeSameAs(receiverCandidate);
        foreach (var model in new[] { rootCandidate, inheritedCandidate, receiverCandidate })
        {
            (await GeneratedFamilyMedia.Get(model.Id)).Should().BeOfType<GeneratedFamilyAnime>()
                .Which.Episodes.Should().Be(model.Episodes);
            (await GeneratedFamilyAnime.Get(model.Id))!.Episodes.Should().Be(model.Episodes);
        }

        var collision = await GeneratedFamilyMedia.Insert(new GeneratedFamilyMedia { Id = root.Key, Kind = "root overwrite" });
        collision.Outcome.Should().Be(MutationOutcome.Conflict);
        collision.Entity.Should().BeNull();
        (await GeneratedFamilyAnime.Get(root.Key))!.Episodes.Should().Be(12);
    }

    [Fact]
    public async Task Non_string_root_receiver_infers_key_and_returns_existing_receipt()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var model = new NumericInsertionProbe { Id = 42, Value = "assigned key" };
        Task<MutationResult<NumericInsertionProbe, long>> pending = model.Insert();
        var result = await pending;
        result.Key.Should().Be(42);
        result.Outcome.Should().Be(MutationOutcome.Inserted);
        result.CommitOutcome.Should().Be(DataCommitOutcome.Committed);
        result.Entity.Should().BeSameAs(model);
        (await NumericInsertionProbe.Get(42))!.Value.Should().Be("assigned key");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("completion failure")]
    public async Task Insert_holds_default_through_async_lifecycle_and_restores_caller(string outcome)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = 0;
        var after = 0;
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync(configureServices: _ =>
        {
            InsertionLifecycleProbe.Lifecycle.BeforeUpsert(async context =>
            {
                before++;
                context.Prior.Should().BeNull();
                EntityContext.Current!.Partition.Should().BeNullOrEmpty();
                entered.SetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                EntityContext.Current!.Partition.Should().BeNullOrEmpty();
                return context.Proceed();
            });
            InsertionLifecycleProbe.Lifecycle.AfterUpsert(async _ =>
            {
                await Task.Yield();
                after++;
                EntityContext.Current!.Partition.Should().BeNullOrEmpty();
                if (outcome == "completion failure") throw new InvalidOperationException("test completion failure");
            });
        });
        using var route = EntityContext.With(adapter: "inmemory", partition: "locale");
        using var cancellation = new CancellationTokenSource();
        var model = new InsertionLifecycleProbe { Id = Guid.NewGuid().ToString("N"), Value = "inserted" };
        var pending = model.Insert(partition: "", ct: cancellation.Token);
        if (await Task.WhenAny(entered.Task, pending) == pending) await pending;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        EntityContext.Current!.Partition.Should().Be("locale");
        if (outcome == "cancel") cancellation.Cancel();
        release.SetResult();

        if (outcome == "cancel")
            await ((Func<Task>)(() => pending)).Should().ThrowAsync<OperationCanceledException>();
        else if (outcome == "completion failure")
            await ((Func<Task>)(() => pending)).Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("test completion failure");
        else (await pending).Entity.Should().BeSameAs(model);

        before.Should().Be(1);
        after.Should().Be(outcome == "cancel" ? 0 : 1);
        EntityContext.Current!.Partition.Should().Be("locale");
        (await InsertionLifecycleProbe.Get(model.Id)).Should().BeNull();
        var stored = await InsertionLifecycleProbe.Get(model.Id, "");
        if (outcome == "cancel") stored.Should().BeNull();
        else stored!.Value.Should().Be("inserted", "completion failure does not roll back a committed insertion");
    }

    [Fact]
    public async Task Precancelled_and_null_requests_do_not_resolve_or_change_scope()
    {
        using var route = EntityContext.With(adapter: "unsupported-before-resolution", partition: "ambient");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ((Func<Task>)(() => new TodoEntity().Insert(partition: "", ct: cancellation.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => TodoEntity.Insert(null!, partition: "")))
            .Should().ThrowAsync<ArgumentNullException>();
        EntityContext.Current!.Partition.Should().Be("ambient");
        EntityContext.Current.Adapter.Should().Be("unsupported-before-resolution");
    }

    [Fact]
    public async Task Unsupported_connector_and_deferred_receipt_refuse_without_insertion()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var route = EntityContext.With(adapter: "inmemory", partition: "ambient");
        var model = new TodoEntity { Id = Guid.NewGuid().ToString("N"), Title = "not inserted" };
        using (EntityContext.Adapter("json"))
        {
            await ((Func<Task>)(() => model.Insert(partition: ""))).Should().ThrowAsync<NotSupportedException>();
            (await TodoEntity.Get(model.Id, "")).Should().BeNull();
        }
        using (EntityContext.Transaction("insert-public-not-deferred"))
        {
            await ((Func<Task>)(() => model.Insert(partition: ""))).Should().ThrowAsync<NotSupportedException>();
            await EntityContext.Commit();
        }
        (await TodoEntity.Get(model.Id, "")).Should().BeNull();
        EntityContext.Current!.Partition.Should().Be("ambient");
    }

    private sealed class NumericInsertionProbe : Entity<NumericInsertionProbe, long>
    {
        public string Value { get; set; } = "";
    }

    private sealed class InsertionLifecycleProbe : Entity<InsertionLifecycleProbe>
    {
        public string Value { get; set; } = "";
    }
}
