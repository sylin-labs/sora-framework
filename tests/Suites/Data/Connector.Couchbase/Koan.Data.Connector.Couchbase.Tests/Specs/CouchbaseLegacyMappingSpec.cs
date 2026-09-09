using Couchbase;
using Couchbase.Core.IO.Authentication.Authenticators;
using Couchbase.KeyValue;
using Couchbase.Management.Collections;
using Couchbase.Query;
using Koan.Core;
using Koan.Data.Abstractions.Sources;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Koan.Data.Connector.Couchbase.Tests.Specs;

public sealed class CouchbaseLegacyMappingSpec(CouchbaseFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<CouchbaseFixture>(fixture, output)
{
    [Fact]
    public async Task Compact_map_reads_queries_conditionally_updates_and_preserves_external_fields()
    {
        RequireBackingStore();
        var scope = Name("legacy");
        const string collection = "customers";
        await Seed(scope, collection, "7", new JObject
        {
            ["CUSTOMER_NO"] = 7L,
            ["DISPLAY_NM"] = "Ada Lovelace",
            ["PROFILE"] = new JObject { ["Language"] = "en" },
            ["LEGACY_ONLY"] = "preserve-me"
        }, queryable: true);

        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("Legacy", StorageLifecycle.External, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("Legacy").Map<LegacyCustomer>(map => map
                    .Container(StorageAddress.From(scope, collection))
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.DisplayName).Name("DISPLAY_NM")
                    .Property(customer => customer.Profile).Object("PROFILE"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("Legacy"))
        {
            var customer = await LegacyCustomer.Get(7);
            customer!.DisplayName.Should().Be("Ada Lovelace");
            customer.Profile.Language.Should().Be("en");
            (await LegacyCustomer.Query(item => item.DisplayName == "Ada Lovelace"))
                .Should().ContainSingle();

            var conditional = host.Services.GetRequiredService<IDataService>()
                .GetRepository<LegacyCustomer, long>()
                .Should().BeAssignableTo<IConditionalWriteRepository<LegacyCustomer, long>>().Which;
            customer.DisplayName = "Ada, conditionally claimed";
            (await conditional.ConditionalReplaceAsync(customer, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<LegacyCustomer>(item => item.DisplayName == "Ada Lovelace")))
                .Should().BeTrue();

            customer.DisplayName = "Augusta Ada King";
            customer.Profile.Language = "fr";
            await customer.Save();
        }

        var stored = await Read(scope, collection, "7");
        stored["DISPLAY_NM"]!.Value<string>().Should().Be("Augusta Ada King");
        stored["PROFILE"]!["Language"]!.Value<string>().Should().Be("fr");
        stored["LEGACY_ONLY"]!.Value<string>().Should().Be("preserve-me");
    }

    [Fact]
    public async Task Nested_paths_and_composite_keys_preserve_values_outside_the_map()
    {
        RequireBackingStore();
        var scope = Name("paths");
        await Seed(scope, "customers", "9", new JObject
        {
            ["CUSTOMER_NO"] = 9L,
            ["NAME_DATA"] = new JObject
            {
                ["full"] = "Grace Hopper",
                ["first"] = "Grace",
                ["legacy"] = "preserve-me"
            }
        });
        var siteId = new CustomerSiteId(42, 3);
        await Seed(scope, "sites", Newtonsoft.Json.JsonConvert.SerializeObject(siteId), new JObject
        {
            ["CUSTOMER_NO"] = 42L,
            ["SITE_NO"] = 3,
            ["DISPLAY_NM"] = "Primary"
        });

        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("Legacy", StorageLifecycle.External, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
            {
                koan.Data.Source("Legacy").Map<PathCustomer>(map => map
                    .Container(StorageAddress.From(scope, "customers"))
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.Full).Path("NAME_DATA", "full")
                    .Property(customer => customer.First).Path("NAME_DATA", "first"));
                koan.Data.Source("Legacy").Map<MappedSite>(map => map
                    .Container(StorageAddress.From(scope, "sites"))
                    .Key(site => site.Id).Parts(parts => parts
                        .Property(key => key.CustomerNo).Name("CUSTOMER_NO")
                        .Property(key => key.SiteNo).Name("SITE_NO"))
                    .Property(site => site.DisplayName).Name("DISPLAY_NM"));
            }))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("Legacy"))
        {
            var customer = await PathCustomer.Get(9);
            customer!.Full.Should().Be("Grace Hopper");
            customer.Full = "Rear Admiral Grace Hopper";
            await customer.Save();

            var site = await MappedSite.Get(siteId);
            site!.DisplayName.Should().Be("Primary");
        }

        var stored = await Read(scope, "customers", "9");
        stored["NAME_DATA"]!["full"]!.Value<string>().Should().Be("Rear Admiral Grace Hopper");
        stored["NAME_DATA"]!["first"]!.Value<string>().Should().Be("Grace");
        stored["NAME_DATA"]!["legacy"]!.Value<string>().Should().Be("preserve-me");
    }

    [Fact]
    public async Task Read_only_external_map_rejects_before_mutation()
    {
        RequireBackingStore();
        var scope = Name("readonly");
        await Seed(scope, "customers", "11", new JObject
        {
            ["CUSTOMER_NO"] = 11L,
            ["DISPLAY_NM"] = "Source owned"
        });

        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("ReadOnlyLegacy", StorageLifecycle.External, DataSourceAccess.ReadOnly))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("ReadOnlyLegacy").Map<ReadOnlyCustomer>(map => map
                    .Container(StorageAddress.From(scope, "customers"))
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.DisplayName).Name("DISPLAY_NM"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("ReadOnlyLegacy"))
        {
            var customer = await ReadOnlyCustomer.Get(11);
            customer!.DisplayName = "must not persist";
            await FluentActions.Invoking(() => customer.Save())
                .Should().ThrowAsync<DataSourcePolicyException>();
        }

        (await Read(scope, "customers", "11"))["DISPLAY_NM"]!.Value<string>()
            .Should().Be("Source owned");
    }

    [Fact]
    public async Task Managed_maps_support_object_and_composite_shapes_but_reject_provider_generated_keys()
    {
        RequireBackingStore();
        var scope = Name("managed");
        var settings = Settings("Mapped", StorageLifecycle.Managed, DataSourceAccess.ReadWrite);
        settings["Koan:Data:Couchbase:Durability"] = "Majority";
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(settings)
            .ConfigureServices(services => services.AddKoan(koan =>
            {
                koan.Data.Source("Mapped").Map<MappedEnvelope>(map => map
                    .Container(StorageAddress.From(scope, "envelopes"))
                    .Key(item => item.Id).Name("ID")
                    .Object("DATA"));
                koan.Data.Source("Mapped").Map<MappedSite>(map => map
                    .Container(StorageAddress.From(scope, "sites"))
                    .Key(item => item.Id).Parts(parts => parts
                        .Property(key => key.CustomerNo).Name("CUSTOMER_NO")
                        .Property(key => key.SiteNo).Name("SITE_NO"))
                    .Property(item => item.DisplayName).Name("DISPLAY_NM"));
                koan.Data.Source("Mapped").Map<GeneratedCustomer>(map => map
                    .Container(StorageAddress.From(scope, "generated"))
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

            await FluentActions.Invoking(() => new GeneratedCustomer { DisplayName = "unsupported" }.Save())
                .Should().ThrowAsync<MappingCompilationException>()
                .WithMessage("*application-assigned*");
        }
    }

    [Fact]
    public async Task External_mapping_never_creates_a_missing_container()
    {
        RequireBackingStore();
        var scope = Name("missing");
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Settings("External", StorageLifecycle.External, DataSourceAccess.ReadWrite))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("External").Map<ExternalMissing>(map => map
                    .Container(StorageAddress.From(scope, "records"))
                    .Key(item => item.Id).Name("ID")
                    .Property(item => item.Name).Name("NAME"))))
            .StartAsync(TestContext.Current.CancellationToken);

        using (EntityContext.Source("External"))
            await FluentActions.Invoking(() => ExternalMissing.Get(1))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not exist*");

        using var cluster = await Connect();
        var bucket = await cluster.BucketAsync(Fixture.Bucket);
        var scopes = await bucket.Collections.GetAllScopesAsync();
        scopes.Should().NotContain(item => item.Name == scope);
    }

    private Dictionary<string, string?> Settings(
        string source,
        StorageLifecycle lifecycle,
        DataSourceAccess access) => new(Fixture.SettingsForBoot(), StringComparer.Ordinal)
    {
        [$"Koan:Data:Sources:{source}:Adapter"] = "couchbase",
        [$"Koan:Data:Sources:{source}:ConnectionString"] = Fixture.ConnectionString,
        [$"Koan:Data:Sources:{source}:couchbase:Bucket"] = Fixture.Bucket,
        [$"Koan:Data:Sources:{source}:couchbase:Username"] = Fixture.AdminUser,
        [$"Koan:Data:Sources:{source}:couchbase:Password"] = Fixture.AdminPassword,
        [$"Koan:Data:Sources:{source}:StorageLifecycle"] = lifecycle.ToString(),
        [$"Koan:Data:Sources:{source}:Access"] = access.ToString()
    };

    private async Task Seed(string scopeName, string collectionName, string key, JObject document, bool queryable = false)
    {
        using var cluster = await Connect();
        var bucket = await cluster.BucketAsync(Fixture.Bucket);
        try { await bucket.Collections.CreateScopeAsync(scopeName); }
        catch (ScopeExistsException) { }
        try { await bucket.Collections.CreateCollectionAsync(scopeName, collectionName, new CreateCollectionSettings()); }
        catch (CollectionExistsException) { }
        var scope = await bucket.ScopeAsync(scopeName);
        var collection = await scope.CollectionAsync(collectionName);
        await collection.UpsertAsync(key, document);
        if (!queryable) return;
        await CreatePrimaryIndex(cluster, scopeName, collectionName);
    }

    private async Task<JObject> Read(string scopeName, string collectionName, string key)
    {
        using var cluster = await Connect();
        var bucket = await cluster.BucketAsync(Fixture.Bucket);
        var scope = await bucket.ScopeAsync(scopeName);
        var collection = await scope.CollectionAsync(collectionName);
        using var result = await collection.GetAsync(key);
        return result.ContentAs<JObject>()
            ?? throw new InvalidDataException("Couchbase returned an empty test document.");
    }

    private async Task<ICluster> Connect()
    {
        var options = new ClusterOptions { ConnectionString = Fixture.ConnectionString }
            .WithAuthenticator(new PasswordAuthenticator(Fixture.AdminUser, Fixture.AdminPassword));
        return await Cluster.ConnectAsync(options);
    }

    private async Task CreatePrimaryIndex(ICluster cluster, string scope, string collection)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await cluster.QueryAsync<dynamic>(
                    $"CREATE PRIMARY INDEX IF NOT EXISTS ON {Qualified(scope, collection)} USING GSI",
                    new QueryOptions().Readonly(false).Timeout(TimeSpan.FromSeconds(10)));
                await foreach (var _ in result.Rows) { }
                return;
            }
            catch (ServiceNotAvailableException) when (attempt < 59)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private string Qualified(string scope, string collection) =>
        $"`{Fixture.Bucket}`.`{scope}`.`{collection}`";

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

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

    public readonly record struct CustomerSiteId(long CustomerNo, short SiteNo);

    public sealed class MappedSite : Entity<MappedSite, CustomerSiteId>
    {
        public string DisplayName { get; set; } = "";
    }

    public sealed class ReadOnlyCustomer : Entity<ReadOnlyCustomer, long>
    {
        public string DisplayName { get; set; } = "";
    }

    public sealed class MappedEnvelope : Entity<MappedEnvelope>
    {
        public string Value { get; set; } = "";
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
