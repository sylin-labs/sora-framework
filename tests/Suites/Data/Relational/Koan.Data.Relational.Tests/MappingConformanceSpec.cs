using AwesomeAssertions;
using Koan.Core;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core;
using Koan.Data.Core.Options;
using Koan.Data.Relational;
using Koan.Data.Relational.Linq;
using Koan.Data.Relational.Mapping;
using Koan.Data.Relational.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Data.Relational.Tests;

public sealed class MappingConformanceSpec
{
    [Fact]
    public void Enum_mapping_declares_string_columns_and_round_trips_names_inside_objects()
    {
        using var provider = Host(source => source.Map<EnumDocument>(map => map
            .Container("ENUM_DOCUMENT")
            .Key(item => item.Id).Name("ID")
            .Property(item => item.State).Name("STATE")
            .Property(item => item.Details).Object("DETAILS")));
        var plan = provider.GetRequiredService<IDataMappingPlans>().Require<EnumDocument>("Legacy");
        var source = new EnumDocument { Id = 7, State = EnumState.Ready,
            Details = new EnumDetails { State = EnumState.Closed, States = [EnumState.Ready, EnumState.Closed] } };
        var record = plan.Write(source);
        record.Values.Single(value => value.Path.Name == "STATE").Value.Should().Be("ready-for-use");
        var state = plan.Bindings.Single(binding => binding.LogicalPath.Equals(MappingPath.Of("State")));
        state.PhysicalType.Should().Be(typeof(string));
        state.Descriptor.PhysicalType.Should().Be(typeof(string));
        var json = new RelationalStructuredValueCodec().Serialize(record.Values.Single(value => value.Path.Name == "DETAILS").Value);
        json.Should().Contain("\"State\":\"Closed\"").And.Contain("\"States\":[\"ready-for-use\",\"Closed\"]");
        var restored = plan.Hydrate<EnumDocument>(record.Values);
        restored.State.Should().Be(EnumState.Ready);
        restored.Details.States.Should().Equal(EnumState.Ready, EnumState.Closed);
    }

    public enum EnumState { [System.Runtime.Serialization.EnumMember(Value = "ready-for-use")] Ready = 1, Closed = 2 }
    public sealed class EnumDetails
    {
        public EnumState State { get; set; }
        public EnumState[] States { get; set; } = [];
    }
    public sealed class EnumDocument
    {
        public long Id { get; set; }
        public EnumState State { get; set; }
        public EnumDetails Details { get; set; } = new();
    }

    [Fact]
    public void Compact_hybrid_map_round_trips_and_preserves_missing_complex_values()
    {
        using var provider = Host(source => source.Map<Customer>(map => map
            .Container("dbo", "CUSTOMER")
            .Key(customer => customer.Id).Name("CUSTOMER_NO")
            .Property(customer => customer.Name.Full).Name("DISPLAY_NM")
            .Property(customer => customer.Profile).Object("PROFILE_JSON")));
        var plan = provider.GetRequiredService<IDataMappingPlans>().Require<Customer>("Legacy");
        var customer = new Customer
        {
            Id = 42,
            Name = new CustomerName { Full = "Ada Lovelace", First = "Ada" },
            Profile = new CustomerProfile { PreferredLanguage = "en", Tags = ["math", "code"] }
        };

        var record = plan.Write(customer);
        var copy = plan.Hydrate<Customer>(record.Values);

        record.Values.Select(static value => value.Path.ToString()).Should()
            .Equal("CUSTOMER_NO", "DISPLAY_NM", "PROFILE_JSON");
        record.Values[2].Value.Should().BeOfType<DataObject>();
        copy.Id.Should().Be(42);
        copy.Name.Full.Should().Be("Ada Lovelace");
        copy.Name.First.Should().BeEmpty("the unmapped logical value was missing, not an instruction to invent data");
        copy.Profile.PreferredLanguage.Should().Be("en");
        copy.Profile.Tags.Should().Equal("math", "code");
    }

