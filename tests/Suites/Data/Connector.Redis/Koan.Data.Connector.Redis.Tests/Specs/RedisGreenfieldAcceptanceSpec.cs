using Koan.Core;
using Koan.Data.Abstractions.Sources;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;
using KoanData = Koan.Data.Core.Data;

namespace Koan.Data.Connector.Redis.Tests.Specs;

public sealed class RedisGreenfieldAcceptanceSpec(RedisFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<RedisFixture>(fixture, output)
{
    [Fact]
    public async Task External_map_is_keyed_only_preserves_unknown_json_and_read_only_fails_fast()
    {
        RequireBackingStore();
        const string source = "LegacyRedis";
        const string container = "legacy_customers";
        var id = Guid.CreateVersion7().ToString("N");

        await using (var host = await StartMapped(source, container, DataSourceAccess.ReadWrite))
        using (EntityContext.Source(source))
        {
            await new LegacyCustomer
            {
                Id = id,
                DisplayName = "Ada Lovelace",
                Profile = new CustomerProfile { Language = "en" }
            }.Save();

            var db = host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase(Fixture.Database);
            var key = FindKey(host.Services.GetRequiredService<IConnectionMultiplexer>(), id);
            var stored = JObject.Parse((await db.StringGetAsync(key))!);
            stored["LEGACY_ONLY"] = "preserve-me";
            await db.StringSetAsync(key, stored.ToString(Newtonsoft.Json.Formatting.None));

            var customer = await LegacyCustomer.Get(id);
            customer!.DisplayName = "Augusta Ada King";
            customer.Profile.Language = "fr";
            await customer.Save();

            stored = JObject.Parse((await db.StringGetAsync(key))!);
            stored["DISPLAY_NM"]!.Value<string>().Should().Be("Augusta Ada King");
            stored["PROFILE"]!["Language"]!.Value<string>().Should().Be("fr");
            stored["LEGACY_ONLY"]!.Value<string>().Should().Be("preserve-me");

            await FluentActions.Invoking(() => LegacyCustomer.All())
                .Should().ThrowAsync<Exception>()
                .WithMessage("*External*known-key*Function*");
        }

        await using (var host = await StartMapped(source, container, DataSourceAccess.ReadOnly))
        using (EntityContext.Source(source))
        {
            var customer = await LegacyCustomer.Get(id);
            customer!.DisplayName = "must not persist";
            await FluentActions.Invoking(() => customer.Save())
                .Should().ThrowAsync<DataSourcePolicyException>();
        }
    }

    [Fact]
    public async Task Managed_query_bound_is_a_correctness_boundary()
    {
        RequireBackingStore();
        var settings = new Dictionary<string, string?>(Fixture.SettingsForBoot(), StringComparer.Ordinal)
        {
            ["Koan:Data:Redis:MaxQueryEntries"] = "2"
        };
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(settings)
            .ConfigureServices(services => services.AddKoan())
            .StartAsync(TestContext.Current.CancellationToken);
        using var lease = Lease(NewPartition("bound"));
        await new BoundedItem { Name = "one" }.Save();
        await new BoundedItem { Name = "two" }.Save();
        await new BoundedItem { Name = "three" }.Save();

        await FluentActions.Invoking(() => BoundedItem.All())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*3*MaxQueryEntries=2*");
    }

    [Fact]
    public async Task Conditional_replace_is_native_compare_and_set()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        using var lease = Lease(NewPartition("cas"));
        var saved = await new RedisJob { State = "queued", Owner = "" }.Save();
        var repository = host.Services.GetRequiredService<IDataService>()
            .GetRepository<RedisJob, string>()
            .Should().BeAssignableTo<IConditionalWriteRepository<RedisJob, string>>().Which;

        (await repository.ConditionalReplaceAsync(
            new RedisJob { Id = saved.Id, State = "running", Owner = "node-1" },
            Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<RedisJob>(job => job.State == "queued"))).Should().BeTrue();
        (await repository.ConditionalReplaceAsync(
            new RedisJob { Id = saved.Id, State = "running", Owner = "node-2" },
            Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<RedisJob>(job => job.State == "queued"))).Should().BeFalse();
        (await RedisJob.Get(saved.Id))!.Owner.Should().Be("node-1");
    }

    [Fact]
    public async Task Registered_read_only_functions_return_neutral_records_and_scalars()
    {
        RequireBackingStore();
        var suffix = Guid.NewGuid().ToString("N");
        var library = "koan_" + suffix;
        var recordsFunction = "records_" + suffix;
        var scalarFunction = "scalar_" + suffix;
        var code = $"#!lua name={library}\n" +
                   $"redis.register_function{{function_name='{recordsFunction}', callback=function(keys,args) return {{cjson.encode({{Id='1',Title=args[1]}})}} end, flags={{'no-writes'}}}}\n" +
                   $"redis.register_function{{function_name='{scalarFunction}', callback=function(keys,args) return 2 end, flags={{'no-writes'}}}}";
        using var direct = await ConnectionMultiplexer.ConnectAsync(Fixture.ConnectionString);
        await direct.GetDatabase(Fixture.Database).ExecuteAsync("FUNCTION", "LOAD", "REPLACE", code);
        try
        {
            const string source = "FunctionSource";
            var settings = SourceSettings(source, DataSourceAccess.ReadOnly);
            settings[$"Koan:Data:Sources:{source}:ReadLanes:Reports:ConnectionString"] = Fixture.ConnectionString;
            await using var host = await KoanIntegrationHost.Configure()
                .WithSettings(settings)
                .ConfigureServices(services => services.AddKoan(koan =>
                {
                    koan.Data.Source(source).Query("work.ready", query => query
                        .Lane("Reports")
                        .Function(recordsFunction)
                        .Parameter<string>("title"));
                    koan.Data.Source(source).Scalar<long>("work.count", query => query
                        .Lane("Reports")
                        .Function(scalarFunction));
                }))
                .StartAsync(TestContext.Current.CancellationToken);

            var runtime = KoanData.Source(source);
            var records = await runtime.Query("work.ready", new { title = "ship" });
            records.Project<FunctionRow>().Should().ContainSingle()
                .Which.Should().Be(new FunctionRow("1", "ship"));
            (await runtime.Scalar<long>("work.count")).Should().Be(2);
            await FluentActions.Invoking(() => runtime.Inspect().Containers(10, null))
                .Should().ThrowAsync<SourceIntegrationException>()
                .WithMessage("*does not support list containers*");
        }
        finally
        {
            await direct.GetDatabase(Fixture.Database).ExecuteAsync("FUNCTION", "DELETE", library);
        }
    }

    private async Task<IntegrationHost> StartMapped(string source, string container, DataSourceAccess access) =>
        await KoanIntegrationHost.Configure()
            .WithSettings(SourceSettings(source, access))
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source(source).Map<LegacyCustomer>(map => map
                    .Container(container)
                    .Key(customer => customer.Id).Name("CUSTOMER_NO")
                    .Property(customer => customer.DisplayName).Name("DISPLAY_NM")
                    .Property(customer => customer.Profile).Object("PROFILE"))))
            .StartAsync(TestContext.Current.CancellationToken);

    private Dictionary<string, string?> SourceSettings(string source, DataSourceAccess access) =>
        new(Fixture.SettingsForBoot(), StringComparer.Ordinal)
        {
            [$"Koan:Data:Sources:{source}:Adapter"] = "redis",
            [$"Koan:Data:Sources:{source}:ConnectionString"] = Fixture.ConnectionString,
            [$"Koan:Data:Sources:{source}:redis:Database"] = Fixture.Database.ToString(),
            [$"Koan:Data:Sources:{source}:StorageLifecycle"] = StorageLifecycle.External.ToString(),
            [$"Koan:Data:Sources:{source}:Access"] = access.ToString()
        };

    private RedisKey FindKey(IConnectionMultiplexer connection, string id) =>
        connection.GetServers().First(server => server.IsConnected)
            .Keys(Fixture.Database, pattern: $"koan:*:record:{id}").Single();

    private sealed class LegacyCustomer : Entity<LegacyCustomer>
    {
        public string DisplayName { get; set; } = "";
        public CustomerProfile Profile { get; set; } = new();
    }

    private sealed class CustomerProfile
    {
        public string Language { get; set; } = "";
    }

    private sealed class BoundedItem : Entity<BoundedItem>
    {
        public string Name { get; set; } = "";
    }

    private sealed class RedisJob : Entity<RedisJob>
    {
        public string State { get; set; } = "";
        public string Owner { get; set; } = "";
    }

    private sealed record FunctionRow(string Id, string Title);
}
