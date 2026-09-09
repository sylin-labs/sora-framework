using Koan.Core;
using Koan.Core.Hosting.Bootstrap;
using Koan.Core.Modules;
using Koan.Core.Provenance;
using Koan.Data.Analytics.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Analytics.Initialization;

public sealed class AnalyticsModule : KoanModule
{
    public override void Register(IServiceCollection services)
    {
        services.AddKoanOptions<AnalyticsOptions>(Constants.Section);
        services.AddSingleton<Runtime.AnalyticsProjectionRefresher>();
        services.AddHostedService<Runtime.AnalyticsProjectionRefreshLoop>();
    }

    public override Task Start(IServiceProvider services, CancellationToken ct)
    {
        // The composition gate — enforced only when the application actually uses the grammar. The module
        // travels transitively (the connectors implement the analytics composer contract), so mere
        // presence proves nothing; a DECLARED question with no elected engine is what must refuse, and
        // the refusal names the exact package that provides one (DATA-0123).
        if (AnalyticsCatalog.Count == 0) return Task.CompletedTask;

        var engines = services.GetServices<IAnalyticsEngine>().ToList();
        if (engines.Count == 0)
            throw new InvalidOperationException(
                "Analytics questions are declared, but no analytics engine is elected. Reference an engine " +
                "connector — Sylin.Koan.Data.Connector.DuckDb (plus Sylin.Koan.Data.Connector.DuckDb.Native for " +
                "the engine binary) is the reference engine — so declared questions have a substrate.");
        return Task.CompletedTask;
    }

    public override void Report(ProvenanceModuleWriter module, IConfiguration configuration, IHostEnvironment environment)
    {
        module.Describe(Version);
        module.AddNote("Analytics declares named questions over entities; the catalog is served at /analytics/catalog and to agents as analytics.list_questions / analytics.ask.");
    }
}
