using AwesomeAssertions;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Core.Pipeline;
using Koan.Data.Core;
using Koan.Data.Core.Lifecycle;
using Koan.Data.Core.Model;
using Koan.Data.Core.Execution;
using Koan.Data.Core.Querying;
using System.Linq.Expressions;

namespace Koan.Tests.Data.Core.Specs.Entity;

public sealed class EntityExecutionSemanticsSpec
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_transforms_detached_values_but_cannot_retarget_identity(bool changeIdentity)
    {
        var repository = new ReceiptRepository(advertiseInsert: true)
        {
            InsertResult = payload =>
            {
                payload.Value.Should().Be("ENCODED");
                return new(payload.Id, MutationOutcome.Inserted, payload, DataCommitOutcome.Committed);
            }
        };
        var model = new ReceiptEntity { Id = "one", Value = "plain" };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository,
            fieldTransforms: new StorageFieldTransformPlan([new InsertTransform(changeIdentity)]));
        if (changeIdentity)
        {
            await ((Func<Task>)(() => facade.Insert(model))).Should().ThrowAsync<InvalidOperationException>();
            repository.InsertCalls.Should().Be(0);
        }
        else
        {
            var result = await facade.Insert(model);
            result.Entity.Should().BeSameAs(model);
            result.Entity!.Value.Should().Be("plain");
        }
        model.Id.Should().Be("one");
        model.Value.Should().Be("plain");
    }

    private sealed class InsertTransform(bool changeIdentity) : IFieldTransformContributor, IFieldTransform
    {
        public string Id => "insert-test-transform";
        public IFieldTransform? Build(Type entityType) => entityType == typeof(ReceiptEntity) ? this : null;
        public void ApplyOnWrite(object entity)
        {
            var model = (ReceiptEntity)entity;
            model.Value = "ENCODED";
            if (changeIdentity) model.Id = "other";
        }
        public void ApplyOnRead(object entity) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_preserves_lifecycle_without_reading_a_hidden_prior(bool conflict)
    {
        var before = 0;
        var after = 0;
        var lifecycle = new EntityLifecyclePlan<ReceiptEntity, string>();
        lifecycle.AddBeforeUpsert(context =>
        {
            context.Prior.Should().BeNull();
            before++;
            return ValueTask.FromResult(context.Proceed());
        });
        lifecycle.AddAfterUpsert(_ => { after++; return ValueTask.CompletedTask; });
        var repository = new ReceiptRepository(advertiseInsert: true)
        {
            InsertResult = model => new(model.Id,
                conflict ? MutationOutcome.Conflict : MutationOutcome.Inserted,
                conflict ? null : model,
                conflict ? DataCommitOutcome.NotCommitted : DataCommitOutcome.Committed)
        };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository, lifecycle: lifecycle);
        var result = await facade.Insert(new ReceiptEntity { Id = "one" });
        result.Outcome.Should().Be(conflict ? MutationOutcome.Conflict : MutationOutcome.Inserted);
        before.Should().Be(1);
        after.Should().Be(conflict ? 0 : 1);
        repository.GetCalls.Should().Be(0);
        repository.UpsertCalls.Should().Be(0);
        repository.InsertCalls.Should().Be(1);
    }

    [Fact]
    public async Task Unsupported_insert_rejects_before_lifecycle_or_dispatch()
    {
        var lifecycle = new EntityLifecyclePlan<ReceiptEntity, string>();
        lifecycle.AddBeforeUpsert(_ => throw new Exception("Lifecycle must not run"));
        var repository = new ReceiptRepository();
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository, lifecycle: lifecycle);
        await ((Func<Task>)(() => facade.Insert(new ReceiptEntity()))).Should().ThrowAsync<NotSupportedException>();
        repository.InsertCalls.Should().Be(0);
        repository.GetCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("updated")]
    [InlineData("unknown")]
    [InlineData("conflict-entity")]
    public async Task Impossible_insert_receipts_never_complete_or_replay(string defect)
    {
        var after = 0;
        var lifecycle = new EntityLifecyclePlan<ReceiptEntity, string>();
        lifecycle.AddAfterUpsert(_ => { after++; return ValueTask.CompletedTask; });
        var repository = new ReceiptRepository(advertiseInsert: true)
        {
            InsertResult = model => defect switch
            {
                "wrong-key" => new("other", MutationOutcome.Inserted, model, DataCommitOutcome.Committed),
                "updated" => new(model.Id, MutationOutcome.Updated, model, DataCommitOutcome.Committed),
                "unknown" => new(model.Id, MutationOutcome.Inserted, model, DataCommitOutcome.Unknown),
                _ => new(model.Id, MutationOutcome.Conflict, model, DataCommitOutcome.NotCommitted)
            }
        };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository, lifecycle: lifecycle);
        var error = await ((Func<Task>)(() => facade.Insert(new ReceiptEntity { Id = "one" })))
            .Should().ThrowAsync<MutationReceiptRejectedException>();
        error.Which.CommitOutcome.Should().Be(DataCommitOutcome.Unknown);
        after.Should().Be(0);
        repository.InsertCalls.Should().Be(1);
        repository.UpsertCalls.Should().Be(0);
    }

    [Fact]
    public async Task Get_many_normalizes_cardinality_order_duplicates_and_missing_slots()
    {
        var one = new ReceiptEntity { Id = "one", Value = "1" };
        var two = new ReceiptEntity { Id = "two", Value = "2" };
        var repository = new ReceiptRepository
        {
            GetManyResult = [two, one]
        };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var result = await facade.GetMany(["one", "missing", "two", "one"]);

        result.Should().HaveCount(4);
        result[0].Should().BeSameAs(one);
        result[1].Should().BeNull();
        result[2].Should().BeSameAs(two);
        result[3].Should().BeSameAs(one);
    }

    [Fact]
    public async Task Get_many_rejects_an_unrequested_identity()
    {
        var repository = new ReceiptRepository
        {
            GetManyResult = [new ReceiptEntity { Id = "other" }]
        };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var read = () => facade.GetMany(["requested"]);

        await read.Should().ThrowAsync<GetManyReceiptRejectedException>();
    }

    [Fact]
    public async Task Atomic_batch_rejects_before_deferred_load_or_native_save_without_exact_seam()
    {
        var repository = new ReceiptRepository(advertiseAtomic: true);
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);
        var batch = facade.CreateBatch().Update("missing", entity => entity.Value = "changed");

        var save = () => batch.Save(new BatchOptions(RequireAtomic: true));

        await save.Should().ThrowAsync<NotSupportedException>();
        repository.GetCalls.Should().Be(0);
        repository.Batch.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Deferred_mutation_missing_target_fails_before_native_save()
    {
        var repository = new ReceiptRepository();
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var save = () => facade.CreateBatch()
            .Update("missing", entity => entity.Value = "changed")
            .Save();

        var failure = (await save.Should().ThrowAsync<BatchMutationTargetNotFoundException>()).Which;
        failure.OperationIndex.Should().Be(0);
        repository.Batch.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Atomic_batch_requires_and_returns_the_native_atomic_receipt()
    {
        var repository = new ReceiptRepository(advertiseAtomic: true);
        repository.Batch.Capabilities = BatchExecutionCapabilities.Atomic;
        repository.Batch.ResultAtomicity = BatchAtomicity.Atomic;
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var result = await facade.CreateBatch()
            .Add(new ReceiptEntity { Id = "one" })
            .Save(new BatchOptions(RequireAtomic: true));

        result.Atomicity.Should().Be(BatchAtomicity.Atomic);
        repository.Batch.SaveCalls.Should().Be(1);
    }

    [Fact]
    public void Batch_facade_exposes_only_the_execution_capabilities_proved_by_both_provider_and_native_batch()
    {
        var qualified = new ReceiptRepository(advertiseAtomic: true);
        qualified.Batch.Capabilities = BatchExecutionCapabilities.Atomic |
                                       BatchExecutionCapabilities.CompleteItemOutcomes;

        var qualifiedBatch = new RepositoryFacade<ReceiptEntity, string>(qualified).CreateBatch();

        qualifiedBatch.ExecutionCapabilities.Should().Be(
            BatchExecutionCapabilities.Atomic | BatchExecutionCapabilities.CompleteItemOutcomes);

        var unadvertised = new ReceiptRepository();
        unadvertised.Batch.Capabilities = BatchExecutionCapabilities.Atomic |
                                          BatchExecutionCapabilities.CompleteItemOutcomes;

        var conservativeBatch = new RepositoryFacade<ReceiptEntity, string>(unadvertised).CreateBatch();

        conservativeBatch.ExecutionCapabilities.Should().Be(
            BatchExecutionCapabilities.CompleteItemOutcomes,
            "native atomicity is public only when the provider capability fact also qualifies it");
    }

    [Fact]
    public async Task False_atomic_receipt_rejects_after_one_dispatch_without_replay()
    {
        var repository = new ReceiptRepository(advertiseAtomic: true);
        repository.Batch.Capabilities = BatchExecutionCapabilities.Atomic;
        repository.Batch.ResultAtomicity = BatchAtomicity.NotGuaranteed;
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var save = () => facade.CreateBatch()
            .Add(new ReceiptEntity { Id = "one" })
            .Save(new BatchOptions(RequireAtomic: true));

        var failure = (await save.Should().ThrowAsync<BatchReceiptRejectedException>()).Which;
        failure.CommitOutcome.Should().Be(Koan.Data.Abstractions.Failures.DataCommitOutcome.Unknown);
        repository.Batch.SaveCalls.Should().Be(1);
    }

    [Fact]
    public async Task Batch_applies_the_same_timestamp_plan_as_single_upsert()
    {
        var repository = new ReceiptRepository();
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);
        var entity = new ReceiptEntity { Value = "new" };

        await facade.CreateBatch().Add(entity).Save();

        entity.Id.Should().NotBeNullOrWhiteSpace();
        entity.CreatedAt.Should().NotBe(default);
        entity.UpdatedAt.Should().NotBe(default);
        repository.Batch.Added.Should().ContainSingle().Which.Should().BeSameAs(entity);
    }

    [Fact]
    public async Task Lifecycle_bulk_upsert_prepares_all_then_uses_one_native_bulk_dispatch()
    {
        var before = 0;
        var after = 0;
        var lifecycle = new EntityLifecyclePlan<ReceiptEntity, string>();
        lifecycle.AddBeforeUpsert(context =>
        {
            before++;
            context.Current.Value += "-prepared";
            return ValueTask.FromResult(context.Proceed());
        });
        lifecycle.AddAfterUpsert(_ =>
        {
            after++;
            return ValueTask.CompletedTask;
        });
        var repository = new ReceiptRepository();
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository, lifecycle: lifecycle);
        var entities = new[]
        {
            new ReceiptEntity { Id = "one", Value = "a" },
            new ReceiptEntity { Id = "two", Value = "b" }
        };

        var count = await facade.UpsertMany(entities);

        count.Should().Be(2);
        before.Should().Be(2);
        after.Should().Be(2);
        repository.UpsertManyCalls.Should().Be(1);
        repository.UpsertCalls.Should().Be(0);
        entities.Select(entity => entity.Value).Should().Equal("a-prepared", "b-prepared");
    }

    [Fact]
    public async Task Inexact_bulk_receipt_is_unknown_and_never_replayed()
    {
        var repository = new ReceiptRepository { UpsertManyResult = 1 };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var save = () => facade.UpsertMany(
        [
            new ReceiptEntity { Id = "one" },
            new ReceiptEntity { Id = "two" }
        ]);

        var failure = (await save.Should().ThrowAsync<BulkMutationReceiptRejectedException>()).Which;
        failure.Expected.Should().Be(2);
        failure.Reported.Should().Be(1);
        failure.CommitOutcome.Should().Be(Koan.Data.Abstractions.Failures.DataCommitOutcome.Unknown);
        repository.UpsertManyCalls.Should().Be(1);
    }

    [Fact]
    public async Task Exact_upsert_outcome_is_capability_and_native_seam_coupled()
    {
        var repository = new ReceiptRepository(advertiseOutcomes: true)
        {
            UpsertOutcome = MutationOutcome.Inserted
        };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);
        var entity = new ReceiptEntity { Id = "one", Value = "new" };

        var result = await ((IDataMutationOutcomes<ReceiptEntity, string>)facade)
            .UpsertWithOutcome(entity, default);

        result.Key.Should().Be("one");
        result.Outcome.Should().Be(MutationOutcome.Inserted);
        result.Entity.Should().BeSameAs(entity);
        result.CommitOutcome.Should().Be(Koan.Data.Abstractions.Failures.DataCommitOutcome.Committed);
    }

    [Fact]
    public async Task Delete_outcome_reports_missing_without_native_mutation()
    {
        var repository = new ReceiptRepository();
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var result = await ((IDataMutationOutcomes<ReceiptEntity, string>)facade)
            .DeleteWithOutcome("missing", default);

        result.Outcome.Should().Be(MutationOutcome.Missing);
        result.CommitOutcome.Should().Be(Koan.Data.Abstractions.Failures.DataCommitOutcome.NotCommitted);
    }

    [Fact]
    public async Task Conditional_replace_requires_capability_and_native_seam_before_dispatch()
    {
        var unadvertised = new ReceiptRepository();
        var facade = new RepositoryFacade<ReceiptEntity, string>(unadvertised);

        var unsupported = () => facade.ConditionalReplaceAsync(
            new ReceiptEntity { Id = "one" }, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<ReceiptEntity>(entity => entity.Value == "prior"));

        await unsupported.Should().ThrowAsync<NotSupportedException>();
        unadvertised.ConditionalCalls.Should().Be(0);

        var advertised = new ReceiptRepository(advertiseConditional: true)
        {
            ConditionalResult = false
        };
        facade = new RepositoryFacade<ReceiptEntity, string>(advertised);
        var lostRace = await facade.ConditionalReplaceAsync(
            new ReceiptEntity { Id = "one" }, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<ReceiptEntity>(entity => entity.Value == "prior"));

        lostRace.Should().BeFalse();
        advertised.ConditionalCalls.Should().Be(1);
    }

    [Fact]
    public async Task Load_lifecycle_observes_only_the_final_visible_page()
    {
        var observed = new List<string>();
        var lifecycle = new EntityLifecyclePlan<ReceiptEntity, string>();
        lifecycle.AddAfterLoad(context =>
        {
            observed.Add(context.Current.Id);
            return ValueTask.CompletedTask;
        });
        var repository = new ReceiptRepository
        {
            QueryResult = new RepositoryQueryResult<ReceiptEntity>
            {
                Items =
                [
                    new ReceiptEntity { Id = "discarded", Value = "other" },
                    new ReceiptEntity { Id = "visible", Value = "keep" },
                    new ReceiptEntity { Id = "later", Value = "keep" }
                ]
            }
        };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository, lifecycle: lifecycle);
        var boundary = (IDataQueryBoundary<ReceiptEntity, string>)facade;
        var requested = QueryDefinition.All
            .Where(Filter.Eq(nameof(ReceiptEntity.Value), "keep"))
            .WithPagination(1, 1);
        var (adapterQuery, residual) = FilterPushdownCoordinator.Plan(
            requested,
            FilterSupport.None,
            typeof(ReceiptEntity));

        var candidates = await boundary.QueryCandidates(adapterQuery);
        var finalized = FilterPushdownCoordinator.Finalize(requested, adapterQuery, residual, candidates);
        await boundary.MaterializeVisible(finalized.Page);

        finalized.Page.Should().ContainSingle().Which.Id.Should().Be("visible");
        observed.Should().Equal("visible");
    }

    [Fact]
    public async Task Complete_batch_outcomes_follow_logical_builder_order()
    {
        var repository = new ReceiptRepository();
        repository.Batch.Capabilities = BatchExecutionCapabilities.CompleteItemOutcomes;
        repository.Batch.SaveResult = new BatchResult(1, 0, 0)
        {
            HasCompleteItemOutcomes = true,
            Items =
            [
                new BatchItemResult(0, BatchOperation.Add, BatchItemOutcome.Applied),
                new BatchItemResult(1, BatchOperation.Update, BatchItemOutcome.Conflict),
                new BatchItemResult(2, BatchOperation.Delete, BatchItemOutcome.Missing)
            ]
        };
        var facade = new RepositoryFacade<ReceiptEntity, string>(repository);

        var result = await facade.CreateBatch()
            .Delete("gone")
            .Add(new ReceiptEntity { Id = "new" })
            .Update(new ReceiptEntity { Id = "existing" })
            .Save();

        result.Items.Select(item => (item.Index, item.Operation, item.Outcome)).Should().Equal(
            (0, BatchOperation.Delete, BatchItemOutcome.Missing),
            (1, BatchOperation.Add, BatchItemOutcome.Applied),
            (2, BatchOperation.Update, BatchItemOutcome.Conflict));
    }

    private sealed class ReceiptEntity : Entity<ReceiptEntity, string>
    {
        [Identifier]
        public override string Id { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        [Timestamp]
        public DateTimeOffset CreatedAt { get; set; }

        [Timestamp(OnSave = true)]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ReceiptRepository(
        bool advertiseAtomic = false,
        bool advertiseOutcomes = false,
        bool advertiseConditional = false,
        bool advertiseInsert = false) :
        IDataRepository<ReceiptEntity, string>,
        IInsertOnlyRepository<ReceiptEntity, string>,
        IQueryRepository<ReceiptEntity, string>,
        IMutationOutcomeRepository<ReceiptEntity, string>,
        IConditionalWriteRepository<ReceiptEntity, string>,
        IDescribesCapabilities
    {
        public IReadOnlyList<ReceiptEntity?> GetManyResult { get; init; } = [];
        public int GetCalls { get; private set; }
        public int InsertCalls { get; private set; }
        public Func<ReceiptEntity, MutationResult<ReceiptEntity, string>>? InsertResult { get; init; }
        public int UpsertCalls { get; private set; }
        public int UpsertManyCalls { get; private set; }
        public int? UpsertManyResult { get; init; }
        public ReceiptBatch Batch { get; } = new();
        public MutationOutcome UpsertOutcome { get; init; } = MutationOutcome.Updated;
        public bool ConditionalResult { get; init; }
        public int ConditionalCalls { get; private set; }
        public RepositoryQueryResult<ReceiptEntity> QueryResult { get; init; } = new() { Items = [] };

        public void Describe(ICapabilities capabilities)
        {
            if (advertiseAtomic) capabilities.Add(DataCaps.Write.AtomicBatch);
            if (advertiseOutcomes) capabilities.Add(DataCaps.Write.MutationOutcomes);
            if (advertiseConditional)
            {
                capabilities.Add(DataCaps.Write.ConditionalReplace);
                capabilities.Add(DataCaps.Query.Filter, FilterSupport.Full);
            }
            if (advertiseInsert) capabilities.Add(DataCaps.Write.InsertOnly);
        }

        public Task<MutationResult<ReceiptEntity, string>> Insert(ReceiptEntity model, CancellationToken ct = default)
        {
            InsertCalls++;
            return Task.FromResult(InsertResult!(model));
        }

        public Task<ReceiptEntity?> Get(string id, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult<ReceiptEntity?>(null);
        }

        public Task<IReadOnlyList<ReceiptEntity?>> GetMany(IEnumerable<string> ids, CancellationToken ct = default)
            => Task.FromResult(GetManyResult);

        public Task<ReceiptEntity> Upsert(ReceiptEntity model, CancellationToken ct = default)
        {
            UpsertCalls++;
            return Task.FromResult(model);
        }

        public Task<MutationResult<ReceiptEntity, string>> UpsertWithOutcome(
            ReceiptEntity model,
            CancellationToken ct = default)
            => Task.FromResult(new MutationResult<ReceiptEntity, string>(
                model.Id,
                UpsertOutcome,
                model,
                Koan.Data.Abstractions.Failures.DataCommitOutcome.Committed));

        public Task<bool> ConditionalReplaceAsync(
            ReceiptEntity model,
            Koan.Data.Abstractions.Filtering.Filter guard,
            CancellationToken ct = default)
        {
            ConditionalCalls++;
            return Task.FromResult(ConditionalResult);
        }

        public Task<bool> Delete(string id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> UpsertMany(IEnumerable<ReceiptEntity> models, CancellationToken ct = default)
        {
            UpsertManyCalls++;
            return Task.FromResult(UpsertManyResult ?? models.Count());
        }

        public Task<int> DeleteMany(IEnumerable<string> ids, CancellationToken ct = default)
            => Task.FromResult(ids.Count());

        public Task<RepositoryQueryResult<ReceiptEntity>> Query(
            QueryDefinition query,
            CancellationToken ct = default)
            => Task.FromResult(QueryResult);

        public Task<CountResult> Count(QueryDefinition query, CancellationToken ct = default)
            => Task.FromResult(CountResult.Exact(QueryResult.Items.Count));

        public Task<int> DeleteAll(CancellationToken ct = default) => Task.FromResult(0);
        public Task<long> RemoveAll(RemoveStrategy strategy, CancellationToken ct = default) => Task.FromResult(0L);
        public IBatchSet<ReceiptEntity, string> CreateBatch() => Batch;
    }

    private sealed class ReceiptBatch : IBatchSet<ReceiptEntity, string>
    {
        public List<ReceiptEntity> Added { get; } = [];
        public List<ReceiptEntity> Updated { get; } = [];
        public List<string> Deleted { get; } = [];
        public int SaveCalls { get; private set; }
        public BatchExecutionCapabilities Capabilities { get; set; }
        public BatchAtomicity ResultAtomicity { get; set; }
        public BatchResult? SaveResult { get; set; }

        public BatchExecutionCapabilities ExecutionCapabilities => Capabilities;

        public IBatchSet<ReceiptEntity, string> Add(ReceiptEntity entity) { Added.Add(entity); return this; }
        public IBatchSet<ReceiptEntity, string> Update(ReceiptEntity entity) { Updated.Add(entity); return this; }
        public IBatchSet<ReceiptEntity, string> Update(string id, Action<ReceiptEntity> mutate) => this;
        public IBatchSet<ReceiptEntity, string> Delete(string id) { Deleted.Add(id); return this; }
        public IBatchSet<ReceiptEntity, string> Clear() { Added.Clear(); Updated.Clear(); Deleted.Clear(); return this; }

        public Task<BatchResult> Save(BatchOptions? options = null, CancellationToken ct = default)
        {
            SaveCalls++;
            return Task.FromResult(SaveResult ?? new BatchResult(Added.Count, Updated.Count, Deleted.Count)
            {
                Atomicity = ResultAtomicity
            });
        }
    }
}
