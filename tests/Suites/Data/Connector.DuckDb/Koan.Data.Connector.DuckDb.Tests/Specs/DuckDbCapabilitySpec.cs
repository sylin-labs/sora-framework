using Koan.Core;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Connector.DuckDb.Tests.Specs;

/// <summary>
/// The delight pass (DUCK-1): files as tables (Parquet glob, hive partitioning, CSV sniff — the
/// engine's native superpower, declared), extensions as configuration (declared allow-list,
/// fail-closed), the read-only posture (read yes, write refuses, missing never creates), and file
/// hygiene as evidence (a clean stop leaves no WAL — "back up the app = copy the file").
/// </summary>
public sealed class DuckDbCapabilitySpec
{
    [Theory]
    [InlineData("")]
    [InlineData("Engine:")]
    public async Task Declared_extension_options_bind_even_with_a_warm_extension_cache(string prefix)
    {
        await using var host = await Boot(Path.Combine(TempDir("options"), "main.duckdb"))
            .WithSetting($"Koan:Data:DuckDb:{prefix}AutoInstallExtensions", "true")
            .WithSetting($"Koan:Data:DuckDb:{prefix}ExtensionDirectory", ".koan/test-extensions")
            .StartAsync();
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DuckDbOptions>>().Value;
        options.AutoInstallExtensions.Should().BeTrue();
        options.ExtensionDirectory.Should().Be(".koan/test-extensions");
    }

    private static string TempDir(string label) =>
        Path.Combine(Path.GetTempPath(), $"koan-duckdb-cap-{label}-{Guid.CreateVersion7():n}");

