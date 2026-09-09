using Koan.Core;
using Koan.Data.Abstractions.Sources;
using Koan.Testing.Integration;
using Koan.Data.Relational.Orchestration;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Connector.Postgres.Tests.Specs;

public sealed class PostgresLegacyMappingSpec(PostgresFixture fixture)
{
    [Fact]
    public async Task Compact_map_reads_queries_and_updates_an_external_table()
    {
        await SeedCustomer();
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("Legacy", StorageLifecycle.External, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("Legacy").Map<LegacyCustomer>(map => map
                    .Container("CUSTOMER")
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.DisplayName).Name("DISPLAY_NM")
                    .Property(customer => customer.Profile).Object("PROFILE_JSON"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("Legacy"))
        {
            var customer = await LegacyCustomer.Get(7);
            customer!.DisplayName.Should().Be("Ada Lovelace");
            customer.Profile.Language.Should().Be("en");
            (await LegacyCustomer.Query(item => item.DisplayName == "Ada Lovelace"))
                .Should().ContainSingle();
            (await Data<LegacyCustomer, long>.QueryRaw(
                "\"DISPLAY_NM\" = @name",
                new { name = "Ada Lovelace" })).Should().ContainSingle();

            var repository = host.Services.GetRequiredService<IDataService>()
                .GetRepository<LegacyCustomer, long>();
            var conditional = repository.Should()
                .BeAssignableTo<IConditionalWriteRepository<LegacyCustomer, long>>().Which;
            customer.DisplayName = "Ada, conditionally claimed";
            (await conditional.ConditionalReplaceAsync(customer, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<LegacyCustomer>(item => item.DisplayName == "Ada Lovelace")))
                .Should().BeTrue();
            customer.DisplayName = "Augusta Ada King";
            customer.Profile.Language = "fr";
            await customer.Save();
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"DISPLAY_NM\", \"PROFILE_JSON\"::text FROM \"CUSTOMER\" WHERE \"CUSTOMER_NO\" = 7";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Augusta Ada King");
        reader.GetString(1).Should().Contain("\"Language\": \"fr\"");
    }

    /// <summary>
    /// A declared map pins one physical container, so an ambient partition — which asks for a different one —
    /// cannot be honoured. Until 2026-08-21 this store answered by serving the pinned container, so a caller who
    /// asked for a partition got unpartitioned data and no indication of it. SQLite and Redis had always refused.
    /// </summary>
    [Fact]
    public async Task An_explicit_map_refuses_an_ambient_partition()
    {
        await SeedCustomer();
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("Legacy", StorageLifecycle.External, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("Legacy").Map<LegacyCustomer>(map => map
                    .Container("CUSTOMER")
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.DisplayName).Name("DISPLAY_NM")
                    .Property(customer => customer.Profile).Object("PROFILE_JSON"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("Legacy"))
        {
            (await LegacyCustomer.Get(7))!.DisplayName.Should().Be("Ada Lovelace");

            using (EntityContext.Partition("must-not-alias"))
                (await FluentActions.Invoking(() => LegacyCustomer.Get(7))
                    .Should().ThrowAsync<NotSupportedException>())
                    .Which.Message.Should().Contain("ambient partition");
        }
    }

    [Fact]
    public async Task Nested_paths_preserve_unmapped_external_values()
    {
        await Execute("""
            DROP TABLE IF EXISTS "CUSTOMER_PATHS";
            CREATE TABLE "CUSTOMER_PATHS" (
                "CUSTOMER_NO" bigint PRIMARY KEY,
                "NAME_DATA" jsonb NOT NULL
            );
            INSERT INTO "CUSTOMER_PATHS" VALUES
                (9, '{"full":"Grace Hopper","first":"Grace","legacy":"preserve-me"}');
            """);
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("Legacy", StorageLifecycle.External, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("Legacy").Map<PathCustomer>(map => map
                    .Container("CUSTOMER_PATHS")
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.Full).Path("NAME_DATA", "full")
                    .Property(customer => customer.First).Path("NAME_DATA", "first"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("Legacy"))
        {
            var customer = await PathCustomer.Get(9);
            customer!.Full = "Rear Admiral Grace Hopper";
            await customer.Save();
        }

        var json = (string)(await Scalar("SELECT \"NAME_DATA\"::text FROM \"CUSTOMER_PATHS\" WHERE \"CUSTOMER_NO\" = 9"))!;
        json.Should().Contain("Rear Admiral Grace Hopper");
        json.Should().Contain("preserve-me");
    }

    [Fact]
    public async Task Read_only_external_map_rejects_before_mutation()
    {
        await SeedCustomer();
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("Legacy", StorageLifecycle.External, DataSourceAccess.ReadOnly))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("Legacy").Map<ReadOnlyCustomer>(map => map
                    .Container("CUSTOMER")
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.DisplayName).Name("DISPLAY_NM"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("Legacy"))
        {
            var customer = await ReadOnlyCustomer.Get(7);
            customer!.DisplayName = "must not persist";
            await FluentActions.Invoking(async () => await customer.Save())
                .Should().ThrowAsync<DataSourcePolicyException>();
        }
        (await Scalar("SELECT \"DISPLAY_NM\" FROM \"CUSTOMER\" WHERE \"CUSTOMER_NO\" = 7"))
            .Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task Managed_maps_support_object_composite_and_generated_shapes()
    {
        await Execute("""
            DROP TABLE IF EXISTS "MAPPED_ENVELOPES";
            DROP TABLE IF EXISTS "MAPPED_SITES";
            DROP TABLE IF EXISTS "GENERATED_CUSTOMERS";
            """);
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("Mapped", StorageLifecycle.Managed, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
            {
                koan.Data.Source("Mapped").Map<MappedEnvelope>(map => map
                    .Container("MAPPED_ENVELOPES")
                    .Key(item => item.Id).Name("ID")
                    .Object("DATA"));
                koan.Data.Source("Mapped").Map<MappedSite>(map => map
                    .Container("MAPPED_SITES")
                    .Key(item => item.Id).Parts(parts => parts
                        .Property(key => key.CustomerNo).Name("CUSTOMER_NO")
                        .Property(key => key.SiteNo).Name("SITE_NO"))
                    .Property(item => item.DisplayName).Name("DISPLAY_NM"));
                koan.Data.Source("Mapped").Map<GeneratedCustomer>(map => map
                    .Container("GENERATED_CUSTOMERS")
                    .Key(item => item.Id).Name("ID").Generated()
                    .Property(item => item.DisplayName).Name("DISPLAY_NM"));
            }))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("Mapped"))
        {
            var envelope = await new MappedEnvelope { Value = "whole object" }.Save();
            (await MappedEnvelope.Get(envelope.Id))!.Value.Should().Be("whole object");

            var site = await new MappedSite
            {
                Id = new CustomerSiteId(42, 3),
                DisplayName = "Primary"
            }.Save();
            (await MappedSite.Get(site.Id))!.DisplayName.Should().Be("Primary");

            var generated = await new GeneratedCustomer { DisplayName = "Generated" }.Save();
            generated.Id.Should().BeGreaterThan(0);
            generated.DisplayName = "Updated";
            await generated.Save();
            (await GeneratedCustomer.Get(generated.Id))!.DisplayName.Should().Be("Updated");
        }
    }

    [Fact]
    public async Task External_mapping_never_creates_a_missing_table()
    {
        await Execute("DROP TABLE IF EXISTS \"EXTERNAL_MISSING\"");
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("External", StorageLifecycle.External, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("External").Map<ExternalMissing>(map => map
                    .Container("EXTERNAL_MISSING")
                    .Key(item => item.Id).Name("ID")
                    .Property(item => item.Name).Name("NAME"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("External"))
            await FluentActions.Invoking(() => ExternalMissing.Get(1))
                .Should().ThrowAsync<SchemaMismatchException>();

        (await Scalar("SELECT to_regclass('public.\"EXTERNAL_MISSING\"')::text"))
            .Should().Be(DBNull.Value);
    }

    private async Task SeedCustomer() => await Execute("""
        DROP TABLE IF EXISTS "CUSTOMER";
        CREATE TABLE "CUSTOMER" (
            "CUSTOMER_NO" bigint PRIMARY KEY,
            "DISPLAY_NM" text NOT NULL,
            "PROFILE_JSON" jsonb NOT NULL
        );
        INSERT INTO "CUSTOMER" VALUES (7, 'Ada Lovelace', '{"Language":"en"}');
        """);

    private Dictionary<string, string?> Settings(
        string source,
        StorageLifecycle lifecycle,
        DataSourceAccess access) => new(fixture.SettingsForBoot(), StringComparer.Ordinal)
    {
        [$"Koan:Data:Sources:{source}:Adapter"] = "postgres",
        [$"Koan:Data:Sources:{source}:ConnectionString"] = fixture.ConnectionString,
        [$"Koan:Data:Sources:{source}:StorageLifecycle"] = lifecycle.ToString(),
        [$"Koan:Data:Sources:{source}:Access"] = access.ToString()
    };

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public sealed class LegacyCustomer : Entity<LegacyCustomer, long>
    {
        public string DisplayName { get; set; } = "";
        public Profile Profile { get; set; } = new();
    }

    public sealed class Profile { public string Language { get; set; } = ""; }
    public sealed class PathCustomer : Entity<PathCustomer, long>
    {
        public string Full { get; set; } = "";
        public string First { get; set; } = "";
    }
    public sealed class ReadOnlyCustomer : Entity<ReadOnlyCustomer, long>
    {
        public string DisplayName { get; set; } = "";
    }
    public sealed class MappedEnvelope : Entity<MappedEnvelope>
    {
        public string Value { get; set; } = "";
    }
    public readonly record struct CustomerSiteId(long CustomerNo, short SiteNo);
    public sealed class MappedSite : Entity<MappedSite, CustomerSiteId>
    {
        public string DisplayName { get; set; } = "";
    }
    public sealed class GeneratedCustomer : Entity<GeneratedCustomer, long>
    {
        public string DisplayName { get; set; } = "";
    }
    public sealed class ExternalMissing : Entity<ExternalMissing, long>
    {
        public string Name { get; set; } = "";
    }
}
