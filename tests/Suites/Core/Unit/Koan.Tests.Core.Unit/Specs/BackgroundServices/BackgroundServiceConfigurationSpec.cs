using Koan.Core;
using Koan.Core.BackgroundServices;
using Koan.Tests.Core.Unit.Specs.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Koan.Tests.Core.Unit.Specs.BackgroundServices;

[Collection(HealthProbeSchedulerOwnershipCollection.Name)]
public sealed class BackgroundServiceConfigurationSpec
{
    [Fact]
    public async Task Configured_disabled_services_do_not_execute_after_AddKoan()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Koan:BackgroundServices:Enabled"] = "false",
            ["Koan:BackgroundServices:StartupTimeoutSeconds"] = "17",
            ["Koan:BackgroundServices:Services:probe:Enabled"] = "false"
        });
        var probe = new Probe();
        builder.Services.AddSingleton<IKoanBackgroundService>(probe);
        builder.Services.AddKoan();
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<KoanBackgroundServiceOptions>>().Value;
        options.Enabled.Should().BeFalse();
        options.StartupTimeoutSeconds.Should().Be(17);
        options.Services["probe"].Enabled.Should().BeFalse();
        await host.StartAsync();
        var orchestrator = host.Services.GetRequiredService<KoanBackgroundServiceOrchestrator>();
        host.Services.GetServices<IHostedService>().OfType<KoanBackgroundServiceOrchestrator>()
            .Should().ContainSingle().Which.Should().BeSameAs(orchestrator);
        await orchestrator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        probe.Calls.Should().Be(0);
        await host.StopAsync();
    }

    private sealed class Probe : IKoanBackgroundService
    {
        public string Name => "probe";
        public int Calls { get; private set; }
        public Task Execute(CancellationToken ct) { Calls++; return Task.CompletedTask; }
    }
}
