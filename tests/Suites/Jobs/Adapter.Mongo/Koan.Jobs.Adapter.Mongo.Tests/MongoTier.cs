using Koan.Jobs;
using Koan.Jobs.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;
using Xunit;

namespace Koan.Jobs.Adapter.Mongo.Tests;

/// <summary>Starts a MongoDB container once for the class and exposes the Koan data-source settings.</summary>
public sealed class MongoJobsFixture : IAsyncLifetime
{
    private MongoDbContainer? _container;
    public IReadOnlyDictionary<string, string?> Settings { get; private set; } = new Dictionary<string, string?>();

    public async ValueTask InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("Koan_MONGO__CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(cs))
        {
            _container = new MongoDbBuilder("mongo:8.3.4").Build();
            await _container.StartAsync();
            cs = _container.GetConnectionString();
        }
        Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Koan:Environment"] = "Test",
            ["Koan:Data:Sources:Default:Adapter"] = "mongo",
            ["Koan:Data:Sources:Default:ConnectionString"] = cs,
            ["Koan:Data:Sources:Default:Database"] = "koan_jobs",
            // The Mongo client provider / readiness probe resolves the adapter-specific keys.
            ["Koan:Data:Mongo:ConnectionString"] = cs,
            ["Koan:Data:Mongo:Database"] = "koan_jobs",
            // Testcontainers already waits for the container to be up; Koan's per-boot readiness gating
            // is redundant here and, across 28 rapid host boot/dispose cycles, occasionally exceeds its
            // 30s window. Disable it so each test connects directly.
            ["Koan:Data:Mongo:Readiness:EnableReadinessGating"] = "false",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

/// <summary>Runs the shared <see cref="JobBehaviorSuite"/> on a real MongoDB store (the document-store tier).</summary>
public sealed class MongoBehaviors : JobBehaviorSuite, IClassFixture<MongoJobsFixture>
{
    private readonly MongoJobsFixture _fx;
    public MongoBehaviors(MongoJobsFixture fx) => _fx = fx;

    protected override Task<JobsHarness> CreateHostAsync(Action<JobsOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
        => JobsHarness.StartWithSettingsAsync(_fx.Settings, configure, configureServices);
}
