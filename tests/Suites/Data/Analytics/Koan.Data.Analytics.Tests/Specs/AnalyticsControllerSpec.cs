using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Analytics.Runtime;
using Koan.Data.Analytics.Web.Controllers;
using Koan.Data.Abstractions.Analytics;
using Koan.Testing.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Analytics.Tests.Specs;

/// <summary>
/// The per-entity analytics controller: the recipe sheet lists only this entity's declared questions,
/// and the generic results door runs any of them bounded by N — while refusing questions that belong
/// to another entity (and recording unknown asks as coverage gaps).
/// </summary>
public sealed class AnalyticsControllerSpec(SqliteFixture fixture)
{
    private sealed class ProbeAnalyticsController : AnalyticsController<AnalyticsProbe, string>;

    private static ProbeAnalyticsController ControllerFor(IServiceProvider services)
    {
        var controller = new ProbeAnalyticsController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        return controller;
    }

    [Fact]
    public async Task The_recipe_sheet_lists_this_entity_and_the_results_door_runs_them()
    {
        var tag = "ctl-" + Guid.CreateVersion7().ToString("N")[..10];
        Analytics.Question<AnalyticsProbe, string>($"ctl-{tag}-count",
            q => q.Where(p => p.Name.StartsWith(tag)).Count());
        Analytics.Question<AnalyticsProbe, string>($"ctl-{tag}-by-name",
            q => q.Where(p => p.Name.StartsWith(tag)).By(p => p.Name).Count());

        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .WithSetting("Koan:Data:Analytics:RefreshLoopEnabled", "false")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        AppHost.Current = host.Services;
        await new AnalyticsProbe { Name = $"{tag}-alpha", Score = 10m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-beta", Score = 20m }.Save();

        var controller = ControllerFor(host.Services);

        var sheet = controller.Recipes();
        var sheetJson = System.Text.Json.JsonSerializer.Serialize(((ObjectResult)sheet).Value!);
        sheetJson.Should().Contain($"ctl-{tag}-count");

        var answer = await controller.Results($"ctl-{tag}-count", n: 10);
        var payload = (AnalyticsResult)((ObjectResult)answer).Value!;
        payload.Question.Should().Be($"ctl-{tag}-count");
        payload.Rows.Should().HaveCount(1);
        Convert.ToInt64(payload.Rows[0].Values["count"]).Should().Be(2);
        AnalyticsCatalog.Names().Should().Contain($"ctl-{tag}-by-name");
    }

    [Fact]
    public async Task The_results_door_bounds_the_answer_at_n()
    {
        var tag = "ctl-n-" + Guid.CreateVersion7().ToString("N")[..10];
        Analytics.Question<AnalyticsProbe, string>($"ctl-{tag}-by-name",
            q => q.Where(p => p.Name.StartsWith(tag)).By(p => p.Name).Count());

        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .WithSetting("Koan:Data:Analytics:RefreshLoopEnabled", "false")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        AppHost.Current = host.Services;
        for (var index = 0; index < 5; index++)
            await new AnalyticsProbe { Name = $"{tag}-g{index}" }.Save();

        var controller = ControllerFor(host.Services);
        var answer = await controller.Results($"ctl-{tag}-by-name", n: 2);
        var payload = (AnalyticsResult)((ObjectResult)answer).Value!;
        payload.Completion.Should().Be(AnalyticsCompletion.RowCapped,
            "n=2 bounds a five-group answer");
        payload.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unknown_recipe_is_refused_with_this_entitys_recipes_and_recorded()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .WithSetting("Koan:Data:Analytics:RefreshLoopEnabled", "false")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();

        var controller = ControllerFor(host.Services);
        var before = AnalyticsGapLog.TotalCount;

        var result = await controller.Results("ctl-no-such-recipe", n: 10);
        var notFound = (NotFoundObjectResult)result;
        notFound.Value!.ToString().Should().Contain("unknown-question");

        AnalyticsGapLog.TotalCount.Should().Be(before + 1, "an unknown ask is a coverage signal");
    }

    [Fact]
    public async Task Rows_serve_materialized_rows_and_refresh_replaces_them()
    {
        var tag = "ctl-mat-" + Guid.CreateVersion7().ToString("N")[..10];
        Analytics.Question<AnalyticsProbe, string>($"ctl-{tag}-mat",
            q => q.Where(p => p.Name.StartsWith(tag))
                  .By(p => p.Name).Count()
                  .Materialize(r => r.Every(TimeSpan.FromHours(6)).ServeWithin(TimeSpan.FromHours(6))));

        var materialization = Path.Combine(Path.GetTempPath(), $"koan-ctl-mat-{Guid.CreateVersion7():N}.duckdb");
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .WithSetting("Koan:Data:Analytics:AllowHttpRefreshTrigger", "true")
            .WithSetting("Koan:Data:Analytics:RefreshLoopEnabled", "false")
            .WithSetting("Koan:Data:Analytics:MaterializationConnectionString", $"Data Source={materialization}")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        AppHost.Current = host.Services;
        await new AnalyticsProbe { Name = $"{tag}-alpha", Score = 10m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-alpha", Score = 20m }.Save();

        var controller = ControllerFor(host.Services);

        // Rows door on a never-refreshed materialization: empty is the honest state, not an error.
        var cold = await controller.Rows($"ctl-{tag}-mat");
        var coldOk = (OkObjectResult)cold;
        var coldCompletion = (string)coldOk.Value!.GetType().GetProperty("Completion")!.GetValue(coldOk.Value)!;
        coldCompletion.Should().Be("Complete", "a never-refreshed projection answers empty, honestly");

        // Refresh through the door: rows appear, counts correct.
        var refreshed = (await controller.Refresh($"ctl-{tag}-mat",
            host.Services.GetRequiredService<AnalyticsProjectionRefresher>(),
            host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnalyticsOptions>>(),
            CancellationToken.None)) as OkObjectResult;
        refreshed.Should().BeOfType<OkObjectResult>();

        var served = await controller.Rows($"ctl-{tag}-mat");
        var servedOk = (OkObjectResult)served;
        var payload = System.Text.Json.JsonSerializer.Serialize(servedOk.Value!);
        payload.Should().Contain(tag + "-alpha").And.Contain("\"count\":2");

        // Parquet export through the engine's COPY: magic bytes prove real Parquet, not renamed JSON.
        var parquet = await controller.Rows($"ctl-{tag}-mat", format: "parquet");
        var file = (FileContentResult)parquet;
        // The export is engine-written Parquet, not renamed JSON.
        file.FileContents.AsSpan(0, 4).ToArray().Should().Equal((byte)'P', (byte)'A', (byte)'R', (byte)'1');
    }
}
