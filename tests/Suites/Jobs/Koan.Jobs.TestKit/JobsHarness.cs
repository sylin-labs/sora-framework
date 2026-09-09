using System.IO;
using Koan.Core;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Jobs;
using Koan.Jobs.Testing;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Koan.Jobs.TestKit;

/// <summary>
/// One deterministic test harness for every tier. A real Koan app (AddKoan reflective discovery) with the data
/// adapter the consuming project references, a <see cref="FakeTimeProvider"/> as the clock, and the background
/// worker disabled so the test drives the orchestrator/scheduler explicitly. The same harness API powers the
/// shared <see cref="JobBehaviorSuite"/> on in-memory and on each durable adapter.
/// </summary>
public sealed class JobsHarness : IAsyncDisposable
{
    private readonly IntegrationHost _host;
    private readonly string? _dbPath;

    private JobsHarness(IntegrationHost host, FakeTimeProvider clock, string? dbPath)
    {
        _host = host;
        _dbPath = dbPath;
        Clock = clock;
        Driver = JobsTestDriver.From(host.Services);
        Coordinator = host.Services.GetRequiredService<IJobCoordinator>();
        Ledger = host.Services.GetRequiredService<IJobLedger>();
        Registry = host.Services.GetRequiredService<JobTypeRegistry>();
    }

    public FakeTimeProvider Clock { get; }
    public IServiceProvider Services => _host.Services;
    public JobsTestDriver Driver { get; }
    public IJobCoordinator Coordinator { get; }
    public IJobLedger Ledger { get; }
    internal JobTypeRegistry Registry { get; }

    /// <summary>In-memory tier — uses whatever (non-durable) data adapter the consuming project references.</summary>
    public static Task<JobsHarness> StartInMemoryAsync(Action<JobsOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
        => StartCore(configure, null, null, configureServices: configureServices);

    /// <summary>SQLite durable tier — a temp-file database (the consuming project references the SQLite connector).</summary>
    public static Task<JobsHarness> StartSqliteAsync(Action<JobsOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"koan-jobs-{Guid.NewGuid():n}.db");
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Koan:Environment"] = "Test",
            ["Koan:Data:Sources:Default:Adapter"] = "sqlite",
            ["Koan:Data:Sources:Default:ConnectionString"] = SqliteConnectionString(dbPath),
        };
        return StartCore(configure, settings, dbPath, configureServices: configureServices);
    }

    /// <summary>Generic durable tier — caller supplies the data-source settings (Mongo/Postgres/SqlServer).</summary>
    public static Task<JobsHarness> StartWithSettingsAsync(IReadOnlyDictionary<string, string?> settings, Action<JobsOptions>? configure = null, Action<IServiceCollection>? configureServices = null, bool clearOnStart = true)
        => StartCore(configure, settings, null, clearOnStart, configureServices);

    /// <summary>SQLite against a specific db file (for crash/restart tests). <paramref name="clearOnStart"/> false =
    /// reuse the existing data (a "reboot"); <paramref name="ownsDb"/> false = leave the file for the test to manage.</summary>
    public static Task<JobsHarness> StartSqliteAtAsync(string dbPath, bool clearOnStart, bool ownsDb, Action<JobsOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Koan:Environment"] = "Test",
            ["Koan:Data:Sources:Default:Adapter"] = "sqlite",
            ["Koan:Data:Sources:Default:ConnectionString"] = SqliteConnectionString(dbPath),
        };
        return StartCore(configure, settings, ownsDb ? dbPath : null, clearOnStart, configureServices);
    }

    private static async Task<JobsHarness> StartCore(Action<JobsOptions>? configure, IReadOnlyDictionary<string, string?>? settings, string? dbPath, bool clearOnStart = true, Action<IServiceCollection>? configureServices = null)
    {
        // Mongo's TTL monitor uses wall time. Start ahead of it so terminal records survive until
        // the test explicitly drives archival; an old fixed date races the server's background expiry.
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero));
        var builder = KoanIntegrationHost.Configure();
        if (settings is not null) builder = builder.WithSettings(settings);
        var host = builder
            .ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddKoan();
                s.Configure<JobsOptions>(o =>
                {
                    o.EnableWorker = false;
                    o.RescheduleJitter = TimeSpan.Zero;
                    configure?.Invoke(o);
                });
                configureServices?.Invoke(s);
            })
            .Build();
        await host.StartAsync();
        if (clearOnStart)
        {
            await EnsureJobSchema();   // framework-defined entities need their tables ensured on some adapters
            // Clean slate between specs. Use RemoveStrategy.Safe (DeleteMany), NOT the default Optimized — on Mongo,
            // Optimized resolves to Fast = drop-and-recreate the collection, and repeating that DDL across the suite's
            // ~40 rapid host cycles on one server churns the catalog/index builds and flakes intermittently. A
            // document delete has no DDL and is reliable on every tier.
            await JobRecord.RemoveAll(RemoveStrategy.Safe);
            await JobGateRecord.RemoveAll(RemoveStrategy.Safe);
            await JobMetric.RemoveAll(RemoveStrategy.Safe);
        }
        return new JobsHarness(host, clock, dbPath);
    }

    private static string SqliteConnectionString(string dbPath)
        => $"Data Source={dbPath};Pooling=False";

    private static async Task EnsureJobSchema()
    {
        var ensure = new Instruction(DataInstructions.EnsureCreated);
        try { await Data<JobRecord, string>.Execute<object?>(ensure); } catch { /* no-op on adapters without schema */ }
        try { await Data<JobGateRecord, string>.Execute<object?>(ensure); } catch { }
        try { await Data<JobMetric, string>.Execute<object?>(ensure); } catch { }
    }

    /// <summary>In-process stage-handler executor. Runs exactly one stage per call through the real orchestration path.</summary>
    public JobStagePilot Pilot => new(this);

    public void Advance(TimeSpan by) => Clock.Advance(by);
    public Task Drain(CancellationToken ct = default) => Driver.DrainAsync(ct);
    public Task TriggerDue(CancellationToken ct = default) => Driver.TriggerDueAsync(ct);
    public Task Boot(CancellationToken ct = default) => Driver.SubmitBootAsync(ct);
    public Task Reap(CancellationToken ct = default) => Driver.ReapAsync(ct);
    public Task<int> Archive(CancellationToken ct = default) => Driver.ArchiveAsync(ct);
    public Task FlushMetrics(CancellationToken ct = default) => Driver.FlushMetricsAsync(ct);

    public Task<JobStatus?> StatusOf<T>(string workId) where T : Entity<T>, IKoanJob<T>
        => Coordinator.StatusAsync(typeof(T).FullName!, workId, default);

    public async Task<JobRecord?> JobFor<T>(string workId) where T : Entity<T>, IKoanJob<T>
    {
        var jobs = await Ledger.Query(new JobQuery(WorkType: typeof(T).FullName!, WorkId: workId), default);
        return jobs.OrderByDescending(j => j.FirstSubmittedAt).FirstOrDefault();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        if (_dbPath is not null && File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
