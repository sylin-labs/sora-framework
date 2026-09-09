using Koan.Data.Abstractions.Filtering;
using Koan.Data.Core.Querying;
using Koan.Tests.Data.Core.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Data.Core.Specs.Entity;

public sealed class CounterpartQuerySemanticsSpec
{
    [Theory]
    [InlineData("")]
    [InlineData("selected")]
    [InlineData(null)]
    public async Task Structured_query_selects_partition_before_execution_and_restores_ambient(string? selected)
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var adapter = EntityContext.Adapter("inmemory");
        var id = Guid.NewGuid().ToString("N");
        foreach (var name in new[] { "", "selected", "ambient" })
        {
            using var partition = EntityContext.Partition(name);
            await new TodoEntity { Id = id, Title = name.Length == 0 ? "default" : name }.Save();
        }
        using var ambient = EntityContext.Partition("ambient");
        var query = QueryDefinition.All.Where(Filter.Eq(nameof(TodoEntity.Id), id)).ForPartition(selected);
        var result = await TodoEntity.AllWithCount(query);
        result.Items.Single().Title.Should().Be(selected switch { null => "ambient", "" => "default", _ => selected });
        result.TotalCount.Should().Be(1);
        EntityContext.Current!.Partition.Should().Be("ambient");
        (await Data<TodoEntity, string>.Count(query)).Should().Be(1);
        EntityContext.Current!.Partition.Should().Be("ambient");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ((Func<Task>)(() => TodoEntity.AllWithCount(query, cancelled.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        EntityContext.Current!.Partition.Should().Be("ambient");
    }

    [Fact]
    public async Task Empty_partition_keyed_helpers_read_default_instead_of_stale_ambient_replica()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var adapter = EntityContext.Adapter("inmemory");
        var id = Guid.NewGuid().ToString("N");
        using (EntityContext.Partition("")) await new TodoEntity { Id = id, Title = "Suppressed" }.Save();
        using var ambient = EntityContext.Partition("fr");
        await new TodoEntity { Id = id, Title = "Stale published" }.Save();
        (await TodoEntity.Get(id, string.Empty))!.Title.Should().Be("Suppressed");
        (await TodoEntity.Get(new[] { id }, string.Empty)).Single()!.Title.Should().Be("Suppressed");
        (await TodoEntity.Get(id))!.Title.Should().Be("Stale published");
        EntityContext.Current!.Partition.Should().Be("fr");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ((Func<Task>)(() => TodoEntity.Get(id, string.Empty, cancelled.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        EntityContext.Current!.Partition.Should().Be("fr");
        await ((Func<Task>)(() => TodoEntity.Get(new[] { id }, string.Empty, cancelled.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        EntityContext.Current!.Partition.Should().Be("fr");
    }

    [Fact]
    public async Task Unsupported_counterpart_rejects_and_mutation_preserves_the_row()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var row = await new TodoEntity { Title = "Preserved" }.Save();
        var filter = Filter.SameIdIn<TodoEntity>(item => item.Title == "Visible", "");
        await ((Func<Task>)(() => TodoEntity.AllWithCount(QueryDefinition.All.Where(filter))))
            .Should().ThrowAsync<NotSupportedException>();
        await ((Func<Task>)(() => TodoEntity.Remove(row.Id, QueryDefinition.All.Where(filter))))
            .Should().ThrowAsync<NotSupportedException>();
        (await TodoEntity.Get(row.Id))!.Title.Should().Be("Preserved");
        var repository = runtime.Services.GetRequiredService<IDataService>().GetRepository<TodoEntity, string>();
        await ((Func<Task>)(() => ((IQueryRepository<TodoEntity, string>)repository).Query(QueryDefinition.All.Where(filter))))
            .Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void Counterpart_tree_never_splits_to_a_clr_residual()
    {
        var native = FilterSupport.Full with { SupportsSameIdIn = true };
        var same = Filter.SameIdIn<TodoEntity>(item => item.Title == "Visible", "");
        var residual = new ClrFilter((System.Linq.Expressions.Expression<Func<TodoEntity, bool>>)(item => TestPredicate(item)));
        ((Action)(() => FilterPushdownCoordinator.Plan(QueryDefinition.All.Where(Filter.All(same, residual)), native, typeof(TodoEntity))))
            .Should().Throw<NotSupportedException>();
        ((Action)(() => FilterPushdownCoordinator.Plan(QueryDefinition.All.Where(same), FilterSupport.Full, typeof(TodoEntity))))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Normalized_snapshots_own_operand_and_set_collections()
    {
        var values = new List<object?> { "original" };
        var operands = new List<Filter> { Filter.In(nameof(TodoEntity.Title), values) };
        var snapshot = Filter.Snapshot(new AllOf(operands));
        values[0] = "changed";
        operands.Clear();
        InMemoryFilterEvaluator.Compile<TodoEntity>(snapshot!)(new TodoEntity { Title = "original" }).Should().BeTrue();
        InMemoryFilterEvaluator.Compile<TodoEntity>(snapshot!)(new TodoEntity { Title = "changed" }).Should().BeFalse();
    }

    [Fact]
    public void Snapshots_own_binary_values_and_limit_opaque_values_only_for_counterpart_proof()
    {
        var bytes = new byte[] { 1, 2 };
        var original = Filter.Eq("bytes", bytes);
        var snapshot = Filter.Snapshot(original)!;
        Filter.Equivalent(snapshot, original).Should().BeTrue();
        bytes[0] = 9;
        Filter.Equivalent(snapshot, original).Should().BeFalse();
        var opaque = new List<string> { "provider value" };
        Filter.Snapshot(Filter.Eq("opaque", opaque)).Should().NotBeNull();
        var authority = Filter.SameIdIn<TodoEntity>(item => true, "");
        ((Action)(() => Filter.Snapshot(Filter.All(authority, Filter.Eq("opaque", opaque)))))
            .Should().Throw<NotSupportedException>();
        ((Action)(() => Filter.Snapshot(Filter.All(authority, Filter.In("opaque", new object?[] { opaque })))))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Equivalent_dates_preserve_kind_for_scalar_and_set_counterpart_deduplication()
    {
        var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Local);
        Filter.Equivalent(Filter.Eq("date", utc), Filter.Eq("date", local)).Should().BeFalse();
        Filter.Equivalent(Filter.In("date", new object?[] { utc }), Filter.In("date", new object?[] { local })).Should().BeFalse();
    }

    [Fact]
    public void Sort_member_path_owns_the_supplied_member_list()
    {
        var title = typeof(TodoEntity).GetProperty(nameof(TodoEntity.Title))!;
        var members = new List<System.Reflection.MemberInfo> { title };
        var path = new Koan.Data.Abstractions.Sorting.MemberPath(typeof(TodoEntity), members, typeof(string), false, -1);
        members[0] = typeof(TodoEntity).GetProperty(nameof(TodoEntity.Description))!;
        path.Members.Should().Equal(title);
        path.DotPath.Should().Be(nameof(TodoEntity.Title));
        ((Action)(() => ((IList<System.Reflection.MemberInfo>)path.Members)[0] = members[0]))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Internal_read_evidence_is_absent_from_both_json_serializers()
    {
        var evidence = new UnserializableEvidence();
        object[] carriers =
        [
            new RepositoryQueryResult<TodoEntity> { Items = [], ReadEvidence = evidence },
            new BoundedQueryResult<TodoEntity>([], 0, false) { ReadEvidence = evidence },
            new Koan.Data.Core.Relationships.RelationshipQueryResult<TodoEntity, string>(
                new Dictionary<string, IReadOnlyList<TodoEntity>>(),
                new(Koan.Data.Core.Relationships.RelationshipExecutionMode.Native, "test", 0, 0, 0, null))
                { ReadEvidence = evidence },
            new QueryResult<TodoEntity> { Items = [], TotalCount = 0, Page = 1, PageSize = 10, ReadEvidence = evidence }
        ];
        foreach (var carrier in carriers)
        {
            Newtonsoft.Json.JsonConvert.SerializeObject(carrier).Should().NotContain("ReadEvidence");
            System.Text.Json.JsonSerializer.Serialize(carrier, carrier.GetType()).Should().NotContain("ReadEvidence");
        }
    }

    private sealed class UnserializableEvidence : IQueryReadEvidence
    {
        public Type EntityType => throw new InvalidOperationException("Internal evidence was serialized.");
        public Type KeyType => throw new InvalidOperationException();
        public string? Partition => throw new InvalidOperationException();
        public bool CoversRows(IEnumerable<object> rows) => false;
        public bool IsCurrentScope() => false;
    }

    [Fact]
    public async Task Lambda_and_json_query_overloads_preserve_structured_constraints()
    {
        await using var runtime = await DataCoreRuntimeFixture.CreateAsync();
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        await new TodoEntity { Title = "candidate", Description = "hidden" }.Save();
        var visible = await new TodoEntity { Title = "candidate", Description = "visible" }.Save();
        var query = QueryDefinition.All.Where(Filter.Eq(nameof(TodoEntity.Description), "visible"));
        (await TodoEntity.Query(item => item.Title == "candidate", query)).Select(row => row.Id).Should().Equal(visible.Id);
        (await TodoEntity.Query("{\"Title\":\"candidate\"}", query)).Select(row => row.Id).Should().Equal(visible.Id);
    }

    private static bool TestPredicate(TodoEntity item) => item.Title.Length > 0;
}
