using Koan.Core;
using Koan.Data.Abstractions.Annotations;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Abstractions.Sources;
using Koan.Testing.Integration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Connector.Sqlite.Tests.Specs;

public sealed class SqliteInsertOnlySpec(SqliteFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<SqliteFixture>(fixture, output)
{
    [Fact]
    public async Task Duplicate_preserves_physical_bytes_and_ordinary_save_still_updates()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<InsertProbe, string>();
        DataCaps.Describe(repository, "sqlite").Has(DataCaps.Write.InsertOnly).Should().BeTrue();
        var insert = (IInsertOnlyRepository<InsertProbe, string>)repository;
        var original = new InsertProbe { Id = Guid.NewGuid().ToString("N"), Value = "original" };
        var receipt = await insert.Insert(original);
        receipt.Outcome.Should().Be(MutationOutcome.Inserted);
        receipt.CommitOutcome.Should().Be(DataCommitOutcome.Committed);
        var before = await StoredJson(original.Id);

        var conflict = await insert.Insert(new InsertProbe { Id = original.Id, Value = "replacement" });
        conflict.Key.Should().Be(original.Id);
        conflict.Outcome.Should().Be(MutationOutcome.Conflict);
        conflict.CommitOutcome.Should().Be(DataCommitOutcome.NotCommitted);
        conflict.Entity.Should().BeNull();
        (await StoredJson(original.Id)).Should().Be(before);

        original.Value = "ordinary save";
        await original.Save();
        (await StoredJson(original.Id)).Should().Contain("ordinary save");
    }

    [Fact]
    public async Task Concurrent_native_insertions_commit_exactly_one_identity()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<InsertProbe, string>();
        var insert = (IInsertOnlyRepository<InsertProbe, string>)repository;
        var id = Guid.NewGuid().ToString("N");
        // Warm schema separately so the race exercises native insertion, not schema realization.
        _ = await InsertProbe.Get(id);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await gate.Task;
            return await insert.Insert(new InsertProbe { Id = id, Value = $"winner-{index}" });
        })).ToArray();
        gate.SetResult();
        var receipts = await Task.WhenAll(attempts);
        var winner = receipts.Should().ContainSingle(result => result.Outcome == MutationOutcome.Inserted).Subject;
        receipts.Count(result => result.Outcome == MutationOutcome.Conflict).Should().Be(7);
        receipts.Where(result => result.Outcome == MutationOutcome.Conflict)
            .Should().OnlyContain(result => result.Entity == null && result.CommitOutcome == DataCommitOutcome.NotCommitted);
        (await StoredJson(id)).Should().Contain(winner.Entity!.Value);
    }

    [Fact]
    public async Task Generated_identity_is_returned_and_nonidentity_constraints_are_not_conflicts()
    {
        RequireBackingStore();
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(Fixture.SettingsForBoot())
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("Default").Map<GeneratedProbe>(map => map
                    .Container("KOAN_INSERT_GENERATED")
                    .Key(item => item.Id).Name("ID").Generated()
                    .Property(item => item.Value).Name("VALUE"))))
            .StartAsync(TestContext.Current.CancellationToken);
        var insert = (IInsertOnlyRepository<GeneratedProbe, long>)host.Services
            .GetRequiredService<IDataService>().GetRepository<GeneratedProbe, long>();
        var model = new GeneratedProbe { Value = Guid.NewGuid().ToString("N") };
        var receipt = await insert.Insert(model);
        model.Id.Should().BeGreaterThan(0);
        receipt.Key.Should().Be(model.Id);
        receipt.Entity!.Id.Should().Be(model.Id);
        receipt.CommitOutcome.Should().Be(DataCommitOutcome.Committed);

        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync();
        await using var index = connection.CreateCommand();
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_KOAN_INSERT_VALUE ON KOAN_INSERT_GENERATED (VALUE)";
        await index.ExecuteNonQueryAsync();
        await FluentActions.Invoking(() => insert.Insert(new GeneratedProbe { Value = model.Value }))
            .Should().ThrowAsync<SqliteException>();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM KOAN_INSERT_GENERATED WHERE VALUE = @value";
        count.Parameters.AddWithValue("@value", model.Value);
        Convert.ToInt64(await count.ExecuteScalarAsync()).Should().Be(1);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT VALUE FROM KOAN_INSERT_GENERATED WHERE ID = @id";
        read.Parameters.AddWithValue("@id", model.Id);
        (await read.ExecuteScalarAsync()).Should().Be(model.Value);
    }

    private async Task<string> StoredJson(string id)
    {
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM KOAN_INSERT_PROBE WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Trigger_ignore_and_another_tables_primary_key_violation_are_not_identity_conflicts()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var insert = (IInsertOnlyRepository<TriggerProbe, string>)host.Services
            .GetRequiredService<IDataService>().GetRepository<TriggerProbe, string>();
        _ = await TriggerProbe.Get("ignored");
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE KOAN_INSERT_TRIGGER_OTHER (Id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO KOAN_INSERT_TRIGGER_OTHER VALUES ('occupied');
                CREATE TRIGGER KOAN_INSERT_IGNORE BEFORE INSERT ON KOAN_INSERT_TRIGGER_PROBE
                WHEN NEW.Id = 'ignored' BEGIN SELECT RAISE(IGNORE); END;
                CREATE TRIGGER KOAN_INSERT_OTHER_KEY BEFORE INSERT ON KOAN_INSERT_TRIGGER_PROBE
                WHEN NEW.Id = 'other-key' BEGIN INSERT INTO KOAN_INSERT_TRIGGER_OTHER VALUES ('occupied'); END;
                """;
            await setup.ExecuteNonQueryAsync();
        }
        await FluentActions.Invoking(() => insert.Insert(new TriggerProbe { Id = "ignored" }))
            .Should().ThrowAsync<InvalidOperationException>();
        var failure = await FluentActions.Invoking(() => insert.Insert(new TriggerProbe { Id = "other-key" }))
            .Should().ThrowAsync<SqliteException>();
        failure.Which.SqliteExtendedErrorCode.Should().Be(1555);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM KOAN_INSERT_TRIGGER_PROBE";
        Convert.ToInt64(await count.ExecuteScalarAsync()).Should().Be(0);
    }

    [Fact]
    public async Task External_mapping_without_native_primary_identity_rejects_before_insertion()
    {
        RequireBackingStore();
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE KOAN_INSERT_NONUNIQUE (ID INTEGER NOT NULL, VALUE TEXT NOT NULL)";
            await create.ExecuteNonQueryAsync();
        }
        var settings = new Dictionary<string, string?>(Fixture.SettingsForBoot(), StringComparer.Ordinal)
        {
            ["Koan:Data:Sources:External:Adapter"] = "sqlite",
            ["Koan:Data:Sources:External:ConnectionString"] = Fixture.ConnectionString,
            ["Koan:Data:Sources:External:StorageLifecycle"] = StorageLifecycle.External.ToString()
        };
        await using var host = await KoanIntegrationHost.Configure()
            .WithSettings(settings)
            .ConfigureServices(services => services.AddKoan(koan =>
                koan.Data.Source("External").Map<GeneratedProbe>(map => map
                    .Container("KOAN_INSERT_NONUNIQUE")
                    .Key(item => item.Id).Name("ID")
                    .Property(item => item.Value).Name("VALUE"))))
            .StartAsync(TestContext.Current.CancellationToken);
        using (EntityContext.Source("External"))
        {
            var insert = (IInsertOnlyRepository<GeneratedProbe, long>)host.Services
                .GetRequiredService<IDataService>().GetRepository<GeneratedProbe, long>();
            await FluentActions.Invoking(() => insert.Insert(new GeneratedProbe { Id = 7, Value = "not persisted" }))
                .Should().ThrowAsync<Koan.Data.Relational.Orchestration.SchemaMismatchException>();
        }
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM KOAN_INSERT_NONUNIQUE";
        Convert.ToInt64(await count.ExecuteScalarAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Non_generated_default_numeric_identity_rejects_before_native_insert()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<NumericProbe, long>();
        var insert = (IInsertOnlyRepository<NumericProbe, long>)repository;
        await FluentActions.Invoking(() => insert.Insert(new NumericProbe()))
            .Should().ThrowAsync<NotSupportedException>();
        await using var connection = new SqliteConnection(Fixture.ConnectionString);
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM KOAN_INSERT_NUMERIC_PROBE WHERE Id = 0";
        Convert.ToInt64(await count.ExecuteScalarAsync()).Should().Be(0);
    }

    [Storage(Name = "KOAN_INSERT_NUMERIC_PROBE")]
    private sealed class NumericProbe : Entity<NumericProbe, long>;

    [Storage(Name = "KOAN_INSERT_PROBE")]
    private sealed class InsertProbe : Entity<InsertProbe>
    {
        public string Value { get; set; } = "";
    }

    private sealed class GeneratedProbe : Entity<GeneratedProbe, long>
    {
        public string Value { get; set; } = "";
    }

    [Storage(Name = "KOAN_INSERT_TRIGGER_PROBE")]
    private sealed class TriggerProbe : Entity<TriggerProbe>;
}
