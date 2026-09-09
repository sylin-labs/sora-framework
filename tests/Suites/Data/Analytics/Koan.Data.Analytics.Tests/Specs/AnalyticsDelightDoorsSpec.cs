using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Abstractions.Analytics;
using Koan.Data.Analytics;
using Koan.Data.Analytics.Runtime;
using Koan.Data.Analytics.Recipes;
using Koan.Data.Analytics.Web.Controllers;
using Koan.Testing.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Analytics.Tests.Specs;

/// <summary>
/// The delight doors (ANL-6): explain (what would this ask do — without executing), history (the
/// refresh ledger with its triggers), shape (the answer's shape from the declaration alone), and
/// freshness negotiation (maxAge + materialization-derived caching headers). All four expose facts
/// the surface already owns; the specs pin that they invent none.
/// </summary>
public sealed class AnalyticsDelightDoorsSpec(SqliteFixture fixture) : IAsyncDisposable
{
    private IntegrationHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<IServiceProvider> BootSeededAsync(string tag)
    {
        var materialization = Path.Combine(Path.GetTempPath(), $"koan-mat-{tag}.duckdb");
        if (File.Exists(materialization)) File.Delete(materialization);

        var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .WithSetting("Koan:Data:Analytics:MaterializationConnectionString", $"Data Source={materialization}")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        _host = host;
        AppHost.Current = host.Services;
        await new AnalyticsProbe { Name = $"{tag}-alpha", Priority = 1, Score = 10m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-beta", Priority = 3, Score = 70m }.Save();
        return host.Services;
    }

    private static string Declare(string tag, Action<AnalyticsMaterializationBuilder>? materialize = null) =>
        DeclareNamed($"proj-{tag}-by-name", tag, materialize);

    private static string DeclareNamed(string name, string tag, Action<AnalyticsMaterializationBuilder>? materialize = null)
    {
        // One Where — a second call replaces, not ANDs. The tag scopes the question to this run's seeds
        // so the shared record store cannot skew counts.
        Analytics.Question<AnalyticsProbe, string>(name, q =>
        {
            q.WithParameterDefault<int>("min-priority", 0)
             .Where(p => p.Name.StartsWith(tag) && p.Priority >= Analytics.P<int>("min-priority"))
             .By(p => p.Name)
             .Sum(p => p.Score);
            if (materialize is null) q.Materialize(r => { });
            else q.Materialize(materialize);
        });
        return name;
    }

    [Fact]
    public async Task Shape_describes_the_answer_without_computing()
    {
        var tag = "shape-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        await BootSeededAsync(tag);

        var shape = Analytics.Shape(name);
        shape.Name.Should().Be(name);
        shape.Entity.Should().Be(nameof(AnalyticsProbe));
        shape.MeasureKind.Should().Be("sum");
        shape.GroupMember.Should().Be("Name");
        shape.Parameters.Should().ContainSingle(p => p.Name == "min-priority" && p.ClrType == typeof(int),
            "declared parameters are part of the shape");
        shape.Materialized.Should().BeTrue();
        shape.Policy.Should().NotBeNull();
        shape.Columns.Should().Contain(c => c.Name == "Name").And.Contain(c => c.Name == "sum_Score");

        // On-demand questions shape too — Materialized false is the flag that refuses the row doors.
        Analytics.Question<AnalyticsProbe, string>($"shape-ondemand-{tag}", q => q.Count());
        var onDemand = Analytics.Shape(AnalyticsCatalog.Names().First(n => n.StartsWith("shape-ondemand-", StringComparison.Ordinal)));
        onDemand.Materialized.Should().BeFalse();
        onDemand.Policy.Should().BeNull();
    }

    [Fact]
    public async Task Explain_reports_compute_without_executing_anything()
    {
        var tag = "exp-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        var services = await BootSeededAsync(tag);

        var explanation = await Analytics.Explain(name, new Dictionary<string, object?> { ["min-priority"] = 0 });

        explanation.Would.Should().Be("compute", "nothing is materialized yet");
        explanation.Reason.Should().Contain("nothing is materialized");
        explanation.Composed.Should().NotBeNull("composition is explanation, not execution");
        explanation.Engine.Should().Be("duckdb", "the elected engine explains itself");
        explanation.Composed!.Provider.Should().Be("sqlite", "composition happens over the record store — that is where the data lives");
        explanation.Capabilities.Should().Contain("facets").And.Contain("delta").And.Contain("parquet");
        explanation.SuppliedParameters.Should().Contain("min-priority");
        explanation.LastRefreshUtc.Should().BeNull();

        // The side-effect law: explain never materializes — the projection is still cold afterwards.
        var sink = services.GetRequiredService<IAnalyticsProjectionSink>();
        (await sink.ReadStateAsync(name, CancellationToken.None)).Should().BeNull(
            "an explain that created refresh state would be lying about never executing");
    }

    [Fact]
    public async Task Explain_reports_serve_when_fresh_and_refuse_when_parameters_are_missing()
    {
        var tag = "exp2-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag, r => r.ServeWithin(TimeSpan.FromHours(1)));
        var services = await BootSeededAsync(tag);
        await services.GetRequiredService<AnalyticsProjectionRefresher>().RefreshAsync(name);

        var warm = await Analytics.Explain(name, new Dictionary<string, object?> { ["min-priority"] = 0 });
        warm.Would.Should().Be("serve", "the materialization is within tolerance");
        warm.LastRefreshUtc.Should().NotBeNull();
        warm.MaterializedRows.Should().Be(2);

        // Defaults bind when no ask-time value arrives — that is how parameterized projections refresh.
        // A parameter declared WITHOUT a default still refuses: it has no fallback.
        var required = $"proj-{tag}-required";
        Analytics.Question<AnalyticsProbe, string>(required, q =>
        {
            q.WithParameter<int>("min-priority")
             .Where(p => p.Name.StartsWith(tag) && p.Priority >= Analytics.P<int>("min-priority"))
             .By(p => p.Name)
             .Sum(p => p.Score)
             .Materialize(r => { });
        });
        var cold = await Analytics.Explain(required);
        cold.Would.Should().Be("refuse", "a default-less parameter has no fallback to bind");
        cold.Reason.Should().Contain("min-priority");
    }

    [Fact]
    public async Task History_records_what_triggered_every_refresh()
    {
        var tag = "hist-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        var services = await BootSeededAsync(tag);
        var refresher = services.GetRequiredService<AnalyticsProjectionRefresher>();

        await refresher.RefreshAsync(name);
        await refresher.RefreshAsync(name, CancellationToken.None, "http");

        var history = await Analytics.History(name);
        history.Entries.Should().HaveCount(2);
        history.Entries[0].RanUtc.Should().BeOnOrAfter(history.Entries[1].RanUtc, "newest first");
        history.Entries.Select(e => e.Trigger).Should().Contain("programmatic").And.Contain("http",
            "the ledger names what caused every re-materialization");
        history.Entries.Should().OnlyContain(e => e.RowCount == 2 && e.DurationMs >= 0);
    }

    [Fact]
    public async Task MaxAge_negotiates_the_served_path_per_ask()
    {
        var tag = "age-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        var services = await BootSeededAsync(tag);
        await services.GetRequiredService<AnalyticsProjectionRefresher>().RefreshAsync(name);

        var generous = await Analytics.Of<AnalyticsProbe, string>().Run(
            name, null, TimeSpan.FromHours(1), CancellationToken.None);
        generous.ServedFrom.Should().Be("materialization", "the materialization is within the caller's tolerance");
        generous.MaterializedUtc.Should().NotBeNull();

        var demanding = await Analytics.Of<AnalyticsProbe, string>().Run(
            name, null, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        demanding.ServedFrom.Should().Be("live", "a fresher-than-declared demand computes instead of being served");

        Analytics.Question<AnalyticsProbe, string>($"age-ondemand-{tag}", q => q.Count());
        (await FluentActions.Awaiting(() => Analytics.Of<AnalyticsProbe, string>()
                .Run($"age-ondemand-{tag}", null, TimeSpan.FromMinutes(1), CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("on-demand",
                "maxAge negotiates materialization freshness; a live ask is always age zero");
    }

    [Fact]
    public async Task Malformed_and_negative_durations_refuse()
    {
        AnalyticsDuration.TryParse("15m", out var minutes).Should().BeTrue();
        minutes.Should().Be(TimeSpan.FromMinutes(15));
        AnalyticsDuration.TryParse("90", out var plain).Should().BeTrue();
        plain.Should().Be(TimeSpan.FromSeconds(90));
        AnalyticsDuration.TryParse("1d", out var days).Should().BeTrue();
        days.Should().Be(TimeSpan.FromDays(1));
        AnalyticsDuration.TryParse("bogus", out _).Should().BeFalse();
        AnalyticsDuration.TryParse("-5m", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Materialized_answers_carry_freshness_headers_and_revalidate_with_304()
    {
        var tag = "etag-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        var services = await BootSeededAsync(tag);
        await services.GetRequiredService<AnalyticsProjectionRefresher>().RefreshAsync(name);

        var controller = ControllerFor(services);
        var first = await controller.Results(name, n: 10);
        var payload = (AnalyticsResult)((ObjectResult)first).Value!;
        payload.ServedFrom.Should().Be("materialization");
        payload.MaterializedUtc.Should().NotBeNull();

        controller.Response.Headers.ETag.Should().NotBeEmpty("the answer carries a revalidation token");
        controller.Response.Headers.LastModified.ToString().Should().NotBeEmpty();
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-cache");

        var etag = controller.Response.Headers.ETag.ToString();
        var revalidator = ControllerFor(services);
        revalidator.Request.Headers["If-None-Match"] = etag;
        var second = await revalidator.Results(name, n: 10);
        second.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(304, "unchanged inputs revalidate to no body");
    }

    [Fact]
    public async Task The_explain_history_and_shape_doors_refuse_the_unknown()
    {
        var tag = "doors-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        await BootSeededAsync(tag);

        (await FluentActions.Awaiting(() => Analytics.Explain("no-such-question"))
            .Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("no-such-question");
        (await FluentActions.Awaiting(() => Analytics.History("no-such-question"))
            .Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("no-such-question");
        FluentActions.Invoking(() => Analytics.Shape("no-such-question"))
            .Should().Throw<KeyNotFoundException>().Which.Message.Should().Contain("no-such-question");

        Analytics.Question<AnalyticsProbe, string>($"doors-ondemand-{tag}", q => q.Count());
        var onDemand = AnalyticsCatalog.Names().First(n => n.StartsWith("doors-ondemand-", StringComparison.Ordinal));
        (await FluentActions.Awaiting(() => Analytics.History(onDemand))
            .Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("on-demand", "an on-demand question has no ledger");
    }

    private static ProbeAnalyticsController ControllerFor(IServiceProvider services)
    {
        var controller = new ProbeAnalyticsController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        return controller;
    }

    private sealed class ProbeAnalyticsController : AnalyticsController<AnalyticsProbe, string>;
}