    [Fact]
    public void Identity_plus_object_excludes_the_independent_key_and_nested_physical_path_round_trips()
    {
        using var provider = Host(source => source
            .Map<Customer>(map => map
                .Container("CUSTOMER")
                .Key(customer => customer.Id).Name("Id")
                .Object("Data"))
            .Map<FlatCustomer>(map => map
                .Container("FLAT_CUSTOMER")
                .Key(customer => customer.Id).Name("ID")
                .Property(customer => customer.NameFull).Path("NAME_DATA", "full")));
        var plans = provider.GetRequiredService<IDataMappingPlans>();

        var whole = plans.Require<Customer>("Legacy");
        var wholeRecord = whole.Write(new Customer { Id = 9, Name = new CustomerName { Full = "Grace" } });
        var data = wholeRecord.Values.Single(value => value.Path.Name == "Data").Value.Should().BeOfType<DataObject>().Subject;
        data.Properties.Select(static property => property.Name).Should().NotContain("Id");
        whole.Hydrate<Customer>(wholeRecord.Values).Name.Full.Should().Be("Grace");

        var nested = plans.Require<FlatCustomer>("Legacy");
        var nestedRecord = nested.Write(new FlatCustomer { Id = 7, NameFull = "Lin" });
        nestedRecord.Values.Single(value => value.BindingId.Contains("NameFull", StringComparison.Ordinal)).Path
            .Should().Be(new PhysicalPath("NAME_DATA", "full"));
        nested.Hydrate<FlatCustomer>(nestedRecord.Values).NameFull.Should().Be("Lin");
    }

    [Fact]
    public void Managed_identity_object_accepts_getter_only_derived_paths_without_granting_hydration_authority()
    {
        var plan = RelationalManagedMapping.Compile<ComputedIdentity>(
            "Default",
            StorageAddress.From("SENSORS"));
        var record = plan.Write(new ComputedIdentity { Id = "sensor-1", DisplayName = "North bed" });

        var serial = plan.Use(MappingPath.Of(nameof(ComputedIdentity.Serial)), MappingConsumer.Filter)
            .Bindings.Should().ContainSingle().Which;
        var copy = plan.Hydrate<ComputedIdentity>(record.Values.Append(new MappedValue(
            serial.Id,
            serial.PhysicalPath,
            serial.Shape,
            "provider-observation-must-not-assign")));

        serial.Descriptor.Authority.Should().Be(MappingAuthority.Derived);
        plan.Read().Bindings.Should().NotContain(serial, "derived paths do not independently hydrate the aggregate");
        copy.Id.Should().Be("sensor-1");
        copy.Serial.Should().Be("sensor-1");
        copy.DisplayName.Should().Be("North bed");
    }

    [Fact]
    public void Composite_and_generated_identity_are_complete_pre_dispatch_decisions()
    {
        using var provider = Host(source => source
            .Map<CustomerSite>(map => map
                .Container("CUSTOMER_SITE")
                .Key(site => site.Id).Parts(parts => parts
                    .Property(key => key.CustomerNo).Name("CUSTOMER_NO")
                    .Property(key => key.SiteNo).Name("SITE_NO"))
                .Property(site => site.DisplayName).Name("DISPLAY_NAME"))
            .Map<GeneratedCustomer>(map => map
                .Container("CUSTOMER")
                .Key(customer => customer.Id).Name("CUSTOMER_NO").Generated()
                .Property(customer => customer.DisplayName).Name("DISPLAY_NAME")));
        var plans = provider.GetRequiredService<IDataMappingPlans>();

        var composite = plans.Require<CustomerSite>("Legacy");
        var record = composite.Write(new CustomerSite { Id = new CustomerSiteId(5, 2), DisplayName = "HQ" });
        composite.WriteIdentity(new CustomerSiteId(5, 2)).Values.Select(static value => value.Value).Should().Equal(5L, (short)2);
        composite.Hydrate<CustomerSite>(record.Values).Id.Should().Be(new CustomerSiteId(5, 2));
        Action partial = () => composite.Hydrate<CustomerSite>(record.Values.Where(value => value.Path.Name != "SITE_NO"));
        partial.Should().Throw<MappingValueException>().WithMessage("*every declared part*");

        var generated = plans.Require<GeneratedCustomer>("Legacy");
        generated.Write(new GeneratedCustomer { DisplayName = "new" }).Values
            .Should().ContainSingle(value => value.Path.Name == "DISPLAY_NAME");
        generated.Identity.IsGenerated.Should().BeTrue();
    }

