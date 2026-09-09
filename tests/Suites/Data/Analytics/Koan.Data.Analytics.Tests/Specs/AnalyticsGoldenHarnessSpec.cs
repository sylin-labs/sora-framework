using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Analytics;
using Koan.Testing.Integration;

namespace Koan.Data.Analytics.Tests.Specs;

/// <summary>
/// Golden questions are product infrastructure: declared questions carry known-answer assertions, and
/// the harness reports every wrong answer with its reason. Kept deployments of this feature class ran
/// such checks; killed ones skipped them (DATA-0123 evidence).
/// </summary>
public sealed class AnalyticsGoldenHarnessSpec(SqliteFixture fixture)
{
    [Fact]
    public async Task A_green_harness_reports_no_failures_and_a_wrong_expectation_is_reported()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        AppHost.Current = host.Services;

        await new AnalyticsProbe { Name = "harness", Priority = 7, Score = 55m }.Save();

        Analytics.Question<AnalyticsProbe, string>("harness-total", q => q.Where(p => p.Name == "harness").Count());
        AnalyticsGoldenQuestions.Register(new AnalyticsGoldenQuestion
        {
            QuestionName = "harness-total",
            Assert = answer => Convert.ToInt64(answer.Rows[0].Values["count"]) == 1
                ? null
                : "expected at least one probe"
        });
        AnalyticsGoldenQuestions.Register(new AnalyticsGoldenQuestion
        {
            QuestionName = "harness-wrong",
            Assert = _ => "this expectation is deliberately impossible to satisfy"
        });

        // A second question the wrong golden points at, so the audit exercises the missing-answer path too.
        Analytics.Question<AnalyticsProbe, string>("harness-wrong", q => q.Count());

        var failures = await AnalyticsHarness.AuditAsync(host.Services);

        failures.Should().ContainSingle(f => f.Contains("harness-wrong", StringComparison.Ordinal),
            "a golden question that cannot be satisfied must surface as a named failure");
        failures.Should().NotContain(f => f.Contains("harness-total", StringComparison.Ordinal),
            "the satisfied golden question must stay green");
    }
}
