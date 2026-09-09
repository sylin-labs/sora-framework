using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Abstractions.Analytics;
using Koan.Data.Analytics;
using Koan.Data.Analytics.Runtime;
using Koan.Data.Analytics.Recipes;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Analytics.Tests.Specs;

/// <summary>
/// Serve-or-compute: a materialized projection is served within its declared tolerance, computed live
/// when stale (backfilling when declared), and every answer says which path produced it. The
/// materialization store is per-host DuckDB, rebuilt from the record store — derived state, never a
/// second system of record.
/// </summary>
public sealed class AnalyticsProjectionSpec(SqliteFixture fixture) : IAsyncDisposable
{
    private IntegrationHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<IServiceProvider> BootAsync(string tag)
    {
        var materialization = Path.Combine(Path.GetTempPath(), $"koan-mat-{tag}.duckdb");
        if (File.Exists(materialization)) File.Delete(materialization);

        var host = KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .WithSetting("Koan:Data:Analytics:MaterializationConnectionString", $"Data Source={materialization}")
            .ConfigureServices(services => services.AddKoan())
            .Build();
        _host = host;
        AppHost.Current = host.Services;

        await new AnalyticsProbe { Name = $"{tag}-alpha", Priority = 1, Score = 10m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-alpha", Priority = 2, Score = 30m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-beta", Priority = 3, Score = 70m }.Save();
        await host.StartAsync();
        return host.Services;
    }

    private static void Declare(string name, string tag, Action<AnalyticsMaterializationBuilder> materialize) =>
        Analytics.Question<AnalyticsProbe, string>(name, q =>
        {
            q.Where(p => p.Name.StartsWith(tag))
             .By(p => p.Name)
             .Sum(p => p.Score)
             .Materialize(materialize);
        });

    private static decimal SumFor(AnalyticsResult answer, string name) =>
        Convert.ToDecimal(answer.Rows.Single(row => (string?)row.Values["Name"] == name).Values["sum_Score"]);

    [Fact]
    public async Task A_trigger_only_projection_stays_live_until_refreshed_then_serves()
    {
        var tag = "trig-" + Guid.CreateVersion7().ToString("N")[..10];
        Declare($"proj-{tag}-by-name", tag, r => { /* no interval, no tolerance: refresh is manual */ });
        var services = await BootAsync(tag);

        // Cold: nothing has refreshed the projection, so the ask computes live and says so.
        var live = await Analytics.Of<AnalyticsProbe, string>().Run($"proj-{tag}-by-name", ct: CancellationToken.None);
        live.ServedFrom.Should().Be("live");
        live.Age.Should().Be("live");
        SumFor(live, $"{tag}-alpha").Should().Be(40m);

        var sink = services.GetRequiredService<IAnalyticsProjectionSink>();
        (await sink.ReadStateAsync($"proj-{tag}-by-name", CancellationToken.None)).Should().BeNull(
            "a trigger-only projection is not materialized behind the caller's back");

        // Refresh through the door (what POST /analytics/refresh/{name} calls), then the ask serves.
        var refresher = services.GetRequiredService<AnalyticsProjectionRefresher>();
        var receipt = await refresher.RefreshAsync($"proj-{tag}-by-name");
        receipt.RowCount.Should().Be(2);

        var served = await Analytics.Of<AnalyticsProbe, string>().Run($"proj-{tag}-by-name", ct: CancellationToken.None);
        served.ServedFrom.Should().Be("materialization");
        served.Age.Should().NotBe("live", "a served answer is labeled with its age");
        SumFor(served, $"{tag}-alpha").Should().Be(40m, "the materialization matches the record store");
    }

