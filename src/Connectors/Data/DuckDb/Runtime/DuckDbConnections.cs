using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Koan.Core.Orchestration;
using Koan.Data.Connector.DuckDb.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koan.Data.Connector.DuckDb.Runtime;

/// <summary>
/// Connection management for the embedded engine. DuckDB instances are shared per (path + config)
/// within the process — two connections on one normalized string join one engine — so every operation
/// on a source must use byte-identical connection strings. Engine settings from
/// <see cref="DuckDbOptions"/> are folded in here for exactly that reason: they are part of the
/// instance key, and options applied only on some connections would fork the engine in two.
/// </summary>
internal sealed class DuckDbConnections : IDisposable, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string _host = Guid.CreateVersion7().ToString("n");
    private readonly string? _contentRoot;
    private readonly IOptions<DuckDbOptions> _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, DuckDBConnection> _keepers = new(StringComparer.Ordinal);
    private bool _disposed;

    public DuckDbConnections(ILogger<DuckDbConnections> logger, IHostEnvironment? environment, IOptions<DuckDbOptions> settings)
    {
        _logger = logger;
        _contentRoot = environment?.ContentRootPath;
        _settings = settings;
    }

    /// <summary>
    /// Anchors a relative Data Source to the application's content root. Auto-created databases must
    /// live inside the application's own scope — resolving them against whatever directory a process
    /// happened to start from makes unrelated applications share (or fight over) one store.
    /// </summary>
    internal string AnchorDataSource(string dataSource)
        => Path.IsPathRooted(dataSource) || string.IsNullOrWhiteSpace(_contentRoot)
            ? Path.GetFullPath(dataSource)
            : Path.GetFullPath(Path.Combine(_contentRoot, dataSource));

    /// <summary>
    /// Opens a connection on the source's single normalized instance string. When
    /// <paramref name="nonCreating"/> is set, a file-backed source that does not exist yet refuses here:
    /// a look must never become a write, and DuckDB creates missing files on open.
    /// </summary>
    public DuckDBConnection Create(string connectionString, string source, bool nonCreating = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var normalized = Normalize(connectionString, source);
            if (nonCreating && !Exists(normalized))
                throw new FileNotFoundException(
                    "The DuckDB database does not exist yet and the caller declined creation.", normalized);
            var extensions = _settings.Value.Extensions;
            if (extensions is { Count: > 0 })
                return new ExtensionLoadingConnection(normalized, [.. extensions], _settings.Value.AutoInstallExtensions);
            return Validated(normalized);
        }
    }

    public void PrepareManaged(string connectionString)
    {
        var path = DataSourceOf(connectionString);
        if (string.IsNullOrWhiteSpace(path) || IsMemory(path)) return;
        var fullPath = AnchorDataSource(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    /// <summary>The connection-string's file path, resolved but not anchored. Empty for in-memory sources.</summary>
    internal string DataSourceOf(string connectionString)
    {
        foreach (var part in SplitSegments(connectionString))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DataSource", StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..].Trim();
        }
        return string.Empty;
    }

    internal (string Path, bool IsMemory) DescribeSource(string connectionString)
    {
        var path = DataSourceOf(connectionString);
        return (path, IsMemory(path));
    }

    private string Normalize(string connectionString, string source)
    {
        var (stripped, modeMemory, readOnly) = StripSqliteOnlyKeys(connectionString);
        var path = DataSourceOf(stripped);
        var settings = _settings.Value;
        var normalized = IsMemory(path) || modeMemory ? Memory(stripped, source) : WithAnchoredPath(stripped, path);

        // Engine settings ride the instance key (see type comment), so they are appended to every string.
        var config = EngineConfig(settings, readOnly);
        return config.Length == 0 ? normalized : normalized + ";" + config;
    }

    private string WithAnchoredPath(string connectionString, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsUri(path)) return connectionString;
        var anchored = AnchorDataSource(path);
        return connectionString.Replace(path, anchored, StringComparison.OrdinalIgnoreCase);
    }

    private string EngineConfig(DuckDbOptions settings, bool readOnly)
    {
        var parts = new List<string>(4);
        if (readOnly) parts.Add("access_mode=read_only");
        if (!string.IsNullOrWhiteSpace(settings.MemoryLimit)) parts.Add($"memory_limit={settings.MemoryLimit}");
        if (settings.Threads is { } threads) parts.Add($"threads={threads}");
        if (!string.IsNullOrWhiteSpace(settings.ExtensionDirectory)) parts.Add($"extension_directory={settings.ExtensionDirectory}");
        if (!settings.AutoInstallExtensions)
        {
            // Runtime downloads of extension binaries are a supply-chain and air-gap decision, not a
            // default (DATA-0123). Pre-install and point extension_directory at them instead.
            parts.Add("autoinstall_known_extensions=false");
            parts.Add("autoload_known_extensions=false");
        }
        return string.Join(";", parts);
    }

    /// <summary>
    /// In-memory sources live in an ephemeral, host-private scratch file. DuckDB's true in-memory stores
    /// are connection-private — a second connection sees an empty database — which breaks the usual
    /// open-per-operation pattern. A scratch file under the content root gives the same observable
    /// lifetime (created on first use, deleted with the host) while letting every operation join one
    /// engine. Keyed per source, never shared across hosts.
    /// </summary>
    private string Memory(string requested, string source)
    {
        var key = source + "\n" + requested;
        if (!_keepers.ContainsKey(key) && _keepers.Count >= Constants.MaximumMemorySources)
            throw new InvalidOperationException(
                $"DuckDB reached the host bound of {Constants.MaximumMemorySources} in-memory sources. " +
                "Reduce routed sources or use file-backed storage.");
        var name = "koan-" + Fingerprint(_host + "\n" + key);
        var scratch = Path.Combine(
            string.IsNullOrWhiteSpace(_contentRoot) ? Path.GetTempPath() : _contentRoot,
            ".koan", "tmp", "duckdb", name + ".duckdb");
        var directory = Path.GetDirectoryName(scratch);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var normalized = $"Data Source={scratch}";
        if (!_keepers.ContainsKey(key))
        {
            var keeper = new DuckDBConnection(normalized);
            try { keeper.Open(); }
            catch { keeper.Dispose(); throw; }
            _keepers.Add(key, keeper);
        }
        return normalized;
    }

    private static bool IsMemory(string path) =>
        string.IsNullOrWhiteSpace(path) ||
        path.Equals(":memory:", StringComparison.OrdinalIgnoreCase);

    private static bool IsUri(string value) => value.Contains("://", StringComparison.Ordinal);

    /// <summary>
    /// SQLite-shaped keys the engine does not know: their meaning is carried by this class instead.
    /// <c>Mode=Memory</c> selects the per-source scratch store; <c>Cache</c> and <c>Pooling</c> are
    /// dropped (instance sharing here is per-path, and prepared statements are pooled by the engine).
    /// Segments split on <c>;</c> outside double quotes, so a quoted value that contains a separator —
    /// a secret, most commonly — stays whole and can only ever reach the engine's own validator.
    /// </summary>
    private static (string ConnectionString, bool ModeMemory, bool ReadOnly) StripSqliteOnlyKeys(string connectionString)
    {
        var kept = new List<string>();
        var modeMemory = false;
        var readOnly = false;
        foreach (var part in SplitSegments(connectionString))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) { kept.Add(part); continue; }
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("Memory", StringComparison.OrdinalIgnoreCase)) modeMemory = true;
                else if (value.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)) readOnly = true;
                else kept.Add(part);
                continue;
            }
            if (key.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Pooling", StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(part);
        }
        return (string.Join(';', kept), modeMemory, readOnly);
    }

    /// <summary>
    /// The entity path strips SQLite-shaped local keys before the engine sees a string; the analytics
    /// materialization store builds connections from a raw options string and must apply the same rule.
    /// Engine instances are shared per path within the process — a pooling layer on top is not a cache,
    /// it is a second set of physical connections racing the engine's catalog versioning.
    /// </summary>
    internal static string StripLocalKeys(string connectionString)
    {
        var kept = new List<string>();
        foreach (var part in SplitSegments(connectionString))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
            {
                var key = part[..separator].Trim();
                if (key.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Pooling", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            kept.Add(part);
        }
        return string.Join(';', kept);
    }

    /// <summary>Splits on <c>;</c> outside double quotes; quoted values stay whole.</summary>
    internal static List<string> SplitSegments(string connectionString)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var character in connectionString)
        {
            if (character == '"') { inQuotes = !inQuotes; current.Append(character); }
            else if (character == ';' && !inQuotes)
            {
                if (current.Length > 0) segments.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(character);
        }
        if (current.Length > 0) segments.Add(current.ToString().Trim());
        return segments;
    }

    /// <summary>
    /// Engine-side validation with the same redaction contract as the SQLite sibling: a connection string
    /// the engine refuses is logged de-identified before the failure travels.
    /// </summary>
    private DuckDBConnection Validated(string normalized)
    {
        try
        {
            // The engine's builder is the strictest validator we have: an unknown key (an SQLite-ism that
            // survived, a typo) throws here rather than at open, and the failure is logged de-identified.
            var builder = new DuckDBConnectionStringBuilder();
            foreach (var part in SplitSegments(normalized))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0)
                    throw new InvalidOperationException($"Unrecognized connection string segment '{part}'.");
                builder[part[..separator].Trim()] = part[(separator + 1)..].Trim();
            }
            return new DuckDBConnection(normalized);
        }
        catch (Exception error)
        {
            _logger.LogWarning("duckdb.connection parse-failed connection={Connection} error={Error}",
                Koan.Core.Redaction.DeIdentify(normalized), Koan.Core.Redaction.DeIdentify(error.Message));
            throw;
        }
    }

    /// <summary>
    /// Extensions load PER CONNECTION in DuckDB.NET - a LOAD on one connection does not carry to the
    /// next, even on the same engine instance. A declared allow-list therefore rides every connection
    /// this class hands out: the wrapper loads the names at Open, before any caller statement, and a
    /// load that fails refuses with the extension named and the pre-install/autoinstall choice. A
    /// declared extension that is not there is configuration truth, not a silent skip.
    /// </summary>
    private sealed class ExtensionLoadingConnection : DuckDBConnection
    {
        private readonly string[] _extensions;
        private readonly bool _autoInstall;

        public ExtensionLoadingConnection(string connectionString, string[] extensions, bool autoInstall) : base(connectionString)
        {
            _extensions = extensions;
            _autoInstall = autoInstall;
            foreach (var extension in _extensions)
                if (string.IsNullOrWhiteSpace(extension) || !extension.All(c => char.IsLetterOrDigit(c) || c == '_'))
                    throw new InvalidOperationException(
                        $"DuckDB extension name '{extension}' is not a plain identifier. Extensions are declared as names (sqlite_scanner, httpfs, iceberg), not expressions.");
        }

        public override void Open()
        {
            base.Open();
            try { Load(); }
            catch { Dispose(); throw; }
        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            await base.OpenAsync(cancellationToken).ConfigureAwait(false);
            try { Load(); }
            catch { await DisposeAsync().ConfigureAwait(false); throw; }
        }

        private void Load()
        {
            foreach (var extension in _extensions)
            {
                using var command = CreateCommand();
                try
                {
                    if (_autoInstall)
                    {
                        command.CommandText = $"INSTALL {extension}";
                        command.ExecuteNonQuery();
                    }
                    command.CommandText = $"LOAD {extension}";
                    command.ExecuteNonQuery();
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        $"DuckDB could not load the declared extension '{extension}': {error.Message}. " +
                        "Pre-install the extension and point Koan:Data:DuckDb:ExtensionDirectory at the binaries, " +
                        "or set Koan:Data:DuckDb:AutoInstallExtensions=true to allow runtime downloads (an air-gap decision).",
                        error);
                }
            }
        }
    }

    /// <summary>Whether the source's backing database exists — always true for in-memory sources.</summary>
    private bool Exists(string normalized)
    {
        var path = DataSourceOf(normalized);
        if (string.IsNullOrWhiteSpace(path) || IsMemory(path) || IsUri(path)) return true;
        return File.Exists(path);
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var keeper in _keepers.Values)
            {
                var path = DataSourceOf(keeper.ConnectionString);
                keeper.Dispose();
                if (!string.IsNullOrWhiteSpace(path)) TryDelete(path);
            }
            _keepers.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