    [Fact]
    public void Codec_and_every_consumer_share_one_binding_and_one_physical_encoding()
    {
        var codec = new DataMappingCodec<bool, string>(
            value => value ? "Y" : "N",
            value => value == "Y",
            "legacy-yes-no-v1");
        using var provider = Host(source => source.Map<FlaggedCustomer>(map => map
            .Container("FLAGS")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.Enabled).Name("IS_ENABLED").Codec(codec)));
        var plan = provider.GetRequiredService<IDataMappingPlans>().Require<FlaggedCustomer>("Legacy");
        var consumers = new[]
        {
            MappingConsumer.Filter, MappingConsumer.Order, MappingConsumer.Patch,
            MappingConsumer.ConditionalWrite, MappingConsumer.Projection, MappingConsumer.Index
        };
        var uses = consumers.Select(consumer => plan.Use(MappingPath.Of("Enabled"), consumer)).ToArray();

        uses.Select(static use => use.Bindings[0]).Should().OnlyContain(binding => ReferenceEquals(binding, uses[0].Bindings[0]));
        uses.Should().OnlyContain(use => use.Receipt.PlanId == plan.Id);
        plan.Write(new FlaggedCustomer { Id = 1, Enabled = true }).Values.Single(value => value.Path.Name == "IS_ENABLED").Value
            .Should().Be("Y");

