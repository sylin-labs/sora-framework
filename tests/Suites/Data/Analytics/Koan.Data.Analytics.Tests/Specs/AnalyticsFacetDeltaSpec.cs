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
/// The facet and delta doors (ANL-5). Distribution facets answer "what is the distribution?";
/// with a watermark they answer "what has been moving?" — a different question, and the envelope
/// names which ran. The delta door hands back the cursor for the next poll: consumers never
/// construct watermarks, the server keeps no per-consumer state.
/// </summary>
public sealed class AnalyticsFacetDeltaSpec(SqliteFixture fixture) : IAsyncDisposable
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

        var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .WithSetting("Koan:Data:Analytics:MaterializationConnectionString", $"Data Source={materialization}")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        _host = host;
        AppHost.Current = host.Services;
        return host.Services;
    }

    private async Task<IServiceProvider> BootSeededAsync(string tag)
    {
        var services = await BootAsync(tag);
        await new AnalyticsProbe { Name = $"{tag}-alpha", Priority = 1, Score = 10m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-alpha", Priority = 2, Score = 30m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-beta", Priority = 3, Score = 70m }.Save();
        return services;
    }

    private static string Declare(string tag) =>
        DeclareNamed($"proj-{tag}-by-name", tag);

    private static string DeclareNamed(string name, string tag)
    {
        Analytics.Question<AnalyticsProbe, string>(name, q => q
            .Where(p => p.Name.StartsWith(tag))
            .By(p => p.Name)
            .Sum(p => p.Score)
            .Materialize(r => { /* trigger-only: refresh is explicit in these specs */ }));
        return name;
    }

    private static async Task<AnalyticsWatermark> RefreshAsync(IServiceProvider services, string name)
    {
        var refresher = services.GetRequiredService<AnalyticsProjectionRefresher>();
        await refresher.RefreshAsync(name);
        // The door hands the cursor back; read it through the sink the way a consumer would
        // receive it from the first delta call.
        var first = await Analytics.Delta(name, since: null, ct: CancellationToken.None);
        return first.Watermark;
    }

    [Fact]
    public async Task Distribution_facets_summarize_the_materialized_column()
    {
        var tag = "facet-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        await BootSeededAsync(tag);

        await RefreshAsync(AppHost.Current!, name);
        var facets = await Analytics.Facets(name, "Name", ct: CancellationToken.None);

        facets.Mode.Should().Be(AnalyticsFacetMode.Distribution, "no watermark means the full distribution");
        facets.Buckets.Should().HaveCount(2);
        facets.Completion.Should().Be(AnalyticsCompletion.Complete);
        // The projection is grouped by Name, so its materialized rows carry one tuple per distinct
        // value — the facet door lists the values (the dropdown shape), counting materialized tuples.
        // Equal counts tie-break by value.
        facets.Buckets.Select(static b => (string?)b.Value).Should().Equal(
            $"{tag}-alpha", $"{tag}-beta");
        facets.Buckets.Should().OnlyContain(b => b.Count == 1);
    }

    [Fact]
    public async Task Facets_refuse_on_demand_questions_and_undeclared_columns()
    {
        var tag = "facet-refuse-" + Guid.CreateVersion7().ToString("N")[..8];
        Analytics.Question<AnalyticsProbe, string>($"facet-ondemand-{tag}", q => q.Count());
        await BootAsync(tag);
        var onDemand = AnalyticsCatalog.Names().First(n => n.StartsWith("facet-ondemand-", StringComparison.Ordinal));

        (await FluentActions.Awaiting(() => Analytics.Facets(onDemand, "Name", ct: CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("on-demand", "facets read materializations");

        var name = Declare("facet-col-" + Guid.CreateVersion7().ToString("N")[..8]);
        (await FluentActions.Awaiting(() => Analytics.Facets(name, "NotAColumn", ct: CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("NotAColumn").And.Contain("Name",
                "the refusal lists what the projection actually declares");
    }

    [Fact]
    public async Task Bucket_capped_facets_say_so()
    {
        var tag = "facet-cap-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        await BootSeededAsync(tag);
        await RefreshAsync(AppHost.Current!, name);

        var facets = await Analytics.Facets(name, "Name", limit: 1, ct: CancellationToken.None);

        facets.Buckets.Should().HaveCount(1);
        facets.Completion.Should().Be(AnalyticsCompletion.RowCapped,
            "two distinct names exist; a one-bucket answer must state the cap");
    }

    [Fact]
    public async Task The_delta_door_hands_back_the_cursor_and_serves_changes()
    {
        var tag = "delta-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        var services = await BootSeededAsync(tag);

        // First poll after an initial materialization: everything written so far, plus the cursor.
        var refresher = services.GetRequiredService<AnalyticsProjectionRefresher>();
        await refresher.RefreshAsync(name);
        var first = await Analytics.Delta(name, ct: CancellationToken.None);
        first.Watermark.Given.Should().BeNull("the first poll starts from the beginning");
        first.Watermark.Current.Should().NotBeNullOrWhiteSpace();
        first.Rows.Should().HaveCount(2);
        first.Rows.Should().OnlyContain(row => !row.Values.ContainsKey("_koan_stamp"),
            "the stamp is operational; no door leaks it");

        // New source data, re-materialized: the held cursor now splits before the new writes.
        await new AnalyticsProbe { Name = $"{tag}-gamma", Priority = 4, Score = 90m }.Save();
        await new AnalyticsProbe { Name = $"{tag}-alpha", Priority = 5, Score = 5m }.Save();
        await refresher.RefreshAsync(name);

        var second = await Analytics.Delta(name, since: first.Watermark.Current, ct: CancellationToken.None);
        second.Watermark.Given.Should().Be(first.Watermark.Current, "the envelope echoes the cursor it consumed");
        second.Rows.Should().HaveCount(3,
            "the second refresh rewrote everything wholesale: alpha's sum changed, beta was rewritten, gamma is new");
        second.Rows.Should().Contain(row => (string?)row.Values["Name"] == $"{tag}-gamma");
        second.Completion.Should().Be(AnalyticsCompletion.Complete);
    }

    [Fact]
    public async Task Movement_facets_summarize_changes_with_their_limits_stated()
    {
        var tag = "move-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        await BootSeededAsync(tag);

        var services = await BootSeededAsync(tag);
        var refresher = services.GetRequiredService<AnalyticsProjectionRefresher>();
        await refresher.RefreshAsync(name);
        var first = await Analytics.Delta(name, ct: CancellationToken.None);
        await new AnalyticsProbe { Name = $"{tag}-alpha", Priority = 5, Score = 5m }.Save();
        await refresher.RefreshAsync(name);

        var movement = await Analytics.Facets(name, "Name", since: first.Watermark.Current, ct: CancellationToken.None);

        movement.Mode.Should().Be(AnalyticsFacetMode.Movement, "a watermark flips the question");
        movement.ChangesConsidered.Should().Be(2,
            "the second refresh rewrote both group tuples wholesale — both count as movement since the cursor");
        movement.Buckets.Should().Contain(bucket => (string?)bucket.Value == $"{tag}-alpha");
        movement.DeletesInvisible.Should().BeTrue(
            "the envelope states the blindness: deleted source rows leave no trace in a derived store's stamps");
        movement.Watermark!.Given.Should().Be(first.Watermark.Current);
        movement.Watermark.Current.Should().NotBeNullOrWhiteSpace();
        movement.Completion.Should().Be(AnalyticsCompletion.Complete);
        movement.Buckets.Should().HaveCount(2, "both tuples were rewritten");
    }

    [Fact]
    public async Task A_malformed_watermark_refuses_instead_of_rewinding()
    {
        var tag = "wm-" + Guid.CreateVersion7().ToString("N")[..10];
        var name = Declare(tag);
        await BootSeededAsync(tag);
        await RefreshAsync(AppHost.Current!, name);

        (await FluentActions.Awaiting(() => Analytics.Delta(name, since: "not-a-cursor", ct: CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain(AnalyticsWatermark.Prefix,
                "the refusal shows the expected cursor shape — silently treating garbage as 'from the beginning' would over-serve");

        (await FluentActions.Awaiting(() => Analytics.Facets(name, "Name", since: "wm1.notanumber", ct: CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("wm1");
    }
}
