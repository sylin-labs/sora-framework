using System.IO;
using AwesomeAssertions;
using Koan.Data.Core;
using Koan.Jobs;
using Koan.Jobs.TestKit;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Koan.Jobs.Adapter.Sqlite.Tests.Specs;

/// <summary>Tier-specific durable proofs (the shared behaviors run via <c>SqliteBehaviors</c>): the election picks
/// the data-backed ledger, and the transactional outbox enlists in an ambient transaction.</summary>
public sealed class DurableSqliteSpec
{
    [Fact]
    public async Task named_source_work_executes_from_central_ledger_and_survives_restart()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"koan-jobs-address-root-{Guid.NewGuid():N}.db");
        var workPath = Path.Combine(Path.GetTempPath(), $"koan-jobs-address-work-{Guid.NewGuid():N}.db");
        var settings = new Dictionary<string, string?>
        {
            ["Koan:Environment"] = "Test",
            ["Koan:Data:Sources:Default:Adapter"] = "sqlite",
            ["Koan:Data:Sources:Default:ConnectionString"] = $"Data Source={rootPath};Pooling=False",
            ["Koan:Data:Sources:localized:Adapter"] = "sqlite",
            ["Koan:Data:Sources:localized:ConnectionString"] = $"Data Source={workPath};Pooling=False"
        };
        var work = new GreetJob { Name = "named-source" };
        try
        {
            await using (var host = await JobsHarness.StartWithSettingsAsync(settings))
            {
                using (EntityContext.Source("localized"))
                using (EntityContext.Partition("fr")) await work.Job.Submit();
                var record = await host.JobFor<GreetJob>(work.Id);
                record.Should().NotBeNull();
                record!.WorkSource.Should().Be("localized");
                (await GreetJob.Get(work.Id)).Should().BeNull();
            }
            // Preserve the ledger while booting the same configuration into a new host.
            await using var restarted = await JobsHarness.StartWithSettingsAsync(settings, clearOnStart: false);
            await restarted.Drain();
            using (EntityContext.Source("localized"))
            using (EntityContext.Partition("fr"))
            {
                (await GreetJob.Get(work.Id))!.Greeting.Should().Be("Hello, named-source");
                (await work.Job.Status()).Should().Be(JobStatus.Completed);
            }
        }
        finally
        {
            if (File.Exists(rootPath)) File.Delete(rootPath);
            if (File.Exists(workPath)) File.Delete(workPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task partitioned_submission_retains_transaction_commit_or_rollback(bool commit)
    {
        await using var host = await JobsHarness.StartSqliteAsync();
        var work = new GreetJob { Name = "transaction" };
        using (EntityContext.Partition("fr"))
        using (EntityContext.Transaction("partitioned-submit"))
        {
            await work.Job.Submit();
            (await work.Job.Status()).Should().BeNull();
            if (commit) await EntityContext.Commit();
            else await EntityContext.Rollback();
        }
        await host.Drain();
        var records = await host.Ledger.Query(new JobQuery(WorkId: work.Id), default);
        records.Should().HaveCount(commit ? 1 : 0);
        using (EntityContext.Partition("fr"))
        {
            var stored = await GreetJob.Get(work.Id);
            if (commit) stored!.Greeting.Should().Be("Hello, transaction");
            else stored.Should().BeNull();
        }
    }

    [Fact]
    public async Task explicit_default_source_owns_the_database_file()
    {
        var db = Path.Combine(Path.GetTempPath(), $"koan-jobs-placement-{Guid.NewGuid():n}.db");
        var fallback = Path.Combine(Path.GetTempPath(), $"koan-jobs-fallback-{Guid.NewGuid():n}.db");
        try
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Koan:Environment"] = "Test",
                ["Koan:Data:Sources:Default:Adapter"] = "sqlite",
                ["Koan:Data:Sources:Default:ConnectionString"] = $"Data Source={db};Pooling=False",
                ["Koan:Data:Sqlite:ConnectionString"] = $"Data Source={fallback};Pooling=False",
            };
            await using (var host = await JobsHarness.StartWithSettingsAsync(settings)) { }

            await using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().BeGreaterThanOrEqualTo(3,
                "the configured file must contain the framework-owned job ledger schema, not merely a readiness probe");
            File.Exists(fallback).Should().BeFalse(
                "the unused provider fallback must not be materialized alongside the authoritative Default source");
        }
        finally
        {
            if (File.Exists(db)) File.Delete(db);
            if (File.Exists(fallback)) File.Delete(fallback);
        }
    }

    [Fact]
    public async Task election_picks_the_routing_ledger_over_a_durable_adapter()
    {
        await using var host = await JobsHarness.StartSqliteAsync();
        // A durable adapter elects the RoutingJobLedger: durable for Auto/DataStore types, volatile for InMemory.
        host.Ledger.Should().BeOfType<RoutingJobLedger>();
    }

    [Fact]
    public async Task submit_in_a_rolled_back_transaction_never_enqueues()
    {
        GreetJob.Reset();
        await using var host = await JobsHarness.StartSqliteAsync();
        var j = new GreetJob { Name = "x" };
        var id = j.Id;

        using (EntityContext.Transaction("rollback"))
        {
            await j.Job.Submit();
            await EntityContext.Rollback();
        }

        await host.Drain();
        GreetJob.Executions.Should().Be(0, "a rolled-back transaction must not enqueue the job");
        (await host.StatusOf<GreetJob>(id)).Should().BeNull();
    }

    [Fact]
    public async Task submit_in_a_committed_transaction_enqueues_once_on_commit()
    {
        GreetJob.Reset();
        await using var host = await JobsHarness.StartSqliteAsync();
        var j = new GreetJob { Name = "y" };
        var id = j.Id;

        using (EntityContext.Transaction("commit"))
        {
            await j.Job.Submit();
            (await host.StatusOf<GreetJob>(id)).Should().BeNull("deferred until commit (outbox)");
            await EntityContext.Commit();
        }

        await host.Drain();
        GreetJob.Executions.Should().Be(1);
        (await host.StatusOf<GreetJob>(id)).Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task collection_submission_reports_transaction_enlistment_until_commit()
    {
        await using var host = await JobsHarness.StartSqliteAsync();
        var jobs = new[]
        {
            new GreetJob { Name = "one" },
            new GreetJob { Name = "two" }
        };

        using (EntityContext.Transaction("collection-commit"))
        {
            var submission = await jobs.Submit();

            submission.Accepted.Should().Be(2);
            submission.Submitted.Should().Be(2);
            submission.PendingCommit.Should().BeTrue();
            (await host.StatusOf<GreetJob>(jobs[0].Id)).Should().BeNull("the ledger rows are not visible before commit");
            await EntityContext.Commit();
        }

        (await host.StatusOf<GreetJob>(jobs[0].Id)).Should().Be(JobStatus.Queued);
        (await host.StatusOf<GreetJob>(jobs[1].Id)).Should().Be(JobStatus.Queued);
    }
}