        var dialect = new SpyDialect();
        var translated = new SqlFilterTranslator(dialect, plan).Translate(Filter.Eq("Enabled", true));
        translated.whereSql.Should().Be("(read(IS_ENABLED) = @p0)");
        translated.parameters.Should().Equal("Y");
        ReferenceEquals(
            plan.Use(MappingPath.Of("Enabled"), MappingConsumer.Filter),
            plan.Use(MappingPath.Of("Enabled"), MappingConsumer.Filter)).Should().BeTrue("warm uses are compiled once");
        var index = plan.Indexes.Single(item => !item.Primary);
        ReferenceEquals(index.Bindings[0], uses[0].Bindings[0]).Should().BeTrue();
    }

    [Fact]
    public void Managed_identity_object_convention_compiles_selective_subpaths_without_a_second_resolver()
    {
        using var provider = Host(_ => { });
        var plans = provider.GetRequiredService<IDataMappingPlans>();
        var plan = plans.GetOrAdd<Customer>(
            "Legacy",
            new MappingConvention(StorageAddress.From("dbo", "CUSTOMER"), "Id", "Json"));

        var full = plan.Write(new Customer { Id = 4, Name = new CustomerName { Full = "Katherine" } });
        var selected = plan.Read(MappingPath.Of("Name", "Full"));
        var filter = plan.Use(MappingPath.Of("Name", "Full"), MappingConsumer.Filter);

        full.Values.Select(static value => value.Path.Name).Should().Equal("Id", "Json");
        selected.PhysicalRoots.Should().Equal("Json");
        selected.Bindings.Should().ContainSingle().Which.PhysicalPath.Should().Be(new PhysicalPath("Json", "Name", "Full"));
        ReferenceEquals(selected.Bindings[0], filter.Bindings[0]).Should().BeTrue();
        selected.Receipt.NativeProofRequired.Should().BeTrue();
    }

    [Fact]
    public void Managed_root_object_preserves_complex_collections_without_inventing_query_bindings()
    {
        var plan = RelationalManagedMapping.Compile<ManagedAggregate>(
            "Default",
            StorageAddress.From("MANAGED_AGGREGATE"));
        var source = new ManagedAggregate
        {
            Id = "aggregate-1",
            Tags = ["durable", "bounded"],
            Facts = [new ManagedFact("candidate", 3)],
            ActiveSince = new ManagedPartialDate(2024, 6, null),
            Carriers = new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "acme" }
        };

        var written = plan.Write(source);
        var hydrated = plan.Hydrate<ManagedAggregate>(written.Values);

        written.Values.Select(static value => value.Path.Name).Should().Equal("Id", "Json");
        plan.Use(MappingPath.Of(nameof(ManagedAggregate.Tags)), MappingConsumer.Filter)
            .Bindings.Should().ContainSingle()
            .Which.PhysicalPath.Should().Be(new PhysicalPath("Json", nameof(ManagedAggregate.Tags)));
        Action complexCollection = () => plan.Use(
            MappingPath.Of(nameof(ManagedAggregate.Facts)),
            MappingConsumer.Filter);
        complexCollection.Should().Throw<MappingValueException>().WithMessage("*physical binding*");
        plan.Use(
                MappingPath.Of(nameof(ManagedAggregate.ActiveSince), nameof(ManagedPartialDate.Year)),
                MappingConsumer.Filter)
            .Bindings.Should().ContainSingle()
            .Which.PhysicalPath.Should().Be(new PhysicalPath(
                "Json",
                nameof(ManagedAggregate.ActiveSince),
                nameof(ManagedPartialDate.Year)));
        hydrated.Id.Should().Be(source.Id);
        hydrated.Tags.Should().Equal(source.Tags);
        hydrated.Facts.Should().Equal(source.Facts);
        hydrated.ActiveSince.Should().Be(source.ActiveSince);
        hydrated.Carriers.Should().BeEquivalentTo(source.Carriers);
    }

    [Fact]
    public void Explicit_read_only_one_way_codec_is_legal_and_never_enters_a_write()
    {
        var codec = new DataMappingCodec<bool, string>(null, value => value == "Y", "legacy-read-only-flag");
        using var provider = Host(source => source.Map<ReadOnlyFlaggedCustomer>(map => map
            .Container("FLAGS")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.Enabled).Name("IS_ENABLED").ReadOnly().Codec(codec)));
        var plan = provider.GetRequiredService<IDataMappingPlans>().Require<ReadOnlyFlaggedCustomer>("Legacy");

        plan.Write(new ReadOnlyFlaggedCustomer { Id = 1, Enabled = true }).Values.Should().ContainSingle(value => value.Path.Name == "ID");
        var hydrated = plan.Hydrate<ReadOnlyFlaggedCustomer>(
        [
            new MappedValue(plan.Identity.Parts[0].Id, new PhysicalPath("ID"), MappingValueShape.Scalar, 1L),
            new MappedValue(plan.Bindings.Single(binding => binding.LogicalPath.Equals(MappingPath.Of("Enabled"))).Id,
                new PhysicalPath("IS_ENABLED"), MappingValueShape.Scalar, "Y")
        ]);
        hydrated.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Invalid_authority_path_and_codec_shapes_reject_at_compilation()
    {
        using var duplicate = Host(source => source.Map<FlatCustomer>(map => map
            .Container("DUP")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.NameFull).Name("NAME")
            .Property(customer => customer.NameFull).Name("OTHER")));
        Action duplicatePlan = () => duplicate.GetRequiredService<IDataMappingPlans>().Require<FlatCustomer>("Legacy");
        duplicatePlan.Should().Throw<MappingCompilationException>().WithMessage("*duplicate authority*");

        using var overlap = Host(source => source.Map<TwoNames>(map => map
            .Container("OVERLAP")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.First).Path("NAME", "full")
            .Property(customer => customer.Last).Path("NAME", "full", "last")));
        Action overlapPlan = () => overlap.GetRequiredService<IDataMappingPlans>().Require<TwoNames>("Legacy");
        overlapPlan.Should().Throw<MappingCompilationException>().WithMessage("*ambiguous*");

        var oneWay = new DataMappingCodec<bool, string>(null, value => value == "Y", "read-only-codec");
        using var asymmetric = Host(source => source.Map<FlaggedCustomer>(map => map
            .Container("FLAGS")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.Enabled).Name("IS_ENABLED").Codec(oneWay)));
        Action asymmetricPlan = () => asymmetric.GetRequiredService<IDataMappingPlans>().Require<FlaggedCustomer>("Legacy");
        asymmetricPlan.Should().Throw<MappingCompilationException>().WithMessage("*symmetric codec*");
    }

    [Fact]
    public void Plans_are_host_isolated_bounded_and_warm_reused()
    {
        using var first = Host(source => source.Map<FlatCustomer>(map => map
            .Container("FIRST")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.NameFull).Name("NAME")));
        using var second = Host(source => source.Map<FlatCustomer>(map => map
            .Container("SECOND")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.NameFull).Name("DISPLAY")));
        var firstPlans = first.GetRequiredService<IDataMappingPlans>();
        var secondPlans = second.GetRequiredService<IDataMappingPlans>();

        ReferenceEquals(firstPlans.Require<FlatCustomer>("Legacy"), firstPlans.Require<FlatCustomer>("Legacy")).Should().BeTrue();
        ReferenceEquals(firstPlans.Require<FlatCustomer>("Legacy"), secondPlans.Require<FlatCustomer>("Legacy")).Should().BeFalse();
        firstPlans.Require<FlatCustomer>("Legacy").Container.Name.Should().Be("FIRST");
        secondPlans.Require<FlatCustomer>("Legacy").Container.Name.Should().Be("SECOND");

        using var bounded = Host(
            source => source
                .Map<FlatCustomer>(map => map.Container("ONE").Key(value => value.Id).Name("ID"))
                .Map<TwoNames>(map => map.Container("TWO").Key(value => value.Id).Name("ID")),
            services => services.Configure<MappingOptions>(options => options.PlanEntries = 1));
        var boundedPlans = bounded.GetRequiredService<IDataMappingPlans>();
        boundedPlans.Require<FlatCustomer>("Legacy");
        Action overflow = () => boundedPlans.Require<TwoNames>("Legacy");
        overflow.Should().Throw<MappingCompilationException>().WithMessage("*bounded mapping-plan limit*");
    }

    [Fact]
    public void Relational_family_emits_complete_commands_from_the_mapping_plan()
    {
        using var provider = Host(source => source.Map<FlaggedCustomer>(map => map
            .Container("dbo", "FLAGS")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.Enabled).Name("IS_ENABLED")));
        var mapping = provider.GetRequiredService<IDataMappingPlans>().Require<FlaggedCustomer>("Legacy");
        var planner = new RelationalCommandPlanner(mapping);

        var insert = planner.Insert(new FlaggedCustomer { Id = 3, Enabled = true });
        var patch = planner.Patch(3L, new Dictionary<MappingPath, object?> { [MappingPath.Of("Enabled")] = false });
        var conditional = planner.ConditionalWrite(
            new FlaggedCustomer { Id = 3, Enabled = false }, MappingPath.Of("Enabled"), true);
        var query = planner.Query(QueryDefinition.All.Where(Filter.Eq("Enabled", true)));

        insert.Identity.Should().ContainSingle().Which.Binding.PhysicalPath.Name.Should().Be("ID");
        insert.Values.Should().ContainSingle().Which.Binding.PhysicalPath.Name.Should().Be("IS_ENABLED");
        patch.Values.Should().ContainSingle().Which.Value.Should().Be(false);
        conditional.Conditions.Should().ContainSingle().Which.Value.Should().Be(true);
        query.Filters.Should().ContainSingle().Which.BindingId.Should().Be(mapping.Use(MappingPath.Of("Enabled"), MappingConsumer.Filter).Bindings[0].Id);
        new[] { insert, patch, conditional, query }.Should().OnlyContain(command => command.Receipt.PlanId == mapping.Id);
    }

    [Fact]
    public async Task Definition_validation_is_exact_and_external_lifecycle_performs_zero_shape_mutation()
    {
        using var provider = Host(source => source.Map<FlaggedCustomer>(map => map
            .Container("dbo", "FLAGS")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.Enabled).Name("IS_ENABLED")));
        var mapping = provider.GetRequiredService<IDataMappingPlans>().Require<FlaggedCustomer>("Legacy");
        var orchestrator = new RelationalSchemaOrchestrator();
        var ddl = new ShapeDdl(tableExists: true);
        // The mapping keys on a long; the store keys on a string. Identity cannot drift on any matching mode.
        ddl.Columns["ID"] = new RelationalColumnState("ID", Nullable: false, ClrType: typeof(string));
        ddl.Columns["IS_ENABLED"] = new RelationalColumnState("IS_ENABLED", Nullable: true, ClrType: typeof(bool));
        ddl.Identity.Add("ID");
        var features = new ShapeFeatures();
        var external = new RelationalSchemaPolicy
        {
            StorageLifecycle = StorageLifecycle.External,
            Ddl = RelationalDdlPolicy.AutoCreate,
            Matching = RelationalSchemaMatchingMode.Strict
        };

        var validation = await orchestrator.ValidateAsync(mapping, ddl, features, external);
        validation.State.Should().Be("Unhealthy");
        validation.Corrective.Should().ContainSingle(finding => finding.Subject == "ID");
        Func<Task> ensure = () => orchestrator.EnsureCreatedAsync(mapping, ddl, features, external);
        await ensure.Should().ThrowAsync<InvalidOperationException>();
        ddl.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task Index_and_ttl_plans_use_mapped_paths_and_never_invent_an_unproved_native_claim()
    {
        using var provider = Host(_ => { });
        var mapping = provider.GetRequiredService<IDataMappingPlans>().GetOrAdd<ExpiringCustomer>(
            "Legacy",
            new MappingConvention(StorageAddress.From("dbo", "EXPIRING"), "Id", "Json"));
        var ttl = mapping.Indexes.Single(index => index.Ttl);
        var logical = mapping.Use(MappingPath.Of("ExpiresAt"), MappingConsumer.Filter).Bindings[0];
        var orchestrator = new RelationalSchemaOrchestrator();
        var features = new ShapeFeatures();
        var policy = new RelationalSchemaPolicy { Ddl = RelationalDdlPolicy.AutoCreate };
        var schema = orchestrator.Plan(mapping, features, policy);

        ReferenceEquals(ttl.Bindings[0], logical).Should().BeTrue();
        schema.Indexes.Single(index => index.Ttl).Parts.Select(static part => part.Path)
            .Should().Equal(new PhysicalPath("Json", "ExpiresAt"));
        schema.UnprovedClaims.Should().Contain($"TTL:{ttl.Name}");

        var ddl = new ShapeDdl(tableExists: true);
        foreach (var column in schema.Table.Columns)
            ddl.Columns[column.Name] = new RelationalColumnState(
                column.Name, !column.IsIdentity, column.IsGenerated, column.IsProjected, ClrType: column.ClrType);
        ddl.Identity.AddRange(schema.Table.Identity);
        var validation = await orchestrator.EnsureCreatedAsync(mapping, ddl, features, policy);
        validation.IsComplete.Should().BeTrue();
        ddl.Mutations.Should().Be(0, "unsupported TTL metadata must not become an ordinary index");
    }

    [Fact]
    public void Mutating_write_filter_or_index_physical_facts_fails_the_shared_plan_guard()
    {
        using var provider = Host(source => source.Map<FlaggedCustomer>(map => map
            .Container("FLAGS")
            .Key(customer => customer.Id).Name("ID")
            .Property(customer => customer.Enabled).Name("IS_ENABLED")));
        var mapping = provider.GetRequiredService<IDataMappingPlans>().Require<FlaggedCustomer>("Legacy");
        var planner = new RelationalCommandPlanner(mapping);
        var insert = planner.Insert(new FlaggedCustomer { Id = 1, Enabled = true });
        var query = planner.Query(QueryDefinition.All.Where(Filter.Eq("Enabled", true)));
        var features = new ShapeFeatures();
        var schema = new RelationalSchemaOrchestrator().Plan(mapping, features, new RelationalSchemaPolicy());

        var badWriteBinding = insert.Values[0].Binding with { PhysicalPath = new PhysicalPath("WRONG_WRITE") };
        var badWrite = Copy(insert, values: [new RelationalValue(badWriteBinding, insert.Values[0].Value)]);
        Action write = () => RelationalPlanGuard.Validate(mapping, badWrite);
        write.Should().Throw<MappingValueException>().WithMessage("*changed a compiled*");

        var badFilterBinding = query.Filters[0] with { EncodingId = "wrong-encoding" };
        var badFilter = Copy(query, filters: [badFilterBinding]);
        Action filter = () => RelationalPlanGuard.Validate(mapping, badFilter);
        filter.Should().Throw<MappingValueException>().WithMessage("*changed a compiled*");

        var mappedIndex = schema.Indexes.Single(index => !index.Primary);
        var badIndex = new RelationalIndexDefinition(
            mappedIndex.Name,
            [mappedIndex.Parts[0] with { Path = new PhysicalPath("WRONG_INDEX") }],
            mappedIndex.Unique,
            mappedIndex.Primary,
            mappedIndex.Ttl,
            mappedIndex.RewriteFree);
        var badSchema = new RelationalSchemaPlan(
            mapping,
            schema.Table,
            schema.Indexes.Select(index => index == mappedIndex ? badIndex : index),
            schema.UnprovedClaims,
            schema.RefusedRequirements);
        Action index = () => RelationalPlanGuard.Validate(mapping, badSchema);
        index.Should().Throw<MappingValueException>().WithMessage("*changed a compiled index*");
    }

    private static ServiceProvider Host(
        Action<DataSourceBuilder> configure,
        Action<IServiceCollection>? servicesConfigure = null)
    {
        var services = new ServiceCollection();
        services.AddKoan(koan => configure(koan.Data.Source("Legacy")));
        servicesConfigure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static RelationalCommandPlan Copy(
        RelationalCommandPlan plan,
        IEnumerable<RelationalValue>? values = null,
        IEnumerable<RelationalPathBinding>? filters = null) => new(
        plan.Operation,
        plan.Container,
        values ?? plan.Values,
        plan.Identity,
        plan.Conditions,
        plan.Reads,
        filters ?? plan.Filters,
        plan.Orders,
        plan.Query,
        plan.Receipt);

    public sealed class Customer
    {
        public long Id { get; set; }
        public CustomerName Name { get; set; } = new();
        public CustomerProfile Profile { get; set; } = new();
    }

    public sealed class CustomerName
    {
        public string Full { get; set; } = "";
        public string First { get; set; } = "";
    }

    public sealed class CustomerProfile
    {
        public string? PreferredLanguage { get; set; }
        public IReadOnlyList<string> Tags { get; set; } = [];
    }

    public sealed class FlatCustomer
    {
        public long Id { get; set; }
        public string NameFull { get; set; } = "";
    }

    public sealed class ComputedIdentity
    {
        public string Id { get; set; } = "";
        public string Serial => Id;
        public string DisplayName { get; set; } = "";
    }

    public sealed class TwoNames
    {
        public long Id { get; set; }
        public string First { get; set; } = "";
        public string Last { get; set; } = "";
    }

    public readonly record struct CustomerSiteId(long CustomerNo, short SiteNo);

    public sealed class CustomerSite
    {
        public CustomerSiteId Id { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public sealed class GeneratedCustomer
    {
        public long Id { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public sealed class FlaggedCustomer
    {
        public long Id { get; set; }
        [Index]
        public bool Enabled { get; set; }
    }

    public sealed class ReadOnlyFlaggedCustomer
    {
        public long Id { get; set; }
        public bool Enabled { get; set; }
    }

    public sealed class ExpiringCustomer
    {
        public long Id { get; set; }
        [Index(Ttl = true)]
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public sealed class ManagedAggregate
    {
        public string Id { get; set; } = "";
        public string[] Tags { get; set; } = [];
        public ManagedFact[] Facts { get; set; } = [];
        public ManagedPartialDate? ActiveSince { get; set; }
        public Dictionary<string, string> Carriers { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed record ManagedFact(string Name, int Count);
    public sealed record ManagedPartialDate(int? Year, int? Month, int? Day);

    private sealed class SpyDialect : IRelationalMappingDialect
    {
        public string Read(PhysicalPath path, MappingValueShape shape, Type physicalType) => $"read({path})";
        public string QuoteIdent(string ident) => $"\"{ident}\"";
        public string EscapeLike(string fragment) => fragment;
        public string Parameter(int index) => $"@p{index}";
        public string JsonArrayContains(string columnSql, string parameter) => $"contains({columnSql},{parameter})";
        public string JsonArrayLength(string columnSql) => $"length({columnSql})";
        public string JsonArrayElementLike(string columnSql, string patternParameter, string literalParameter) => $"elementlike({columnSql},{patternParameter},{literalParameter})";
    }

    private sealed class ShapeFeatures : IRelationalStoreFeatures
    {
        public bool SupportsJsonFunctions => true;
        public bool SupportsPersistedComputedColumns => true;
        public string ProviderName => "shape-spy";
        public bool SupportsMappedIndexes => true;
        public bool SupportsRewriteFreeExpressionIndexes => true;
    }

    private sealed class ShapeDdl(bool tableExists) : IRelationalDdlExecutor
    {
        public Dictionary<string, RelationalColumnState?> Columns { get; } = new(StringComparer.Ordinal);
        public List<string> Identity { get; } = [];
        public int Mutations { get; private set; }

        public Task<RelationalTableShape?> Describe(RelationalTableDefinition table, CancellationToken ct = default)
            => Task.FromResult(tableExists ? new RelationalTableShape(Columns, Identity) : null);

        public Task Create(RelationalTableDefinition table, CancellationToken ct = default) => Mutated();

        public Task AddColumn(RelationalTableDefinition table, RelationalColumnDefinition column, CancellationToken ct = default)
            => Mutated();

        public Task CreateIndex(RelationalTableDefinition table, RelationalIndexDefinition index, CancellationToken ct = default)
            => Mutated();

        private Task Mutated()
        {
            Mutations++;
            return Task.CompletedTask;
        }
    }
}
