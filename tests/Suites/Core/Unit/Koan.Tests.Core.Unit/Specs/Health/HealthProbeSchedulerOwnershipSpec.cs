using Koan.Core;
using Koan.Core.BackgroundServices;
using Koan.Core.Logging;
using Koan.Core.Observability.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Koan.Tests.Core.Unit.Specs.Health;

[Collection(HealthProbeSchedulerOwnershipCollection.Name)]
public sealed class HealthProbeSchedulerOwnershipSpec
{
    private const string SchedulerLogAction = "health.scheduler";
    private const string StartedOutcome = "started";
    private const string StoppedOutcome = "stopped";

    [Fact]
    public void AddKoan_composes_the_scheduler_under_one_hosted_lifecycle_owner()
    {
        var services = CreateServices(out _);

        services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                     && descriptor.ImplementationType == typeof(HealthProbeScheduler))
            .Should().BeEmpty("the Koan background-service orchestrator owns scheduler execution");
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().OfType<KoanBackgroundServiceOrchestrator>()
            .Should().ContainSingle().Which.Should()
            .BeSameAs(provider.GetRequiredService<KoanBackgroundServiceOrchestrator>());
        var scheduler = provider.GetRequiredService<HealthProbeScheduler>();
        provider.GetServices<IKoanBackgroundService>()
            .Where(service => service is HealthProbeScheduler)
            .Should().ContainSingle()
            .Which.Should().BeSameAs(scheduler);
        provider.GetServices<IKoanPokableService>()
            .Where(service => service is HealthProbeScheduler)
            .Should().ContainSingle()
            .Which.Should().BeSameAs(scheduler);
        provider.GetServices<IHealthContributor>()
            .Where(service => service is HealthProbeScheduler)
            .Should().ContainSingle()
            .Which.Should().BeSameAs(scheduler);
    }

    [Fact]
    public async Task AddKoan_host_runs_one_scheduler_loop_from_start_through_stop()
    {
        var starts = 0;
        var stops = 0;
        KoanLog.TestSink = (_, _, action, outcome, _) =>
        {
            if (!string.Equals(action, SchedulerLogAction, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(outcome, StartedOutcome, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref starts);
            }
            else if (string.Equals(outcome, StoppedOutcome, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref stops);
            }
        };

        try
        {
            var services = CreateServices(out var lifetime);
            using var provider = services.BuildServiceProvider();
            var hostedServices = provider.GetServices<IHostedService>().ToList();

            foreach (var service in hostedServices)
            {
                await service.StartAsync(CancellationToken.None);
            }
            lifetime.SignalStarted();
            await WaitUntilAsync(() => Volatile.Read(ref starts) > 0);

            lifetime.SignalStopping();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            for (var index = hostedServices.Count - 1; index >= 0; index--)
            {
                await hostedServices[index].StopAsync(shutdown.Token);
            }
            lifetime.SignalStopped();
            await WaitUntilAsync(() => Volatile.Read(ref stops) >= Volatile.Read(ref starts));

            shutdown.IsCancellationRequested.Should().BeFalse(
                "the cancellation-aware scheduler must finish within graceful shutdown");
            Volatile.Read(ref starts).Should().Be(1);
            Volatile.Read(ref stops).Should().Be(1);
        }
        finally
        {
            KoanLog.TestSink = null;
        }
    }

    private static ServiceCollection CreateServices(out TestHostApplicationLifetime lifetime)
    {
        lifetime = new TestHostApplicationLifetime();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddKoan();
        return services;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => SignalStopping();
        public void SignalStarted() => _started.Cancel();
        public void SignalStopping() => _stopping.Cancel();
        public void SignalStopped() => _stopped.Cancel();
    }
}
