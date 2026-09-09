using System.Linq;
using AwesomeAssertions;
using Koan.Core.Ordering;
using Koan.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Web.OpenGraph.Tests;

/// <summary>
/// Guards the CORE-0091 ordering declaration: the OpenGraph pillar is a web-layer module and must be
/// initialized after the Koan.Web core module (the convention the other web pillars follow).
/// </summary>
public sealed class OpenGraphModuleOrderingTests
{
    [Fact]
    public void Module_declares_initialization_after_the_web_core()
    {
        var module = typeof(Koan.Web.OpenGraph.Initialization.OpenGraphModule);

        var after = module
            .GetCustomAttributes(typeof(AfterAttribute), inherit: false)
            .Cast<AfterAttribute>()
            .SelectMany(a => a.Targets);

        after.Should().Contain(typeof(Koan.Web.Initialization.WebModule));
    }

    [Fact]
    public void Module_contributes_social_cards_after_authorization()
    {
        var services = new ServiceCollection();
        new Koan.Web.OpenGraph.Initialization.OpenGraphModule().Register(services);

        using var provider = services.BuildServiceProvider();
        var contributor = provider.GetServices<IKoanWebPipelineContributor>().Should().ContainSingle().Subject;

        contributor.Stage.Should().Be(KoanWebPipelineStage.AfterAuthorization);
    }
}
