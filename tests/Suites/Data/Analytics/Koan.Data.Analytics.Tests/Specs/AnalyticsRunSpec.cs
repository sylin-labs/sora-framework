using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Abstractions.Analytics;
using Koan.Data.Analytics;
using Koan.Data.Analytics.Recipes;
using Koan.Testing.Integration;

namespace Koan.Data.Analytics.Tests.Specs;

/// <summary>
/// The run path: bounded on-demand asks over the store that owns the data, each answer carrying its
/// provenance (question, engine, age, cap). Seeds are tagged per test because the assembly shares one
/// store — analytics must stay correct on a store that is not exclusively its own.
/// </summary>
public sealed class AnalyticsRunSpec(SqliteFixture fixture) : IAsyncDisposable
{
    private IntegrationHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<IServiceProvider> BootAndSeedAsync(string tag, (string Name, int Priority, decimal Score)[] seeds)
    {
        var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        _host = host;
        AppHost.Current = host.Services;
        foreach (var seed in seeds)
            await new AnalyticsProbe { Name = $"{tag}-{seed.Name}", Priority = seed.Priority, Score = seed.Score }.Save();
        return host.Services;
    }

    [Fact]
    public async Task A_count_question_answers_with_its_envelope()
    {
        var tag = Tag();
        Analytics.Question<AnalyticsProbe, string>($"run-{tag}-count",
            q => q.Where(p => p.Name.StartsWith(tag)).Count());
        var services = await BootAndSeedAsync(tag, [("a", 1, 10m), ("b", 2, 20m), ("c", 3, 30m)]);

        var answer = await Analytics.Of<AnalyticsProbe, string>().Run($"run-{tag}-count", ct: CancellationToken.None);

        answer.Engine.Should().Be("sqlite");
        answer.Age.Should().Be("live");
        answer.Completion.Should().Be(AnalyticsCompletion.Complete);
        answer.Rows.Should().HaveCount(1);
        Convert.ToInt64(answer.Rows[0].Values["count"]).Should().Be(3);
    }

    [Fact]
    public async Task A_sum_question_aggregates_a_numeric_member()
    {
        var tag = Tag();
        Analytics.Question<AnalyticsProbe, string>($"run-{tag}-sum",
            q => q.Where(p => p.Name.StartsWith(tag)).Sum(p => p.Score));
        var services = await BootAndSeedAsync(tag, [("a", 1, 10m), ("b", 2, 20m), ("c", 3, 70m)]);

        var answer = await Analytics.Of<AnalyticsProbe, string>().Run($"run-{tag}-sum", ct: CancellationToken.None);

        Convert.ToDecimal(answer.Rows[0].Values["sum_Score"]).Should().Be(100m);
    }

    [Fact]
    public async Task A_grouped_question_counts_per_group_deterministically()
    {
        var tag = Tag();
        Analytics.Question<AnalyticsProbe, string>($"run-{tag}-by-name",
            q => q.Where(p => p.Name.StartsWith(tag)).By(p => p.Name).Count());
        var services = await BootAndSeedAsync(tag,
            [("alpha", 1, 10m), ("alpha", 2, 20m), ("beta", 3, 5m), ("gamma", 1, 1m)]);

        var first = await Analytics.Of<AnalyticsProbe, string>().Run($"run-{tag}-by-name", ct: CancellationToken.None);
        var second = await Analytics.Of<AnalyticsProbe, string>().Run($"run-{tag}-by-name", ct: CancellationToken.None);

        first.Rows.Select(r => r.Values["Name"]).Should().Equal(
            new object[] { $"{tag}-alpha", $"{tag}-beta", $"{tag}-gamma" },
            "group order is deterministic, because agents and callers compare answers");
        first.Rows.Select(r => Convert.ToInt64(r.Values["count"])).Should().Equal(2L, 1L, 1L);
        second.Rows.Select(r => (object?)r.Values["count"]).Should().Equal(first.Rows.Select(r => (object?)r.Values["count"]),
            "the same question, asked twice, is the same answer — determinism is the contract");
    }

    [Fact]
    public async Task A_row_capped_answer_says_so()
    {
        var tag = Tag();
        Analytics.Question<AnalyticsProbe, string>($"run-{tag}-capped",
            q => q.Where(p => p.Name.StartsWith(tag)).By(p => p.Name).Count(),
            rowCap: 2);
        var services = await BootAndSeedAsync(tag,
            [("a", 1, 1m), ("b", 1, 2m), ("c", 1, 3m), ("d", 1, 4m)]);

        var answer = await Analytics.Of<AnalyticsProbe, string>().Run($"run-{tag}-capped", ct: CancellationToken.None);

        answer.Completion.Should().Be(AnalyticsCompletion.RowCapped);
        answer.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_ephemeral_ask_computes_without_joining_the_catalog()
    {
        var tag = Tag();
        var services = await BootAndSeedAsync(tag, [("alpha", 1, 10m), ("alpha", 2, 30m)]);

        var answer = await Analytics.Of<AnalyticsProbe, string>().Ask(
            q => q.Where(p => p.Name == $"{tag}-alpha").Sum(p => p.Score), CancellationToken.None);

        Convert.ToDecimal(answer.Rows[0].Values["sum_Score"]).Should().Be(40m, "alpha's two probes carry 10 + 30");
    }

    /// <summary>
    /// The full random tail matters: a timestamp-led prefix collides when two tests start in the same
    /// millisecond, and analytics answers must never inherit another test's rows.
    /// </summary>
    private static string Tag() => "run-" + Guid.CreateVersion7().ToString("N");
}
