using Microsoft.Extensions.DependencyInjection;
using Koan.Core.Hosting.App;
using Koan.Web.AdapterSurface.InMemory.Tests.PredicateHook;
using Koan.Web.AdapterSurface.InMemory.Tests.RelationshipExpansion;
using Koan.Web.AdapterSurface.TestKit;
using Koan.Web.Extensions;
using Koan.Web.Hooks;
using Koan.Web.Endpoints;

namespace Koan.Web.AdapterSurface.InMemory.Tests;

public sealed class InMemoryAdapterFactory : AdapterTestFactoryBase
{
    public override bool IsAvailable => true;
    protected override string HostEnvironment => "Test";

    // The PredicateHook specs exercise VisibilityWidgetController + VisibilityHook (declared in this
    // test assembly). The direct host doesn't scan the entry assembly the way the old WAF host did, so
    // register both explicitly.
    protected override void ConfigureAdditionalServices(IServiceCollection services)
    {
        services.AddKoanControllersFrom<VisibilityWidgetController>();
        services.AddSingleton<IRequestOptionsHook<VisibilityWidget>, VisibilityHook>();
        services.AddSingleton<IModelHook<VisibilityWidget>, StoppingModelHook>();

        // AN-leak: relationship-expansion visibility surface (Maker [parent] / Work [child] with two
        // divergent edges to the same target). The Work/Maker hooks wall non-public rows so the
        // governed-expansion specs can prove ?with=all honors each related type's own predicates.
        services.AddKoanControllersFrom<MakersController>();
        services.AddKoanControllersFrom<WorksController>();
        services.AddSingleton<IRequestOptionsHook<Maker>, MakerVisibilityHook>();
        services.AddSingleton<IRequestOptionsHook<Work>, WorkVisibilityHook>();
        services.Configure<EntityEndpointOptions>(options => options.RelationshipMaxResults = 2);
    }

    protected override IEnumerable<KeyValuePair<string, string?>> AdapterConfiguration() => new Dictionary<string, string?>
    {
        ["Koan:Environment"] = "Test",
        ["Koan:Data:Sources:Default:Adapter"] = "inmemory",
        ["Koan:Data:Sources:Default:ConnectionString"] = "memory://adapter-surface",
        ["Koan:BackgroundServices:Enabled"] = "false",
        ["Logging:LogLevel:Default"] = "Warning",
    };

    public override async Task ResetAsync()
    {
        await Widget.RemoveAll();
    }
}
