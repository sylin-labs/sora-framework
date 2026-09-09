using Koan.Core;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core;
using Koan.Data.Core.Lifecycle;
using Koan.Data.Core.Model;
using Koan.Data.Core.Routing;
using Koan.Data.Core.Pipeline;
using Koan.Tests.Data.Core.Support;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Data.Core.Specs.Entity;

public sealed class ConditionalPublicationSemanticsSpec
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_lambda_capture_and_explicit_partition_survive_an_async_lifecycle(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync(configureServices: _ =>
            ConditionalRaceProbe.Lifecycle.BeforeUpsert(async context =>
            {
                if (armed) { entered.SetResult(); await release.Task.WaitAsync(context.CancellationToken); }
                return context.Proceed();
            }));
        using var adapter = EntityContext.With(adapter: "inmemory");
        var id = Guid.NewGuid().ToString("N");
        using (EntityContext.Partition("")) await new ConditionalRaceProbe { Id = id, Revision = "default" }.Save();
        await new ConditionalRaceProbe { Id = id, Revision = "ambient" }.Save("ambient");
        using var ambient = EntityContext.Partition("ambient");
        var expected = new List<string> { "default" };
        using var cancellation = new CancellationTokenSource();
        armed = true;
        var pending = new ConditionalRaceProbe { Id = id, Revision = "published" }
            .ReplaceIf(row => expected.Contains(row.Revision), partition: "", ct: cancellation.Token);
        if (await Task.WhenAny(entered.Task, pending) == pending) await pending;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        expected[0] = "changed-after-call";
        (await ConditionalRaceProbe.Get(id))!.Revision.Should().Be("ambient");
        if (cancel) cancellation.Cancel();
        release.SetResult();
        if (cancel) await FluentActions.Invoking(() => pending).Should().ThrowAsync<OperationCanceledException>();
        else (await pending).Should().BeTrue();
        (await ConditionalRaceProbe.Get(id))!.Revision.Should().Be("ambient");
        (await ConditionalRaceProbe.Get(id, ""))!.Revision.Should().Be(cancel ? "default" : "published");
    }

    [Fact]
    public async Task Active_read_scope_refuses_before_readiness_or_mutation()
    {
        var native = new ConditionalProbeRepository();
        var facade = new RepositoryFacade<ConditionalProbe, string>(native,
            readContributors: [new ConditionalScope()]);
        await FluentActions.Invoking(() => facade.ConditionalReplaceAsync(
            new() { Id = "one", Revision = "new" }, Filter.Eq("Revision", "old")))
            .Should().ThrowAsync<NotSupportedException>();
        native.ReadyCalls.Should().Be(0);
        native.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Lifecycle_cannot_substitute_a_different_runtime_entity_shape()
    {
        var native = new ConditionalProbeRepository();
        var lifecycle = new EntityLifecyclePlan<ConditionalProbe, string>();
        lifecycle.AddBeforeUpsert(context =>
        {
            context.Current = new DerivedConditionalProbe { Id = context.Current.Id, Revision = "substituted" };
            return ValueTask.FromResult(context.Proceed());
        });
        var facade = new RepositoryFacade<ConditionalProbe, string>(native, lifecycle: lifecycle);
        await FluentActions.Invoking(() => facade.ConditionalReplaceAsync(
            new() { Id = "one", Revision = "new" }, Filter.Eq("Revision", "old")))
            .Should().ThrowAsync<NotSupportedException>();
        native.Calls.Should().Be(0);
        native.Stored!.Revision.Should().Be("old");
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("date")]
    public void Document_CAS_evaluator_refuses_unqualified_scalar_meaning(string kind)
    {
        var guard = kind == "binary" ? new Not(Filter.Eq("Bytes", new byte[] { 1, 2 })) : Filter.Eq("When", DateTime.UtcNow);
        FluentActions.Invoking(() => InMemoryFilterEvaluator.CompileConditional<ConditionalAtoms>(guard))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task Guard_collections_are_frozen_before_readiness_awaits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new ConditionalProbeRepository { Ready = async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); } };
        var facade = new RepositoryFacade<ConditionalProbe, string>(native);
        var values = new object?[] { "old" };
        var pending = facade.ConditionalReplaceAsync(new() { Id = "one", Revision = "new" }, Filter.In("Revision", values));
        await entered.Task;
        values[0] = "changed-capture";
        release.SetResult();
        (await pending).Should().BeTrue();
        native.Stored!.Revision.Should().Be("new");
        native.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflict_runs_before_but_never_after_lifecycle(bool conflict)
    {
        var before = 0;
        var after = 0;
        var lifecycle = new EntityLifecyclePlan<ConditionalProbe, string>();
        lifecycle.AddBeforeUpsert(context =>
        {
            context.Prior!.Revision.Should().Be("old");
            before++;
            return ValueTask.FromResult(context.Proceed());
        });
        lifecycle.AddAfterUpsert(_ => { after++; return ValueTask.CompletedTask; });
        var native = new ConditionalProbeRepository();
        var facade = new RepositoryFacade<ConditionalProbe, string>(native, lifecycle: lifecycle);
        (await facade.ConditionalReplaceAsync(new() { Id = "one", Revision = "new" },
            Filter.Eq("Revision", conflict ? "stale" : "old"))).Should().Be(!conflict);
        before.Should().Be(1);
        after.Should().Be(conflict ? 0 : 1);
        native.Stored!.Revision.Should().Be(conflict ? "old" : "new");
        native.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exceptions_after_native_commit_do_not_replay_or_imply_rollback(bool duringNativeReturn)
    {
        var native = new ConditionalProbeRepository { ThrowAfterCommit = duringNativeReturn };
        var after = 0;
        var lifecycle = new EntityLifecyclePlan<ConditionalProbe, string>();
        lifecycle.AddAfterUpsert(_ => { after++; throw new IOException("after-write observer failed"); });
        var facade = new RepositoryFacade<ConditionalProbe, string>(native, lifecycle: lifecycle);
        await ((Func<Task>)(() => facade.ConditionalReplaceAsync(new() { Id = "one", Revision = "new" }, Filter.Eq("Revision", "old"))))
            .Should().ThrowAsync<IOException>();
        native.Stored!.Revision.Should().Be("new");
        native.Calls.Should().Be(1);
        after.Should().Be(duringNativeReturn ? 0 : 1);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("route")]
    [InlineData("cancel")]
    public async Task Preparation_cannot_retarget_or_dispatch_after_cancellation(string change)
    {
        var native = new ConditionalProbeRepository();
        var selected = new DataRouteBinding(DataSourcePlan.Default, DataRouteOrigin.Default, 1, 1);
        var current = selected;
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new EntityLifecyclePlan<ConditionalProbe, string>();
        lifecycle.AddBeforeUpsert(context =>
        {
            if (change == "key") context.Current.Id = "other";
            if (change == "route") current = selected with { ContentGeneration = 2 };
            if (change == "cancel") cancellation.Cancel();
            return ValueTask.FromResult(context.Proceed());
        });
        var facade = new RepositoryFacade<ConditionalProbe, string>(native, lifecycle: lifecycle,
            routeBinding: selected, resolveRoute: () => current);
        var failure = await Record.ExceptionAsync(() => facade.ConditionalReplaceAsync(
            new() { Id = "one", Revision = "new" }, Filter.Eq("Revision", "old"), cancellation.Token));
        failure.Should().NotBeNull();
        if (change == "cancel") failure.Should().BeAssignableTo<OperationCanceledException>();
        else failure.Should().BeOfType<InvalidOperationException>();
        native.Calls.Should().Be(0);
        native.Stored!.Revision.Should().Be("old");
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("counterpart")]
    [InlineData("clr")]
    [InlineData("classified")]
    [InlineData("default-key")]
    [InlineData("blank-key")]
    public async Task Unsupported_intent_refuses_before_readiness_and_lifecycle(string kind)
    {
        var native = new ConditionalProbeRepository { Advertise = kind != "capability" };
        var lifecycle = new EntityLifecyclePlan<ConditionalProbe, string>();
        lifecycle.AddBeforeUpsert(_ => throw new Exception("Must not run lifecycle"));
        var facade = new RepositoryFacade<ConditionalProbe, string>(native, lifecycle: lifecycle);
        Filter guard = kind switch
        {
            "counterpart" => Filter.SameIdIn<ConditionalProbe>(row => true, ""),
            "clr" => new ClrFilter((System.Linq.Expressions.Expression<Func<ConditionalProbe, bool>>)(row => row.Revision.GetHashCode() == 0)),
            "classified" => Filter.Eq("Secret", "private"),
            _ => Filter.Eq("Revision", "old")
        };
        await ((Func<Task>)(() => facade.ConditionalReplaceAsync(
            new() { Id = kind == "default-key" ? null! : kind == "blank-key" ? "  " : "one", Revision = "new" }, guard)))
            .Should().ThrowAsync<NotSupportedException>();
        native.ReadyCalls.Should().Be(0);
        native.Calls.Should().Be(0);
        native.Stored!.Revision.Should().Be("old");
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task InMemory_native_compare_exchange_preserves_a_competing_ordinary_mutation(string competitor)
    {
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan()).StartAsync(TestContext.Current.CancellationToken);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var saved = await new ConditionalRaceProbe { Revision = "old" }.Save();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        ConditionalRaceProbe.OnRead = () => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue(); };
        var pending = Task.Run(() => new ConditionalRaceProbe { Id = saved.Id, Revision = "delayed" }
            .ReplaceIf(row => row.Revision == "old"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (competitor == "save") await new ConditionalRaceProbe { Id = saved.Id, Revision = "manual" }.Save();
            else await ConditionalRaceProbe.Remove(saved.Id);
        }
        finally { release.Set(); ConditionalRaceProbe.OnRead = null; }
        (await pending).Should().BeFalse();
        var stored = await ConditionalRaceProbe.Get(saved.Id);
        if (competitor == "save") stored!.Revision.Should().Be("manual");
        else stored.Should().BeNull();
    }

    public class ConditionalProbe : Entity<ConditionalProbe, string>
    {
        public string Revision { get; set; } = "";
        [Secret] public string Secret { get; set; } = "";
    }

    private sealed class DerivedConditionalProbe : ConditionalProbe;

    private sealed class ConditionalAtoms
    {
        public byte[] Bytes { get; set; } = [];
        public DateTime When { get; set; }
    }

    private sealed class ConditionalScope : IReadFilterContributor
    {
        public Filter? ReadFilter(Type entityType) => Filter.Eq("Revision", "old");
        public Capability? RequiredCapability => null;
    }

    private sealed class ConditionalProbeRepository : IDataRepository<ConditionalProbe, string>,
        IConditionalWriteRepository<ConditionalProbe, string>, IDescribesCapabilities
    {
        public ConditionalProbe? Stored { get; private set; } = new() { Id = "one", Revision = "old" };
        public bool Advertise { get; init; } = true;
        public bool ThrowAfterCommit { get; init; }
        public Func<CancellationToken, Task>? Ready { get; init; }
        public int ReadyCalls { get; private set; }
        public int Calls { get; private set; }
        public void Describe(ICapabilities caps)
        {
            if (Advertise) caps.Add(DataCaps.Write.ConditionalReplace);
            caps.Add(DataCaps.Query.Filter, FilterSupport.Full);
        }
        public async Task EnsureReady(CancellationToken ct = default) { ReadyCalls++; if (Ready is not null) await Ready(ct); }
        public Task<ConditionalProbe?> Get(string id, CancellationToken ct = default) => Task.FromResult(Stored);
        public Task<IReadOnlyList<ConditionalProbe?>> GetMany(IEnumerable<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ConditionalReplaceAsync(ConditionalProbe model, Filter guard, CancellationToken ct = default)
        {
            Calls++;
            if (Stored is null || Stored.Id != model.Id || !InMemoryFilterEvaluator.Compile<ConditionalProbe>(guard)(Stored))
                return Task.FromResult(false);
            Stored = new() { Id = model.Id, Revision = model.Revision };
            if (ThrowAfterCommit) throw new IOException("acknowledgement lost after write");
            return Task.FromResult(true);
        }
        public Task<ConditionalProbe> Upsert(ConditionalProbe model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> Delete(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> UpsertMany(IEnumerable<ConditionalProbe> models, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteMany(IEnumerable<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteAll(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default) => throw new NotSupportedException();
        public IBatchSet<ConditionalProbe, string> CreateBatch() => throw new NotSupportedException();
    }
}

public sealed class ConditionalRaceProbe : Entity<ConditionalRaceProbe>
{
    public static Action? OnRead;
    private string _revision = "";
    public string Revision
    {
        get { Interlocked.Exchange(ref OnRead, null)?.Invoke(); return _revision; }
        set => _revision = value;
    }
}
