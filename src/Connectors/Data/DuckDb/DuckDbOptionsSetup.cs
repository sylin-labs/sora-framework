using Koan.Data.Abstractions.Naming;
using Koan.Data.Connector.DuckDb.Infrastructure;
using Koan.Data.Relational.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Koan.Data.Connector.DuckDb;

internal sealed class DuckDbOptionsSetup(IConfiguration configuration) : IConfigureOptions<DuckDbOptions>
{
    public void Configure(DuckDbOptions options)
    {
        var owner = configuration["Koan:Data:Sources:Default:Adapter"];
        var ownsDefault = string.IsNullOrWhiteSpace(owner) || DuckDbAdapterFactory.HandlesProvider(owner);
        var requested = ownsDefault
            ? First(
                Constants.Configuration.Keys.DefaultSourceConnectionString,
                Constants.Configuration.Keys.ProviderSourceConnectionString,
                Constants.Configuration.Keys.ConnectionString,
                Constants.Configuration.Keys.ConnectionStringsDuckDb,
                Constants.Configuration.Keys.ConnectionStringsDefault)
            : First(
                Constants.Configuration.Keys.ProviderSourceConnectionString,
                Constants.Configuration.Keys.ConnectionString,
                Constants.Configuration.Keys.ConnectionStringsDuckDb);

        var candidate = requested ?? options.ConnectionString;
        options.ConnectionString = IsAuto(candidate)
            ? Constants.DefaultConnection
            : candidate.Trim();
        options.NamingStyle = ReadEnum(options.NamingStyle,
            Constants.Configuration.Keys.ProviderNamingStyle,
            Constants.Configuration.Keys.NamingStyle);
        options.Separator = First(
            Constants.Configuration.Keys.ProviderSeparator,
            Constants.Configuration.Keys.Separator) ?? options.Separator;
        options.DdlPolicy = ReadEnum(options.DdlPolicy,
            Constants.Configuration.Keys.ProviderDdlPolicy,
            Constants.Configuration.Keys.DdlPolicy);
        options.SchemaMatching = ReadEnum(options.SchemaMatching,
            Constants.Configuration.Keys.ProviderSchemaMatching,
            Constants.Configuration.Keys.SchemaMatching);
        options.AllowProductionDdl = options.DdlPolicy == RelationalDdlPolicy.AutoCreate;

        // Engine settings: the record-store path and the materialization sink both derive their engine
        // instances from these, so they bind from the provider section (scalar or nested Engine: form).
        options.MemoryLimit = First(
            "Koan:Data:DuckDb:MemoryLimit",
            "Koan:Data:DuckDb:Engine:MemoryLimit") ?? options.MemoryLimit;
        if (int.TryParse(First(
                "Koan:Data:DuckDb:Threads",
                "Koan:Data:DuckDb:Engine:Threads"), out var threads))
            options.Threads = threads;
        var extensions = ReadList(
            "Koan:Data:DuckDb:Extensions",
            "Koan:Data:DuckDb:Engine:Extensions");
        if (extensions is { Count: > 0 }) options.Extensions = extensions;
        options.ExtensionDirectory = First(Constants.Configuration.Keys.ExtensionDirectory,
            Constants.Configuration.Keys.EngineExtensionDirectory) ?? options.ExtensionDirectory;
        var autoInstall = First(Constants.Configuration.Keys.AutoInstallExtensions,
            Constants.Configuration.Keys.EngineAutoInstallExtensions);
        if (autoInstall is not null)
        {
            if (!bool.TryParse(autoInstall, out var enabled))
                throw new OptionsValidationException(nameof(DuckDbOptions), typeof(DuckDbOptions),
                    [$"{Constants.Configuration.Keys.AutoInstallExtensions} must be true or false."]);
            options.AutoInstallExtensions = enabled;
        }
    }

    /// <summary>A list option binds either from a comma-separated scalar or from indexed children.</summary>
    private List<string>? ReadList(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (configuration[key] is { } scalar && !string.IsNullOrWhiteSpace(scalar))
                return scalar.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                             .ToList();
            var section = configuration.GetSection(key);
            if (!section.Exists()) continue;
            var values = section.GetChildren()
                .Select(static child => child.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim())
                .ToList();
            if (values.Count > 0) return values;
        }
        return null;
    }

    private T ReadEnum<T>(T fallback, params string[] keys) where T : struct, Enum =>
        Enum.TryParse<T>(First(keys), true, out var value) ? value : fallback;

    private string? First(params string[] keys)
    {
        foreach (var key in keys)
            if (configuration[key] is { } value && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static bool IsAuto(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
}
