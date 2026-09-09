using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Analytics;
using Koan.Data.Analytics.Recipes;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Analytics.Tests.Specs;

/// <summary>
/// Parameterized questions: a declared Where marker binds at ask time — one question answering a family
/// of slices. Missing values refuse with the required names; extra values are coverage signals, not
/// errors. Undeclared parameters are refused, never silently ignored.
/// </summary>
public sealed class AnalyticsParameterSpec(SqliteFixture fixture) : IAsyncDisposable
{
    private IntegrationHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private const string QuestionName = "param-spec-high-or-above";

    private async Task<IServiceProvider> BootAndSeedAsync(string? namePrefix = null)
    {
        var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "sqlite")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", fixture.ConnectionString)
            .ConfigureServices(services => services.AddKoan())
            .StartAsync();
        _host = host;
        AppHost.Current = host.Services;
        string Name(int index) => namePrefix is null ? $"p{index}" : $"{namePrefix}-p{index}";
        await new AnalyticsProbe { Name = Name(1), Priority = 1, Score = 10m }.Save();
        await new AnalyticsProbe { Name = Name(2), Priority = 5, Score = 20m }.Save();
        await new AnalyticsProbe { Name = Name(3), Priority = 9, Score = 30m }.Save();
        return host.Services;
    }

    [Fact]
    public async Task A_parameterized_question_binds_ask_time_values()
    {
        // The suite shares one SQLite database; the name prefix scopes the count to this run's seeds so
        // concurrent specs cannot skew the answer.
        var prefix = "param-" + Guid.CreateVersion7().ToString("N")[..12];
        Analytics.Question<AnalyticsProbe, string>(QuestionName, q => q
            .WithParameter<int>("min-priority")
            .Where(t => t.Name.StartsWith(prefix) && t.Priority >= Analytics.P<int>("min-priority"))
            .Count());
        var services = await BootAndSeedAsync(prefix);

        var answer = await Analytics.Of<AnalyticsProbe, string>().Run(
            QuestionName, new Dictionary<string, object?> { ["min-priority"] = 5 }, CancellationToken.None);

        answer.Rows.Should().HaveCount(1);
        Convert.ToInt64(answer.Rows[0].Values["count"]).Should().Be(2, "probes p2 (5) and p3 (9) have priority >= 5");
    }

    [Fact]
    public async Task A_missing_parameter_value_refuses_with_the_required_names()
    {
        Analytics.Question<AnalyticsProbe, string>(QuestionName + "-missing", q => q
            .WithParameter<int>("min-priority")
            .Where(t => t.Priority >= Analytics.P<int>("min-priority"))
            .Count());
        var services = await BootAndSeedAsync();

        var refusal = (await FluentActions.Awaiting(() =>
            Analytics.Of<AnalyticsProbe, string>().Run(QuestionName + "-missing", null, CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>()).Which;

        refusal.Message.Should().Contain("min-priority", "the refusal must name the missing parameter");
    }

    [Fact]
    public async Task An_undeclared_parameter_value_refuses_without_computing()
    {
        Analytics.Question<AnalyticsProbe, string>(QuestionName + "-strict", q => q.Count());
        var services = await BootAndSeedAsync();

        var refusal = (await FluentActions.Awaiting(() =>
            Analytics.Of<AnalyticsProbe, string>().Run(QuestionName + "-strict", new Dictionary<string, object?> { ["bogus"] = 1 }, CancellationToken.None))
            .Should().ThrowAsync<NotSupportedException>()).Which;

        refusal.Message.Should().Contain("bogus", "extra values must be named, not silently dropped");
    }
}
