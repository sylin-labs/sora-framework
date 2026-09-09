using System.Data.Common;
using System.Collections.Frozen;
using Koan.Core;
using Koan.Core.Capabilities;
using Koan.Core.Semantics.Segmentation;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core.Configuration;
using Koan.Data.Core.Direct;
using Koan.Data.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Data.Core.Specs.Sources;

public sealed class SourcePolicySpec
{
    [Fact]
    public void Ordinary_source_defaults_to_managed_read_write()
    {
        var registry = Discover(new Dictionary<string, string?>
        {
            ["Koan:Data:Sources:Default:Adapter"] = "sqlite",
            ["Koan:Data:Sources:Default:ConnectionString"] = "Data Source=secret.db"
        });

        var source = registry.GetSource("Default")!;
        source.StorageLifecycle.Should().Be(StorageLifecycle.Managed);
        source.Access.Should().Be(DataSourceAccess.ReadWrite);

        var plan = registry.GetPlan("Default", "sqlite");
        plan.Demand(DataOperationEffect.Read, "read");
        plan.Demand(DataOperationEffect.Write, "write");
        plan.Demand(DataOperationEffect.SchemaOrAdmin, "schema");
        plan.Demand(DataOperationEffect.Unknown, "legacy opaque operation");
        plan.ToString().Should().NotContain("secret.db");
    }