    [Fact]
    public async Task A_stale_read_computes_live_and_backfills_when_declared()
    {
        var tag = "backfill-" + Guid.CreateVersion7().ToString("N")[..10];
        Declare($"proj-{tag}-by-name", tag, r =>
        {
            r.ServeWithin(TimeSpan.Zero)   // always stale: every read proves the live fallback
             .BackfillOnRead();
        });
        var services = await BootAsync(tag);

        var sink = services.GetRequiredService<IAnalyticsProjectionSink>();
        (await sink.ReadStateAsync($"proj-{tag}-by-name", CancellationToken.None)).Should().BeNull();

        var answer = await Analytics.Of<AnalyticsProbe, string>().Run($"proj-{tag}-by-name", ct: CancellationToken.None);
        answer.ServedFrom.Should().Be("live", "a zero tolerance is never served from the materialization");
        SumFor(answer, $"{tag}-alpha").Should().Be(40m);

        // Backfill-on-read: answering also re-materialized, so the engine now carries the state.
        var state = await sink.ReadStateAsync($"proj-{tag}-by-name", CancellationToken.None);
        state.Should().NotBeNull("the stale read backfilled the materialization");
        state!.RowCount.Should().Be(2);
    }

    [Fact]
    public async Task A_fresh_materialization_is_served_with_its_age()
    {
        var tag = "fresh-" + Guid.CreateVersion7().ToString("N")[..10];
        Declare($"proj-{tag}-by-name", tag, r =>
        {
            r.Every(TimeSpan.FromHours(6))
             .ServeWithin(TimeSpan.FromHours(6));
        });
        var services = await BootAsync(tag);

        // Observe the startup refresh instead of assuming a scheduler timing window.
        var sink = services.GetRequiredService<IAnalyticsProjectionSink>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await sink.ReadStateAsync($"proj-{tag}-by-name", timeout.Token) is null)
            await Task.Delay(25, timeout.Token);

        var answer = await Analytics.Of<AnalyticsProbe, string>().Run($"proj-{tag}-by-name", ct: CancellationToken.None);
        answer.ServedFrom.Should().Be("materialization", "a fresh materialization within tolerance is served");
        answer.Age.Should().NotBe("live");
        SumFor(answer, $"{tag}-alpha").Should().Be(40m);
        SumFor(answer, $"{tag}-beta").Should().Be(70m);

        // The grammar's read-model door: same rows, bounded, through code.
        var page = await Analytics.Rows($"proj-{tag}-by-name", limit: 10);
        page.Completion.Should().Be(AnalyticsCompletion.Complete);
        page.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_rows_door_refuses_on_demand_questions()
    {
        Analytics.Question<AnalyticsProbe, string>($"rows-refuse-{Guid.CreateVersion7():N}", q => q.Count());
        var services = await BootAsync("rows-refuse-" + Guid.CreateVersion7().ToString("N")[..8]);

        var onDemand = AnalyticsCatalog.Names().First(n => n.StartsWith("rows-refuse-", StringComparison.Ordinal));
        await FluentActions.Awaiting(async () => await Analytics.Rows(onDemand, ct: CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>()
            .Where(error => error.Message.Contains("on-demand question", StringComparison.Ordinal),
                "the read-model door serves materialized rows only");
    }

    [Fact]
    public async Task The_refresh_door_refuses_what_it_cannot_refresh()
    {
        Analytics.Question<AnalyticsProbe, string>($"proj-refuse-{Guid.CreateVersion7():N}", q => q.Count());
        var services = await BootAsync("refuse-" + Guid.CreateVersion7().ToString("N")[..8]);
        var refresher = services.GetRequiredService<AnalyticsProjectionRefresher>();

        (await FluentActions.Awaiting(() => refresher.RefreshAsync("proj-refuse-unknown"))
            .Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("proj-refuse-unknown");
        (await FluentActions.Awaiting(() => refresher.RefreshAsync(
                AnalyticsCatalog.Names().First(n => n.StartsWith("proj-refuse-", StringComparison.Ordinal))))
            .Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("only materialized questions",
            "an on-demand question computes live; there is nothing to refresh");
    }
}