    private static KoanIntegrationHost.Builder Boot(string dataPath, string? extraKey = null, string? extraValue = null) =>
        KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "duckdb")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", $"Data Source={dataPath}")
            .WithSetting(extraKey ?? "Koan:Data:Unused", extraValue ?? "")
            .ConfigureServices(services => services.AddKoan());

    private sealed class CapProbe : Entity<CapProbe>
    {
        public string Value { get; set; } = "";
    }

    [Fact]
    public async Task Files_are_tables_parquet_glob_hive_and_csv()
    {
        var dir = TempDir("files");
        Directory.CreateDirectory(dir);
        await using var host = await Boot(Path.Combine(dir, "main.duckdb")).StartAsync();
        var direct = host.Services.GetRequiredService<IDataService>().Direct(adapter: "duckdb");

        // Two parquet files through the engine's own COPY, then a glob read across both.
        await direct.Execute($"COPY (SELECT 1 AS id, 'a' AS name UNION ALL SELECT 2, 'b') TO '{dir.Replace("\\", "/")}/events-1.parquet' (FORMAT PARQUET)");
        await direct.Execute($"COPY (SELECT 3 AS id, 'c' AS name UNION ALL SELECT 4, 'd') TO '{dir.Replace("\\", "/")}/events-2.parquet' (FORMAT PARQUET)");
        (await direct.Scalar<long>($"SELECT COUNT(*) FROM '{dir.Replace("\\", "/")}/events-*.parquet'"))
            .Should().Be(4, "a glob reads every matching file as one table");

        // Hive partitioning: the year lives in the directory, not the file.
        var hiveDir = Path.Combine(dir, "hive", "year=2026");
        Directory.CreateDirectory(hiveDir);
        await direct.Execute($"COPY (SELECT 7 AS id) TO '{hiveDir.Replace("\\", "/")}/data.parquet' (FORMAT PARQUET)");
        (await direct.Scalar<long>($"SELECT DISTINCT year FROM read_parquet('{dir.Replace("\\", "/")}/hive/*/*.parquet', hive_partitioning=true)"))
            .Should().Be(2026, "directory partitions become columns");

        // CSV: schema sniffing needs nothing but the file.
        var csv = Path.Combine(dir, "people.csv");
        await File.WriteAllTextAsync(csv, "name,age\nada,36\ngrace,45\n");
        (await direct.Scalar<long>($"SELECT COUNT(*) FROM '{csv.Replace("\\", "/")}'"))
            .Should().Be(2, "a CSV file is a table with typed columns");
    }

    [Fact]
    public async Task Declared_extensions_load_and_attach_sqlite()
    {
        var dir = TempDir("ext");
        Directory.CreateDirectory(dir);
        // sqlite_scanner is not statically bundled: the declaration pairs with an explicit
        // auto-install consent (the air-gap posture stays opt-in, per the options' default).
        await using var host = await Boot(
            Path.Combine(dir, "main.duckdb"),
            "Koan:Data:DuckDb:Extensions:0", "sqlite_scanner")
            .WithSetting("Koan:Data:DuckDb:AutoInstallExtensions", "true")
            .StartAsync();
        // The declared sqlite extension makes foreign stores addressable. ATTACH is connection-scoped,
        // so the build and the read share one transaction-scope connection — exactly the shape real
        // foreign-store work takes.
        var direct = host.Services.GetRequiredService<IDataService>().Direct(adapter: "duckdb");
        var legacy = Path.Combine(dir, "legacy.db").Replace("\\", "/");
        await using var tx = direct.Begin();
        await tx.Execute($"ATTACH '{legacy}' AS legacy (TYPE sqlite); CREATE TABLE legacy.items (v INTEGER); INSERT INTO legacy.items VALUES (41), (42);");
        (await tx.Scalar<long>("SELECT COUNT(*) FROM legacy.items"))
            .Should().Be(2, "the declared sqlite extension makes foreign stores addressable in scope");
    }

    [Fact]
    public async Task An_undeclarable_extension_refuses_with_the_name()
    {
        var dir = TempDir("ext-bogus");
        Directory.CreateDirectory(dir);
        await using var host = await Boot(
            Path.Combine(dir, "main.duckdb"),
            "Koan:Data:DuckDb:Extensions:0", "nosuchextension").StartAsync();

        var refusal = (await FluentActions.Invoking(async () =>
                await host.Services.GetRequiredService<IDataService>().Direct(adapter: "duckdb").Scalar<long>("SELECT 1"))
            .Should().ThrowAsync<InvalidOperationException>()).Which;

        refusal.Message.Should().Contain("nosuchextension",
            "the refusal names the extension that could not load");
        refusal.Message.Should().Contain("AutoInstallExtensions",
            "and the corrective offers the pre-install / autoinstall choice");
    }

    [Fact]
    public async Task ReadOnly_mode_reads_but_never_writes_and_never_creates()
    {
        var dir = TempDir("ro");
        Directory.CreateDirectory(dir);
        var dataPath = Path.Combine(dir, "store.duckdb");

        await using (var writer = await Boot(dataPath).StartAsync())
        {
            await new CapProbe { Value = "kept" }.Save();
        }

        // The write host must be fully closed first: DuckDB is single-writer per file, and even a
        // read-only open from another engine instance is excluded while a writer holds it.
        await using (var reader = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "duckdb")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", $"Data Source={dataPath};Mode=ReadOnly")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync())
        {
            (await CapProbe.All(CancellationToken.None)).Should().Contain(p => p.Value == "kept",
                "a read-only open reads the store it was pointed at");
            var writeError = (await FluentActions.Invoking(async () => await new CapProbe { Value = "rejected" }.Save())
                .Should().ThrowAsync<Exception>()).Which;
            writeError.Message.ToLowerInvariant().Should().Contain("read",
                "writes refuse in read-only mode (the engine names read-only or a read-only transaction)");
        }

        var missing = Path.Combine(dir, "absent.duckdb");
        await using (var creator = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "duckdb")
            .WithSetting("Koan:Data:Sources:Default:ConnectionString", $"Data Source={missing};Mode=ReadOnly")
            .ConfigureServices(services => services.AddKoan())
            .StartAsync())
        {
            await FluentActions.Invoking(async () => await new CapProbe { Value = "no" }.Save())
                .Should().ThrowAsync<Exception>("a read-only open never creates a store");
            File.Exists(missing).Should().BeFalse("read-only mode never creates the file");
        }
    }

    [Fact]
    public async Task A_clean_stop_leaves_no_wal_behind()
    {
        var dir = TempDir("wal");
        Directory.CreateDirectory(dir);
        var dataPath = Path.Combine(dir, "store.duckdb");

        await using (var host = await Boot(dataPath).StartAsync())
        {
            await new CapProbe { Value = "persisted" }.Save();
        }

        File.Exists(dataPath).Should().BeTrue("the store survives the host");
        File.Exists(dataPath + ".wal").Should().BeFalse(
            "pooling keys are dropped and connections close per operation, so the engine checkpoints on the last close - backup is a file copy");
    }
}
