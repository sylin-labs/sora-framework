using Koan.Core;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Relational.Orchestration;
using Koan.Testing.Integration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Connector.Sqlite.Tests.Specs;

public sealed class SqliteConditionalPublicationSpec(SqliteFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<SqliteFixture>(fixture, output)
{
    [Fact]
    public async Task Match_identical_conflict_and_missing_preserve_native_row_contract()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var saved = await new Publication { Revision = "old", Body = "original" }.Save();
        var next = new Publication { Id = saved.Id, Revision = "new", Body = "published" };
        (await next.ReplaceIf(row => row.Revision == "old")).Should().BeTrue();
        (await next.ReplaceIf(row => row.Revision == "new")).Should().BeTrue();
        (await new Publication { Id = saved.Id, Revision = "stale", Body = "lost" }
            .ReplaceIf(row => row.Revision == "old")).Should().BeFalse();
        var missing = new Publication { Id = Guid.NewGuid().ToString("N"), Revision = "new" };
        (await missing.ReplaceIf(row => true)).Should().BeFalse();
        (await Publication.Get(missing.Id)).Should().BeNull();
        (await Publication.Get(saved.Id))!.Body.Should().Be("published");
    }

    [Fact]
    public async Task Concurrent_expected_revision_has_one_winner()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var saved = await new Publication { Revision = "old" }.Save();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await new Publication { Id = saved.Id, Revision = "winner-" + index }
                .ReplaceIf(row => row.Revision == "old");
        })).ToArray();
        start.SetResult();
        (await Task.WhenAll(attempts)).Count(result => result).Should().Be(1);
        (await Publication.Get(saved.Id))!.Revision.Should().StartWith("winner-");
    }

    [Fact]
    public async Task Explicit_default_named_and_inherited_partitions_restore_the_caller()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var id = Guid.NewGuid().ToString("N");
        using (EntityContext.Partition("")) await new Publication { Id = id, Revision = "default" }.Save();
        await new Publication { Id = id, Revision = "named" }.Save("ae11-named");
        await new Publication { Id = id, Revision = "ambient" }.Save("ae11-ambient");
        using var ambient = EntityContext.Partition("ae11-ambient");
        (await new Publication { Id = id, Revision = "default-new" }
            .ReplaceIf(row => row.Revision == "default", partition: "")).Should().BeTrue();
        (await new Publication { Id = id, Revision = "named-new" }
            .ReplaceIf(row => row.Revision == "named", partition: "ae11-named")).Should().BeTrue();
        (await Publication.Get(id))!.Revision.Should().Be("ambient");
        (await new Publication { Id = id, Revision = "ambient-new" }
            .ReplaceIf(row => row.Revision == "ambient")).Should().BeTrue();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await FluentActions.Invoking(() => new Publication { Id = id, Revision = "canceled" }
            .ReplaceIf(row => true, partition: "", ct: canceled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        (await Publication.Get(id))!.Revision.Should().Be("ambient-new");
        (await Publication.Get(id, ""))!.Revision.Should().Be("default-new");
        (await Publication.Get(id, "ae11-named"))!.Revision.Should().Be("named-new");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mapped_scalar_identity_requires_a_physical_primary_key(bool primaryKey)
    {
        RequireBackingStore();
        var table = primaryKey ? "AE11_PUBLICATION_PK" : "AE11_PUBLICATION_DUPLICATE";
        await using (var connection = new SqliteConnection(Fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var setup = connection.CreateCommand();
            setup.CommandText = $"CREATE TABLE IF NOT EXISTS {table} (ID INTEGER NOT NULL {(primaryKey ? "PRIMARY KEY" : "")}, REVISION TEXT NOT NULL); DELETE FROM {table}; INSERT INTO {table} VALUES (41, 'old');";
            if (!primaryKey) setup.CommandText += $" INSERT INTO {table} VALUES (41, 'duplicate');";
            await setup.ExecuteNonQueryAsync();
        }
        var settings = new Dictionary<string, string?>(Fixture.SettingsForBoot())
        {
            ["Koan:Data:Sources:Publication:Adapter"] = "sqlite",
            ["Koan:Data:Sources:Publication:ConnectionString"] = Fixture.ConnectionString,
            ["Koan:Data:Sources:Publication:StorageLifecycle"] = StorageLifecycle.External.ToString()
        };
        await using var host = await KoanIntegrationHost.Configure().WithSettings(settings)
            .ConfigureServices(services => services.AddKoan(koan => koan.Data.Source("Publication")
                .Map<MappedPublication>(map => map.Container(table).Key(row => row.Id).Name("ID")
                    .Property(row => row.Revision).Name("REVISION"))))
            .StartAsync(TestContext.Current.CancellationToken);
        using var source = EntityContext.Source("Publication");
        var replacement = new MappedPublication { Id = 41, Revision = "new" };
        if (primaryKey)
        {
            (await replacement.ReplaceIf(row => row.Revision == "old")).Should().BeTrue();
            (await replacement.ReplaceIf(row => row.Revision == "new")).Should().BeTrue();
            (await MappedPublication.Get(41))!.Revision.Should().Be("new");
        }
        else
        {
            await FluentActions.Invoking(() => replacement.ReplaceIf(row => row.Revision == "old"))
                .Should().ThrowAsync<SchemaMismatchException>();
            await using var verify = new SqliteConnection(Fixture.ConnectionString);
            await verify.OpenAsync();
            await using var read = verify.CreateCommand();
            read.CommandText = $"SELECT COUNT(*) FROM {table} WHERE REVISION IN ('old', 'duplicate')";
            Convert.ToInt64(await read.ExecuteScalarAsync()).Should().Be(2);
        }
    }

    public sealed class Publication : Entity<Publication>
    {
        public string Revision { get; set; } = "";
        public string Body { get; set; } = "";
    }

    public sealed class MappedPublication : Entity<MappedPublication, long>
    {
        public string Revision { get; set; } = "";
    }
}
