using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Naming;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Connector.Mongo.Runtime;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Koan.Data.Connector.Mongo.Tests.Specs;

public sealed class MongoCounterpartQuerySpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output)
{
    [Fact]
    public async Task Canonical_authority_qualifies_translated_search_sort_count_and_page()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var canonical = NewPartition("canonical");
        var translated = NewPartition("translated");
        await Seed(canonical,
            new Catalog { Id = "a", Title = "Zulu", Published = true },
            new Catalog { Id = "b", Title = "Alpha", Published = true },
            new Catalog { Id = "hidden", Published = true, Suppressed = true },
            new Catalog { Id = "draft", Published = false },
            new Catalog { Id = "claimed", Published = false, Claims = ["bob"] },
            new Catalog { Id = "missing-translation", Published = true });
        await Seed(translated,
            new Catalog { Id = "a", Title = "match Alpha", Published = false },
            new Catalog { Id = "b", Title = "match Zulu", Suppressed = true },
            new Catalog { Id = "hidden", Title = "match Hidden", Published = true },
            new Catalog { Id = "draft", Title = "match Draft", Published = true, Claims = ["bob"] },
            new Catalog { Id = "claimed", Title = "match Middle" },
            new Catalog { Id = "orphan", Title = "match Orphan", Published = true });
        var authority = Filter.SameIdIn<Catalog>(item => !item.Suppressed &&
            (item.Published || item.Claims.Contains("bob")), canonical);
        var query = QueryDefinition.All.ForPartition(translated)
            .Where(Filter.All(LinqFilterCompiler.Compile<Catalog>(item => item.Title.Contains("match")), authority))
            .WithSort([Order(nameof(Catalog.Title))]).WithPagination(2, 1).WithCountStrategy(CountStrategy.Exact);
        var result = await Data<Catalog, string>.QueryWithCount(query);
        result.TotalCount.Should().Be(3);
        result.Items.Select(item => item.Id).Should().Equal("claimed");
        (await Data<Catalog, string>.Count(query)).Should().Be(3);
        var admin = await Data<Catalog, string>.QueryWithCount(query.WithoutPagination()
            .Where(Filter.SameIdIn<Catalog>(item => true, canonical)));
        admin.TotalCount.Should().Be(5);
        admin.Items.Should().NotContain(item => item.Id == "orphan" || item.Id == "missing-translation");
    }

    [Fact]
    public async Task Boolean_arms_keep_local_or_and_negated_counterpart_meaning()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var canonical = NewPartition("canonical");
        var translated = NewPartition("translated");
        await Seed(canonical, new Catalog { Id = "yes", Published = true }, new Catalog { Id = "no" });
        await Seed(translated, new Catalog { Id = "yes" }, new Catalog { Id = "no" }, new Catalog { Id = "orphan" });
        var eligible = Filter.SameIdIn<Catalog>(item => item.Published, canonical);
        var query = QueryDefinition.All.ForPartition(translated).WithCountStrategy(CountStrategy.Exact);
        var either = await Data<Catalog, string>.QueryWithCount(query.Where(Filter.Any(eligible, Filter.Eq("Id", "orphan"))));
        either.TotalCount.Should().Be(2);
        either.Items.Select(item => item.Id).Should().BeEquivalentTo("yes", "orphan");
        var negated = await Data<Catalog, string>.QueryWithCount(query.Where(Filter.Negate(eligible)));
        negated.TotalCount.Should().Be(2);
        negated.Items.Select(item => item.Id).Should().BeEquivalentTo("no", "orphan");
        var composite = await Data<Catalog, string>.QueryWithCount(query.Where(Filter.All(
            Filter.Any(eligible, Filter.Eq("Id", "orphan")), Filter.Negate(Filter.Eq("Id", "orphan")))));
        composite.Items.Select(item => item.Id).Should().Equal("yes");
    }

    [Fact]
    public async Task Envelope_preserves_colliding_source_members_and_computed_sort()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var canonical = NewPartition("canonical");
        var translated = NewPartition("translated");
        await Seed(canonical, new Catalog { Id = "a" }, new Catalog { Id = "b" });
        await Seed(translated,
            new Catalog { Id = "a", Scores = [new Score { Value = 20 }], __koanRow = "source row", __koanCounterpart0 = "source lookup", __koanOrder0 = "source sort" },
            new Catalog { Id = "b", Scores = [new Score { Value = 10 }] });
        var path = new MemberPath(typeof(Catalog),
            [typeof(Catalog).GetProperty(nameof(Catalog.Scores))!, typeof(Score).GetProperty(nameof(Score.Value))!],
            typeof(int), true, 1);
        var result = await Data<Catalog, string>.QueryWithCount(QueryDefinition.All.ForPartition(translated)
            .Where(Filter.SameIdIn<Catalog>(item => true, canonical))
            .WithSort([new SortSpec(path, true, SortAggregation.Max)]).WithPagination(1, 1).WithCountStrategy(CountStrategy.Exact));
        result.TotalCount.Should().Be(2);
        var row = result.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be("a");
        row.__koanRow.Should().Be("source row");
        row.__koanCounterpart0.Should().Be("source lookup");
        row.__koanOrder0.Should().Be("source sort");
        row.Scores.Single().Value.Should().Be(20);
    }

    [Fact]
    public async Task Native_command_receipts_prove_bounded_lookup_count_only_and_no_count()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var canonical = NewPartition("canonical");
        var translated = NewPartition("translated");
        await Seed(canonical, new Catalog { Id = "a", Published = true }, new Catalog { Id = "b", Published = true });
        await Seed(translated, new Catalog { Id = "a" }, new Catalog { Id = "b" }, new Catalog { Id = "orphan" });
        var factory = new MongoAdapterFactory();
        var native = factory.Create<Catalog, string>(host.Services);
        CounterpartQueryTarget target;
        using (EntityContext.With(partition: canonical))
            target = ((ICounterpartQueryRepository)native).BindCounterpartTarget();
        var bound = new BoundSameIdInFilter(Filter.Eq(nameof(Catalog.Published), true), target);
        var query = QueryDefinition.All.Where(Filter.All(bound, bound)).WithPagination(1, 1);
        var database = new MongoClient(Fixture.ConnectionString).GetDatabase(Fixture.Database);
        var collection = ((INamingProvider)factory).ResolveStorage(typeof(Catalog), translated, host.Services);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        try
        {
            using var outer = EntityContext.With(partition: translated);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var page = await ((IQueryRepository<Catalog, string>)native).Query(query);
            page.Items.Should().ContainSingle();
            page.TotalCount.Should().BeNull();
            page.CountExecution.Should().Be(CountExecutionKind.None);
            var count = await ((IQueryRepository<Catalog, string>)native).Count(query);
            count.Value.Should().Be(2);
            var bounded = await ((IBoundedQueryRepository<Catalog, string>)native).QueryBoundedCandidates(query, 1);
            bounded.Items.Should().ContainSingle();
            bounded.CandidateLimitExceeded.Should().BeTrue();
            var keyed = await ((IQueryRepository<Catalog, string>)native).Query(query.Where(Filter.All(bound, Filter.Eq("Id", "a"))));
            keyed.Items.Single().Id.Should().Be("a");
            var commands = await database.GetCollection<BsonDocument>("system.profile")
                .Find(new BsonDocument("command.aggregate", collection)).ToListAsync();
            commands.Should().HaveCount(4);
            var pipelines = commands.Select(command => command["command"]["pipeline"].AsBsonArray).ToArray();
            pipelines.Count(pipeline => pipeline.Any(stage => stage.AsBsonDocument.Contains("$count"))).Should().Be(1);
            foreach (var pipeline in pipelines)
            {
                var lookup = pipeline.Where(stage => stage.AsBsonDocument.Contains("$lookup")).Should().ContainSingle().Subject["$lookup"];
                var inner = lookup["pipeline"].AsBsonArray;
                inner.Should().Contain(stage => stage.AsBsonDocument.Contains("$limit") && stage["$limit"] == 1);
                inner.Last()["$project"].AsBsonDocument.Should().Equal(new BsonDocument("_id", 1));
            }
            var countPipeline = pipelines.Single(pipeline => pipeline.Any(stage => stage.AsBsonDocument.Contains("$count")));
            countPipeline.Should().NotContain(stage => stage.AsBsonDocument.Contains("$sort") || stage.AsBsonDocument.Contains("$skip"));
            var explain = await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                ["explain"] = new BsonDocument { ["aggregate"] = collection, ["pipeline"] = countPipeline, ["cursor"] = new BsonDocument() },
                ["verbosity"] = "executionStats"
            });
            var lookupStats = explain["stages"].AsBsonArray.Single(stage => stage.AsBsonDocument.Contains("$lookup"));
            lookupStats["indexesUsed"].AsBsonArray.Should().Contain(new BsonString("_id_"));
            var keyedPipeline = pipelines.Single(pipeline => pipeline[0].AsBsonDocument.Contains("$match"));
            var keyedExplain = await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                ["explain"] = new BsonDocument { ["aggregate"] = collection, ["pipeline"] = keyedPipeline, ["cursor"] = new BsonDocument() },
                ["verbosity"] = "executionStats"
            });
            var keyedStages = keyedExplain["stages"].AsBsonArray;
            var cursorStats = keyedStages.Single(stage => stage.AsBsonDocument.Contains("$cursor"))["$cursor"];
            UsesIndex(cursorStats["queryPlanner"]["winningPlan"], "_id_").Should().BeTrue();
            cursorStats["executionStats"]["totalDocsExamined"].ToInt64().Should().Be(1);
            keyedStages.Single(stage => stage.AsBsonDocument.Contains("$lookup"))["totalDocsExamined"].ToInt64().Should().Be(1);
            Output.WriteLine($"Four native counterpart operations completed in {timer.ElapsedMilliseconds} ms; keyed selection examines one outer and one counterpart document through identity indexes. Count and page remain separate committed reads.");
        }
        finally
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        }
    }

    [Fact]
    public async Task Native_bound_plan_rejects_unbound_nested_and_foreign_targets_before_dispatch()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var native = new MongoAdapterFactory().Create<Catalog, string>(host.Services);
        var query = (IQueryRepository<Catalog, string>)native;
        await FluentActions.Invoking(() => query.Query(QueryDefinition.All.Where(Filter.SameIdIn<Catalog>(item => true, ""))))
            .Should().ThrowAsync<NotSupportedException>();
        var target = ((ICounterpartQueryRepository)native).BindCounterpartTarget();
        await FluentActions.Invoking(() => query.Query(QueryDefinition.All.Where(new BoundSameIdInFilter(
            Filter.SameIdIn<Catalog>(item => true, ""), target))))
            .Should().ThrowAsync<NotSupportedException>();
        await FluentActions.Invoking(() => query.Query(QueryDefinition.All.Where(new BoundSameIdInFilter(
            Filter.All(), new ForeignTarget(typeof(Catalog))))))
            .Should().ThrowAsync<NotSupportedException>();
        var factory = new MongoAdapterFactory();
        var foreignRoute = new MongoRepository<Catalog, string>(host.Services, factory,
            factory.ResolveRoute(host.Services, "Default") with { Database = "foreign-counterpart-database" },
            host.Services.GetRequiredService<MongoClientManager>(), null);
        await FluentActions.Invoking(() => query.Query(QueryDefinition.All.Where(new BoundSameIdInFilter(
            Filter.All(), foreignRoute.BindCounterpartTarget()))))
            .Should().ThrowAsync<NotSupportedException>();
        var foreignEntity = factory.Create<Invoice, Guid>(host.Services);
        await FluentActions.Invoking(() => query.Query(QueryDefinition.All.Where(new BoundSameIdInFilter(
            Filter.All(), ((ICounterpartQueryRepository)foreignEntity).BindCounterpartTarget()))))
            .Should().ThrowAsync<NotSupportedException>();
        await FluentActions.Invoking(() => query.Query(QueryDefinition.All.Where(new BoundSameIdInFilter(
            new ClrFilter((System.Linq.Expressions.Expression<Func<Catalog, bool>>)(item => Arbitrary(item))), target))))
            .Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Mutable_identity_has_no_counterpart_capability_and_refuses_binding_or_candidates()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var native = new MongoAdapterFactory().Create<MutableKeyCatalog, byte[]>(host.Services);
        var capabilities = Koan.Data.Abstractions.Capabilities.DataCaps.Describe(native, "mongo");
        capabilities.Detail<FilterSupport>(Koan.Data.Abstractions.Capabilities.DataCaps.Query.Filter)!
            .SupportsSameIdIn.Should().BeFalse();
        FluentActions.Invoking(() => ((ICounterpartQueryRepository)native).BindCounterpartTarget())
            .Should().Throw<NotSupportedException>().WithMessage("*keys*");
        await FluentActions.Invoking(() => Data<MutableKeyCatalog, byte[]>.QueryWithCount(QueryDefinition.All
            .Where(Filter.SameIdIn<MutableKeyCatalog>(row => true, ""))))
            .Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Load_hook_cannot_substitute_an_unqualified_identity_in_counterpart_results()
    {
        RequireBackingStore();
        var mutateIdentity = false;
        var loads = 0;
        await using var host = await BootAsync(() => LoadedCatalog.Lifecycle.AfterLoad(context =>
        {
            loads++;
            if (mutateIdentity) context.Current.Id = "foreign";
        }));
        var canonical = NewPartition("canonical");
        var translated = NewPartition("translated");
        using (EntityContext.With(partition: canonical)) await new LoadedCatalog { Id = "a" }.Save();
        using (EntityContext.With(partition: translated)) await new LoadedCatalog { Id = "a" }.Save();
        var query = QueryDefinition.All.ForPartition(translated)
            .Where(Filter.SameIdIn<LoadedCatalog>(item => true, canonical)).WithCountStrategy(CountStrategy.Exact);
        mutateIdentity = true;
        await FluentActions.Invoking(() => Data<LoadedCatalog, string>.QueryWithCount(query))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Counterpart read evidence*");
        var repository = (IQueryRepository<LoadedCatalog, string>)host.Services.GetRequiredService<IDataService>()
            .GetRepository<LoadedCatalog, string>();
        await FluentActions.Invoking(() => repository.Query(query))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Counterpart read evidence*");
        await FluentActions.Invoking(() => ((IBoundedQueryRepository<LoadedCatalog, string>)repository)
                .QueryBoundedCandidates(query, 10))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Counterpart read evidence*");
        await FluentActions.Invoking(async () =>
        {
            await foreach (var row in LoadedCatalog.QueryStream(query, batchSize: 10))
                throw new InvalidOperationException("An unqualified row was emitted by the stream.");
        }).Should().ThrowAsync<InvalidOperationException>().WithMessage("*Counterpart read evidence*");
        loads.Should().Be(4, "only the selected outer row is hydrated for each rejected query");
        mutateIdentity = false;
        var result = await Data<LoadedCatalog, string>.QueryWithCount(query);
        result.Items.Single().Id.Should().Be("a");
        result.TotalCount.Should().Be(1);
        loads.Should().Be(5);
    }

    [Fact]
    public async Task Independent_invoice_shape_joins_native_guid_identity_without_string_conversion()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var canonical = NewPartition("invoice-source");
        var reporting = NewPartition("invoice-report");
        var approved = Guid.NewGuid();
        var denied = Guid.NewGuid();
        using (EntityContext.With(partition: canonical))
        {
            await new Invoice { Id = approved, Approved = true }.Save();
            await new Invoice { Id = denied, Approved = false }.Save();
        }
        using (EntityContext.With(partition: reporting))
        {
            await new Invoice { Id = approved }.Save();
            await new Invoice { Id = denied, Approved = true }.Save();
            await new Invoice { Id = Guid.NewGuid(), Approved = true }.Save();
        }
        var result = await Data<Invoice, Guid>.QueryWithCount(QueryDefinition.All.ForPartition(reporting)
            .Where(Filter.SameIdIn<Invoice>(item => item.Approved, canonical)).WithCountStrategy(CountStrategy.Exact));
        result.TotalCount.Should().Be(1);
        result.Items.Single().Id.Should().Be(approved);
    }

    [Fact]
    public async Task Committed_revocation_and_deletion_affect_next_query_and_cancellation_restores_partition()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var canonical = NewPartition("canonical");
        var translated = NewPartition("translated");
        await Seed(canonical, new Catalog { Id = "a", Published = true });
        await Seed(translated, new Catalog { Id = "a" });
        var query = QueryDefinition.All.ForPartition(translated)
            .Where(Filter.SameIdIn<Catalog>(item => item.Published, canonical)).WithCountStrategy(CountStrategy.Exact);
        (await Data<Catalog, string>.Count(query)).Should().Be(1);
        await Seed(canonical, new Catalog { Id = "a", Published = false });
        (await Data<Catalog, string>.Count(query)).Should().Be(0);
        await Seed(canonical, new Catalog { Id = "a", Published = true });
        using (EntityContext.With(partition: canonical)) await Catalog.Remove("a");
        (await Data<Catalog, string>.Count(query)).Should().Be(0);
        using var outer = EntityContext.With(partition: "caller");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions.Invoking(() => Data<Catalog, string>.QueryWithCount(query, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        EntityContext.Current?.Partition.Should().Be("caller");
    }

    [Fact]
    public async Task Concurrent_partition_queries_keep_each_captured_target_and_outer_collection()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var left = NewPartition("left");
        var right = NewPartition("right");
        var translated = NewPartition("translated");
        await Seed(left, new Catalog { Id = "left", Published = true });
        await Seed(right, new Catalog { Id = "right", Published = true });
        await Seed(translated, new Catalog { Id = "left" }, new Catalog { Id = "right" });
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var partition = index % 2 == 0 ? left : right;
            var expected = index % 2 == 0 ? "left" : "right";
            var result = await Data<Catalog, string>.QueryWithCount(QueryDefinition.All.ForPartition(translated)
                .Where(Filter.SameIdIn<Catalog>(item => item.Published, partition)).WithCountStrategy(CountStrategy.Exact));
            result.TotalCount.Should().Be(1);
            result.Items.Single().Id.Should().Be(expected);
            return result;
        }));
        results.Should().HaveCount(12);
    }

    [Fact]
    public async Task Ordinary_managed_binary_filter_preserves_native_scalar_support()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var partition = NewPartition("binary");
        await Seed(partition, new Catalog { Id = "a" }, new Catalog { Id = "b" });
        var factory = new MongoAdapterFactory();
        var name = ((INamingProvider)factory).ResolveStorage(typeof(Catalog), partition, host.Services);
        var database = new MongoClient(Fixture.ConnectionString).GetDatabase(Fixture.Database);
        var collection = database.GetCollection<BsonDocument>(name);
        await collection.UpdateOneAsync(new BsonDocument("_id", "a"),
            new BsonDocument("$set", new BsonDocument("binary", new BsonBinaryData(new byte[] { 1, 2 }))));
        var predicate = Filter.On(FieldPath.Managed("binary", typeof(byte[])), FilterOperator.Eq,
            FilterValue.Of(new byte[] { 1, 2 }));
        var query = QueryDefinition.All.ForPartition(partition).Where(predicate);
        (await Data<Catalog, string>.QueryWithCount(query)).Items.Select(row => row.Id).Should().Equal("a");
        (await Data<Catalog, string>.Count(query)).Should().Be(1);
    }

    [Fact]
    public async Task Counterpart_boolean_dedup_preserves_datetime_native_interpretation()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var canonical = NewPartition("dates");
        var translated = NewPartition("date-replicas");
        var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Local);
        await Seed(canonical, new Catalog { Id = "utc", Created = utc }, new Catalog { Id = "local", Created = local });
        await Seed(translated, new Catalog { Id = "utc" }, new Catalog { Id = "local" });
        var first = Filter.SameIdIn<Catalog>(row => row.Created == utc, canonical);
        var second = Filter.SameIdIn<Catalog>(row => row.Created == local, canonical);
        var query = QueryDefinition.All.ForPartition(translated);
        (await Data<Catalog, string>.QueryWithCount(query.Where(Filter.Any(first, second))))
            .Items.Select(row => row.Id).Should().BeEquivalentTo("utc", "local");
        (await Data<Catalog, string>.Count(query.Where(Filter.All(first, second))))
            .Should().Be(utc == local.ToUniversalTime() ? 2 : 0);
    }

    private static bool Arbitrary(Catalog item) => item.Title.GetHashCode() == 7;
    private static bool UsesIndex(BsonValue value, string index) => value switch
    {
        BsonDocument document => document.Elements.Any(element =>
            element.Name == "indexName" && element.Value == index || UsesIndex(element.Value, index)),
        BsonArray array => array.Any(item => UsesIndex(item, index)),
        _ => false
    };
    private sealed record ForeignTarget(Type EntityType) : CounterpartQueryTarget(EntityType);

    private static SortSpec Order(string property) => new(new MemberPath(typeof(Catalog),
        [typeof(Catalog).GetProperty(property)!], typeof(string), false, -1), false);

    private static async Task Seed(string partition, params Catalog[] rows)
    {
        using var scope = EntityContext.With(partition: partition);
        foreach (var row in rows) await row.Save();
    }

    [Storage(Name = "KOAN_COUNTERPART_CATALOG")]
    public sealed class Catalog : Entity<Catalog>
    {
        public string Title { get; set; } = "";
        public DateTime Created { get; set; }
        public bool Published { get; set; }
        public bool Suppressed { get; set; }
        public List<string> Claims { get; set; } = [];
        public List<Score> Scores { get; set; } = [];
        public string __koanRow { get; set; } = "";
        public string __koanCounterpart0 { get; set; } = "";
        public string __koanOrder0 { get; set; } = "";
    }

    public sealed class Score { public int Value { get; set; } }

    public sealed class Invoice : Entity<Invoice, Guid> { public bool Approved { get; set; } }
    public sealed class LoadedCatalog : Entity<LoadedCatalog>;
    public sealed class MutableKeyCatalog : Entity<MutableKeyCatalog, byte[]>;
}