    [Theory]
    [InlineData(StorageLifecycle.Managed, DataSourceAccess.ReadWrite, DataOperationEffect.Read, true)]
    [InlineData(StorageLifecycle.Managed, DataSourceAccess.ReadWrite, DataOperationEffect.Write, true)]
    [InlineData(StorageLifecycle.Managed, DataSourceAccess.ReadWrite, DataOperationEffect.SchemaOrAdmin, true)]
    [InlineData(StorageLifecycle.Managed, DataSourceAccess.ReadOnly, DataOperationEffect.Read, true)]
    [InlineData(StorageLifecycle.Managed, DataSourceAccess.ReadOnly, DataOperationEffect.Write, false)]
    [InlineData(StorageLifecycle.Managed, DataSourceAccess.ReadOnly, DataOperationEffect.SchemaOrAdmin, false)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadWrite, DataOperationEffect.Read, true)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadWrite, DataOperationEffect.Write, true)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadWrite, DataOperationEffect.SchemaOrAdmin, false)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadOnly, DataOperationEffect.Read, true)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadOnly, DataOperationEffect.Write, false)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadOnly, DataOperationEffect.SchemaOrAdmin, false)]
    public void Four_policy_cells_admit_only_their_proven_effects(
        StorageLifecycle lifecycle,
        DataSourceAccess access,
        DataOperationEffect effect,
        bool admitted)
    {
        var plan = Plan(lifecycle, access);
        var act = () => plan.Demand(effect, "test operation");

        if (admitted) act.Should().NotThrow();
        else act.Should().Throw<DataSourcePolicyException>();
    }

    [Theory]
    [InlineData(StorageLifecycle.Managed, DataSourceAccess.ReadOnly)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadWrite)]
    [InlineData(StorageLifecycle.External, DataSourceAccess.ReadOnly)]
    public void Any_constrained_source_rejects_unknown_effect(
        StorageLifecycle lifecycle,
        DataSourceAccess access)
    {
        var act = () => Plan(lifecycle, access).Demand(DataOperationEffect.Unknown, "opaque command");

        var failure = act.Should().Throw<DataSourcePolicyException>().Which;
        failure.Code.Should().Be(DataSourcePolicyException.UnknownEffectCode);
        failure.Message.Should().Contain("Declare and validate");
    }

    [Fact]
    public void Configuration_compiles_typed_policy_and_removes_framework_keys_from_adapter_settings()
    {
        var registry = Discover(new Dictionary<string, string?>
        {
            ["Koan:Data:Sources:Legacy:Adapter"] = "sqlserver",
            ["Koan:Data:Sources:Legacy:ConnectionString"] = "Server=secret",
            ["Koan:Data:Sources:Legacy:StorageLifecycle"] = "external",
            ["Koan:Data:Sources:Legacy:Access"] = "readonly",
            ["Koan:Data:Sources:Legacy:CommandTimeout"] = "12",
            ["Koan:Data:Sources:Legacy:ReadLanes:Reports:ConnectionString"] = "Server=read-secret",
            ["Koan:Data:Sources:Legacy:ReadLanes:Reports:Mode"] = "enforced"
        });

        var source = registry.GetSource("legacy")!;
        source.StorageLifecycle.Should().Be(StorageLifecycle.External);
        source.Access.Should().Be(DataSourceAccess.ReadOnly);
        source.Settings.Should().ContainKey("CommandTimeout").WhoseValue.Should().Be("12");
        source.Settings.Should().NotContainKey("StorageLifecycle");
        source.Settings.Should().NotContainKey("Access");
        source.Settings.Should().NotContainKey("ReadLanes");

        var lane = registry.GetPlan("Legacy", "sqlserver").ReadLanes["reports"];
        lane.Settings["Mode"].Should().Be("enforced");
        lane.ConnectionIdentity.Should().NotContain("read-secret");
        lane.ToString().Should().NotContain("read-secret");
    }

    [Fact]
    public void Invalid_policy_configuration_fails_with_valid_choices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Koan:Data:Sources:Legacy:Adapter"] = "sqlserver",
                ["Koan:Data:Sources:Legacy:Access"] = "Sometimes"
            })
            .Build();

        var act = () => new DataSourceRegistry().DiscoverFromConfiguration(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ReadWrite*ReadOnly*");
    }

    [Fact]
    public void Registry_and_plan_copy_settings_and_never_expose_connection_material()
    {
        var settings = new Dictionary<string, string> { ["Mode"] = "original" };
        var registry = new DataSourceRegistry();
        registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
            "Legacy", "sqlserver", "Password=top-secret", settings,
            StorageLifecycle.External, DataSourceAccess.ReadOnly));

        var plan = registry.GetPlan("Legacy", "sqlserver");
        settings["Mode"] = "changed";

        registry.GetSource("Legacy")!.Settings["Mode"].Should().Be("original");
        plan.Settings["Mode"].Should().Be("original");
        plan.ConnectionIdentity.Should().NotContain("top-secret");
        plan.ToString().Should().NotContain("top-secret");
    }

    [Fact]
    public void Warm_declared_route_reuses_one_plan_but_literal_routes_do_not_grow_the_host_cache()
    {
        var registry = new DataSourceRegistry();
        registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
            "Default", "spy", "configured", new Dictionary<string, string>()));

        registry.GetPlan("Default", "spy").Should().BeSameAs(registry.GetPlan("Default", "spy"));
        registry.GetPlan("Default", "spy", "literal-a")
            .Should().NotBeSameAs(registry.GetPlan("Default", "spy", "literal-a"));
    }

    [Fact]
    public void Source_catalog_rejects_duplicates_capacity_and_every_change_after_freeze()
    {
        var registry = new DataSourceRegistry(sourceEntries: 1, sourcePlanEntries: 1);
        registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
            "Default", "spy", "configured", new Dictionary<string, string>()));

        FluentActions.Invoking(() => registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
                "default", "other", "replacement", new Dictionary<string, string>())))
            .Should().Throw<InvalidOperationException>().WithMessage("*already declared*");
        FluentActions.Invoking(() => registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
                "Other", "spy", "other", new Dictionary<string, string>())))
            .Should().Throw<InvalidOperationException>().WithMessage("*configured limit of 1*");

        registry.Freeze();
        FluentActions.Invoking(() => registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
                "Late", "spy", "late", new Dictionary<string, string>())))
            .Should().Throw<InvalidOperationException>().WithMessage("*after host composition*");

        registry.GetPlan("Default", "spy");
        FluentActions.Invoking(() => registry.GetPlan("Default", "other"))
            .Should().Throw<InvalidOperationException>().WithMessage("*source-plan cache*configured limit of 1*");
    }

    [Fact]
    public void Diagnostics_project_the_execution_plan_without_adapter_settings_or_credentials()
    {
        var diagnostics = new DataDiagnostics([]);
        var plan = new DataSourcePlan(
            "Legacy",
            "spy",
            StorageLifecycle.External,
            DataSourceAccess.ReadOnly,
            "route-id",
            "connection-id",
            new Dictionary<string, string> { ["Password"] = "top-secret" });

        diagnostics.ObserveSourcePlan(plan);
        var info = diagnostics.GetSourcePlansSnapshot().Should().ContainSingle().Which;

        info.StorageLifecycle.Should().Be(StorageLifecycle.External);
        info.Access.Should().Be(DataSourceAccess.ReadOnly);
        info.ToString().Should().NotContain("top-secret");
    }

    [Fact]
    public async Task Read_only_write_rejects_before_guard_readiness_lifecycle_or_provider()
    {
        var inner = new SpyRepository();
        var guard = new SpyGuard();
        var facade = new RepositoryFacade<PolicyEntity, string>(
            inner,
            [guard],
            sourcePlan: Plan(StorageLifecycle.Managed, DataSourceAccess.ReadOnly));

        var act = () => facade.Upsert(new PolicyEntity { Id = "1" });

        await act.Should().ThrowAsync<DataSourcePolicyException>();
        await FluentActions.Awaiting(() => facade.Insert(new PolicyEntity { Id = "1" }))
            .Should().ThrowAsync<DataSourcePolicyException>();
        guard.Calls.Should().Be(0);
        inner.ReadinessCalls.Should().Be(0);
        inner.UpsertCalls.Should().Be(0);
    }

    [Fact]
    public async Task External_read_skips_legacy_provisioning_readiness_and_dispatches_once()
    {
        var inner = new SpyRepository();
        var facade = new RepositoryFacade<PolicyEntity, string>(
            inner,
            sourcePlan: Plan(StorageLifecycle.External, DataSourceAccess.ReadOnly));

        await facade.Get("1");

        inner.ReadinessCalls.Should().Be(0);
        inner.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task Ordinary_read_retains_legacy_readiness_until_adapters_earn_the_new_stage_contract()
    {
        var inner = new SpyRepository();
        var facade = new RepositoryFacade<PolicyEntity, string>(inner, sourcePlan: DataSourcePlan.Default);

        await facade.Get("1");

        inner.ReadinessCalls.Should().Be(1);
        inner.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task External_fast_remove_is_shape_mutation_and_rejects_before_provider_work()
    {
        var inner = new SpyRepository();
        var facade = new RepositoryFacade<PolicyEntity, string>(
            inner,
            sourcePlan: Plan(StorageLifecycle.External, DataSourceAccess.ReadWrite));

        var act = () => facade.RemoveAll(RemoveStrategy.Fast);

        await act.Should().ThrowAsync<DataSourcePolicyException>();
        inner.ReadinessCalls.Should().Be(0);
        inner.RemoveAllCalls.Should().Be(0);
    }

    [Fact]
    public async Task External_optimized_remove_downgrades_to_safe_semantic_delete()
    {
        var inner = new SpyRepository();
        var facade = new RepositoryFacade<PolicyEntity, string>(
            inner,
            sourcePlan: Plan(StorageLifecycle.External, DataSourceAccess.ReadWrite));

        await facade.RemoveAll(RemoveStrategy.Optimized);

        inner.LastRemoveStrategy.Should().Be(RemoveStrategy.Safe);
    }

    [Fact]
    public async Task External_delete_all_avoids_clear_instruction_and_uses_semantic_delete()
    {
        var inner = new SpyRepository { QueryItems = [new PolicyEntity { Id = "1" }] };
        var facade = new RepositoryFacade<PolicyEntity, string>(
            inner,
            sourcePlan: Plan(StorageLifecycle.External, DataSourceAccess.ReadWrite));

        var removed = await facade.DeleteAll();

        removed.Should().Be(1);
        inner.InstructionCalls.Should().Be(0);
        inner.QueryCalls.Should().Be(2, "bounded semantic deletion verifies completion with an empty first page");
        inner.DeleteManyCalls.Should().Be(1);
    }

    [Fact]
    public async Task Managed_delete_all_selects_repository_contract_once_without_instruction_probe()
    {
        var inner = new SpyRepository { DeleteAllResult = 7 };
        var facade = new RepositoryFacade<PolicyEntity, string>(inner, sourcePlan: DataSourcePlan.Default);

        var removed = await facade.DeleteAll();

        removed.Should().Be(7);
        inner.DeleteAllCalls.Should().Be(1);
        inner.InstructionCalls.Should().Be(0);
        inner.QueryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Read_only_batch_rejects_when_saved_before_inner_batch_or_readiness()
    {
        var inner = new SpyRepository();
        var facade = new RepositoryFacade<PolicyEntity, string>(
            inner,
            sourcePlan: Plan(StorageLifecycle.Managed, DataSourceAccess.ReadOnly));
        var batch = facade.CreateBatch().Add(new PolicyEntity { Id = "1" });

        var act = () => batch.Save();

        await act.Should().ThrowAsync<DataSourcePolicyException>();
        inner.ReadinessCalls.Should().Be(0);
        inner.CreateBatchCalls.Should().Be(0);
    }

    [Fact]
    public async Task Constrained_instruction_requires_an_exact_effect_before_dispatch()
    {
        var inner = new SpyRepository();
        var facade = new RepositoryFacade<PolicyEntity, string>(
            inner,
            sourcePlan: Plan(StorageLifecycle.External, DataSourceAccess.ReadOnly));

        await facade.ExecuteAsync<int>(new Instruction(
            RelationalInstructions.SqlQuery,
            Effect: DataOperationEffect.Read));
        var opaque = () => facade.ExecuteAsync<int>(new Instruction("provider.opaque"));

        await opaque.Should().ThrowAsync<DataSourcePolicyException>();
        inner.InstructionCalls.Should().Be(1);
    }

    [Fact]
    public async Task Normal_data_service_rejects_unknown_instruction_before_provider_construction()
    {
        var factory = new CountingAdapterFactory();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Koan:Data:Sources:Legacy:Adapter"] = factory.Provider,
                ["Koan:Data:Sources:Legacy:ConnectionString"] = "source-secret",
                ["Koan:Data:Sources:Legacy:StorageLifecycle"] = "External",
                ["Koan:Data:Sources:Legacy:Access"] = "ReadOnly"
            }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddKoan();
        services.AddSingleton<IDataAdapterFactory>(factory);
        await using var provider = services.BuildServiceProvider();
        var data = provider.GetRequiredService<IDataService>();
        using var route = EntityContext.With(source: "Legacy");

        await FluentActions.Awaiting(() => data.Execute<PolicyEntity, int>(
                InstructionSql.Scalar("delete from widgets returning id")))
            .Should().ThrowAsync<DataSourcePolicyException>();

        factory.CreateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Direct_connection_override_cannot_bypass_named_source_policy()
    {
        var registry = new DataSourceRegistry();
        registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
            "Legacy", "spy", "configured", new Dictionary<string, string>(),
            StorageLifecycle.External, DataSourceAccess.ReadOnly));
        var factory = new SpyConnectionFactory();
        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(SegmentationPlan.Empty)
            .AddSingleton<IDataProviderConnectionFactory>(factory)
            .BuildServiceProvider();
        var session = new DirectSession(
            services,
            new ConfigurationBuilder().Build(),
            "Legacy",
            null).WithConnectionString("overridden");

        var act = () => session.Execute("anything");

        await act.Should().ThrowAsync<DataSourcePolicyException>();
        factory.ResolveCalls.Should().Be(0);
        factory.CreateCalls.Should().Be(0);
    }

    [Fact]
    public void Direct_transaction_creation_cannot_elevate_a_constrained_source()
    {
        var registry = new DataSourceRegistry();
        registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
            "Legacy", "spy", "configured", new Dictionary<string, string>(),
            StorageLifecycle.External, DataSourceAccess.ReadOnly));
        var factory = new SpyConnectionFactory();
        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(SegmentationPlan.Empty)
            .AddSingleton<IDataProviderConnectionFactory>(factory)
            .BuildServiceProvider();
        var session = new DirectSession(
            services,
            new ConfigurationBuilder().Build(),
            "Legacy",
            null);

        var act = () => session.Begin();

        act.Should().Throw<DataSourcePolicyException>();
        factory.ResolveCalls.Should().Be(0);
        factory.CreateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Direct_result_shape_never_grants_read_authority_to_opaque_text()
    {
        var registry = new DataSourceRegistry();
        registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
            "Legacy", "spy", "configured", new Dictionary<string, string>(),
            StorageLifecycle.External, DataSourceAccess.ReadOnly));
        var factory = new SpyConnectionFactory();
        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(SegmentationPlan.Empty)
            .AddSingleton<IDataProviderConnectionFactory>(factory)
            .BuildServiceProvider();

        var scalar = new DirectSession(services, new ConfigurationBuilder().Build(), "Legacy", null);
        var query = new DirectSession(services, new ConfigurationBuilder().Build(), "Legacy", null);
        var typed = new DirectSession(services, new ConfigurationBuilder().Build(), "Legacy", null);

        await FluentActions.Awaiting(() => scalar.Scalar<int>("update widgets set value = 1 returning value"))
            .Should().ThrowAsync<DataSourcePolicyException>();
        await FluentActions.Awaiting(() => query.Query("select value from widgets; delete from widgets"))
            .Should().ThrowAsync<DataSourcePolicyException>();
        await FluentActions.Awaiting(() => typed.Query<PolicyEntity>("with changed as (delete from widgets returning *) select * from changed"))
            .Should().ThrowAsync<DataSourcePolicyException>();
        factory.ResolveCalls.Should().Be(0);
        factory.CreateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Direct_explicit_effect_is_single_and_result_independent()
    {
        var registry = new DataSourceRegistry();
        registry.RegisterSource(new DataSourceRegistry.SourceDefinition(
            "Legacy", "spy", "configured", new Dictionary<string, string>(),
            StorageLifecycle.External, DataSourceAccess.ReadOnly));
        var factory = new SpyConnectionFactory();
        var services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(SegmentationPlan.Empty)
            .AddSingleton<IDataProviderConnectionFactory>(factory)
            .BuildServiceProvider();
        var session = new DirectSession(services, new ConfigurationBuilder().Build(), "Legacy", null)
            .Effect(DataOperationEffect.Write);

        FluentActions.Invoking(() => session.Effect(DataOperationEffect.Read))
            .Should().Throw<InvalidOperationException>().WithMessage("*once*");
        await FluentActions.Awaiting(() => session.Scalar<int>("select 1"))
            .Should().ThrowAsync<DataSourcePolicyException>();
        factory.ResolveCalls.Should().Be(0);
        factory.CreateCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(DataInstructions.EnsureCreated, DataOperationEffect.SchemaOrAdmin)]
    [InlineData(DataInstructions.Clear, DataOperationEffect.Write)]
    [InlineData(DataInstructions.Patch, DataOperationEffect.Write)]
    [InlineData(RelationalInstructions.SchemaValidate, DataOperationEffect.Read)]
    [InlineData(RelationalInstructions.SqlScalar, DataOperationEffect.Unknown)]
    [InlineData(RelationalInstructions.SqlQuery, DataOperationEffect.Unknown)]
    [InlineData(RelationalInstructions.SqlNonQuery, DataOperationEffect.Unknown)]
    public void Instruction_effects_are_exact_not_prefix_or_result_heuristics(
        string name,
        DataOperationEffect expected)
    {
        new Instruction(name).EffectiveEffect().Should().Be(expected);
        new Instruction("prefix." + name).EffectiveEffect().Should().Be(DataOperationEffect.Unknown);
    }

    [Fact]
    public void Raw_sql_instruction_effect_is_explicit_and_never_derived_from_text_or_result()
    {
        InstructionSql.Query("select 1").EffectiveEffect().Should().Be(DataOperationEffect.Unknown);
        InstructionSql.Scalar("delete from widgets returning id").EffectiveEffect()
            .Should().Be(DataOperationEffect.Unknown);
        InstructionSql.Query(
                "select value from widgets; delete from widgets",
                DataOperationEffect.Read)
            .EffectiveEffect().Should().Be(DataOperationEffect.Read);
        InstructionSql.NonQuery("select 1", DataOperationEffect.SchemaOrAdmin)
            .EffectiveEffect().Should().Be(DataOperationEffect.SchemaOrAdmin);
    }

    [Theory]
    [InlineData(DataCommitOutcome.Committed)]
    [InlineData(DataCommitOutcome.Unknown)]
    public void Committed_or_unknown_outcome_can_never_claim_automatic_replay(DataCommitOutcome outcome)
    {
        var act = () => new DataFailure(
            "provider.transport",
            DataFailureKind.Unavailable,
            outcome,
            DataRetryDisposition.Never,
            DataReplayDisposition.BeforeDispatchOnly);

        act.Should().Throw<ArgumentException>().WithMessage("*never be replayed*");
    }

    [Fact]
    public void Failure_wording_is_framework_owned_and_native_evidence_is_opaque()
    {
        var failure = new DataFailure(
            "provider.unavailable",
            DataFailureKind.Unavailable,
            DataCommitOutcome.NotDispatched,
            DataRetryDisposition.BeforeDispatchOnly,
            DataReplayDisposition.BeforeDispatchOnly,
            "EVD-opaque");

        failure.Message.Should().Be(DataFailureCorrections.Message(DataFailureKind.Unavailable));
        failure.Correction.Should().Be(DataFailureCorrections.Correction(DataFailureKind.Unavailable));
        failure.EvidenceReference.Should().Be("EVD-opaque");
        failure.ToString().Should().NotContain("provider.unavailable");
    }

    private static DataSourceRegistry Discover(Dictionary<string, string?> values)
    {
        var registry = new DataSourceRegistry();
        registry.DiscoverFromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return registry;
    }

    private static DataSourcePlan Plan(StorageLifecycle lifecycle, DataSourceAccess access) =>
        new("Legacy", "spy", lifecycle, access, "route", "connection");

    private sealed class PolicyEntity : Koan.Data.Core.Model.Entity<PolicyEntity, string>
    {
        [Identifier]
        public override string Id { get; set; } = string.Empty;
    }

    private sealed class SpyGuard : IStorageGuard
    {
        public int Calls { get; private set; }
        public void Guard(Type entityType) => Calls++;
    }

    private sealed class SpyRepository :
        IDataRepository<PolicyEntity, string>,
        IQueryRepository<PolicyEntity, string>,
        IInstructionExecutor<PolicyEntity>,
        IDescribesCapabilities
    {
        public int ReadinessCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int UpsertCalls { get; private set; }
        public int RemoveAllCalls { get; private set; }
        public int DeleteManyCalls { get; private set; }
        public int DeleteAllCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public int InstructionCalls { get; private set; }
        public int CreateBatchCalls { get; private set; }
        public RemoveStrategy? LastRemoveStrategy { get; private set; }
        private List<PolicyEntity> _queryItems = [];
        public IReadOnlyList<PolicyEntity> QueryItems { get => _queryItems; init => _queryItems = value.ToList(); }
        public int DeleteAllResult { get; init; }

        public void Describe(ICapabilities capabilities)
            => capabilities.Add(DataCaps.Query.ProviderBoundedPaging);

        public Task EnsureReady(CancellationToken ct = default) { ReadinessCalls++; return Task.CompletedTask; }
        public Task<PolicyEntity?> Get(string id, CancellationToken ct = default) { GetCalls++; return Task.FromResult<PolicyEntity?>(null); }
        public Task<IReadOnlyList<PolicyEntity?>> GetMany(IEnumerable<string> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PolicyEntity?>>([]);
        public Task<PolicyEntity> Upsert(PolicyEntity model, CancellationToken ct = default) { UpsertCalls++; return Task.FromResult(model); }
        public Task<bool> Delete(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> UpsertMany(IEnumerable<PolicyEntity> models, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> DeleteMany(IEnumerable<string> ids, CancellationToken ct = default)
        {
            DeleteManyCalls++;
            var keys = ids.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(_queryItems.RemoveAll(entity => keys.Contains(entity.Id)));
        }
        public Task<int> DeleteAll(CancellationToken ct = default) { DeleteAllCalls++; return Task.FromResult(DeleteAllResult); }
        public Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default) { RemoveAllCalls++; LastRemoveStrategy = strategy; return Task.FromResult(0L); }
        public IBatchSet<PolicyEntity, string> CreateBatch() { CreateBatchCalls++; throw new NotSupportedException(); }
        public Task<RepositoryQueryResult<PolicyEntity>> Query(QueryDefinition query, CancellationToken ct = default)
        {
            QueryCalls++;
            IEnumerable<PolicyEntity> items = _queryItems;
            if (query.HasPagination)
                items = items.Skip(query.EffectiveOffset()).Take(query.EffectivePageSize());
            return Task.FromResult(new RepositoryQueryResult<PolicyEntity>
            {
                Items = items.ToArray(),
                PaginationHandled = query.HasPagination,
                SortHandled = query.Sort.ToFrozenSet()
            });
        }
        public Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default) =>
            Task.FromResult(CountResult.Exact(QueryItems.Count));
        public Task<TResult> ExecuteAsync<TResult>(Instruction instruction, CancellationToken ct = default)
        {
            InstructionCalls++;
            return Task.FromResult(default(TResult)!);
        }
    }

    private sealed class SpyConnectionFactory : IDataProviderConnectionFactory
    {
        public int ResolveCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public bool CanHandle(string provider) => true;
        public string? ResolveConnectionString(string source) { ResolveCalls++; return "resolved"; }
        public DbConnection Create(string connectionString) { CreateCalls++; throw new InvalidOperationException("must not create"); }
    }

    private sealed class CountingAdapterFactory : IDataAdapterFactory
    {
        public string Provider => "effect-gate-spy";
        public int CreateCalls { get; private set; }
        public StorageNamingCapability GetNamingCapability(IServiceProvider services) => new();
        public void DescribeClaims(IDataClaims claims) { }

        public IDataRepository<TEntity, TKey> Create<TEntity, TKey>(IServiceProvider services, string source = "Default")
            where TEntity : class, IEntity<TKey>
            where TKey : notnull
        {
            CreateCalls++;
            throw new InvalidOperationException("provider construction must not occur");
        }
    }
}
