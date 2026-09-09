using FirebirdSql.Data.FirebirdClient;
using Koan.Data.Abstractions;
using Koan.Data.Relational.Orchestration;

namespace Koan.Data.Connector.Firebird.Runtime;

/// <summary>
/// A plain column this store keeps beside the JSON document so a path inside it can still be read as
/// SQL: the mapped scalar the column mirrors (written on every insert from the same encoded value the
/// document carries), or a framework-managed isolation discriminator.
/// </summary>
internal sealed record FirebirdShadowColumn(string Name, Type ClrType, bool Managed);

/// <summary>
/// How Firebird spells a table, a column and its system catalog. Every decision behind these words
/// belongs to the schema orchestrator; this speaks over connections it owns for the duration of one
/// schema operation.
///
/// <para>Firebird has no information_schema. The RDB$ system tables hold catalog truth: relation
/// names in RDB$RELATIONS, columns in RDB$RELATION_FIELDS joined to RDB$FIELDS for the physical
/// type, and key membership in RDB$RELATION_CONSTRAINTS joined to RDB$INDEX_SEGMENTS. Like the
/// SQLite and DuckDB siblings, this store reports which columns exist and declines to compare their
/// definitions — a store type a CLR type cannot see is exactly the comparison it must not fake.</para>
///
/// <para>The database itself is a file on the server. Describing never creates: an absent file
/// refuses the open (isc_io_error, 335544344) and is reported as an unreachable database. Only the
/// provisioning path, which the orchestrator reaches under explicit managed lifecycle consent, may
/// call <see cref="FbConnection.CreateDatabase"/> — and it does so before the first table DDL.</para>
/// </summary>
internal sealed class FirebirdDdlExecutor : IRelationalDdlExecutor
{
    /// <summary>
    /// Firebird's DDL is transactional: two schema statements racing on one database conflict on the
    /// system relations and surface as "update conflicts with concurrent update". Concurrent first-use
    /// of two entities hits exactly that, so schema work on this store serializes — it is rare,
    /// once-per-table work, and one writer is the honest cost of a transactional catalog.
    /// </summary>
    private static readonly SemaphoreSlim DdlGate = new(1, 1);

    /// <summary>The server could not open the database file, which is why nothing was described.</summary>
    public bool DatabaseUnreachable { get; private set; }

    private readonly string _connectionString;
    private readonly IReadOnlyList<FirebirdShadowColumn> _shadowColumns;

    private FirebirdDdlExecutor(string connectionString, IReadOnlyList<FirebirdShadowColumn> shadowColumns)
    {
        _connectionString = connectionString;
        _shadowColumns = shadowColumns;
    }

    /// <summary>
    /// Firebird holds no JSON functions, so paths inside the entity document cannot be read back out
    /// of it with SQL. This store therefore realizes them as plain shadow columns — written on every
    /// insert alongside the document and read by filters as flat columns — so scalar filters, sorts
    /// and indexes are still enforced at the store, and an isolation discriminator never degrades to
    /// an in-memory residual.
    /// </summary>
    public static FirebirdDdlExecutor For(string connectionString, IReadOnlyList<FirebirdShadowColumn>? shadowColumns = null) =>
        new(connectionString, shadowColumns ?? []);

    /// <summary>Opens a connection for a schema operation, refusing to create a missing database file.</summary>
    private async Task<FbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new FbConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch (FbException error) when (IsDatabaseAbsent(error))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            DatabaseUnreachable = true;
            throw;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Provisioning consent reached the executor. Creates the database file when absent — a managed
    /// lifecycle action the source policy has already authorized — then returns an open connection.
    /// </summary>
    private async Task<FbConnection> OpenOrCreateAsync(CancellationToken ct)
    {
        try
        {
            return await OpenAsync(ct).ConfigureAwait(false);
        }
        catch (FbException error) when (IsDatabaseAbsent(error))
        {
            FbConnection.CreateDatabase(_connectionString, pageSize: 16384, forcedWrites: true, overwrite: false);
            DatabaseUnreachable = false;
            return await OpenAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<RelationalTableShape?> Describe(RelationalTableDefinition table, CancellationToken ct = default)
    {
        FbConnection connection;
        try
        {
            connection = await OpenAsync(ct).ConfigureAwait(false);
        }
        catch (FbException)
        {
            // OpenAsync marked DatabaseUnreachable when the file was absent; any other open failure
            // also means nothing was described, and the caller reads the flag to say why.
            return null;
        }

        await using var _ = connection.ConfigureAwait(false);

        // System relation names are CHAR(31) space-padded; a plain string parameter compares the
        // quoted (case-exact) name we created the table with.
        var columns = new Dictionary<string, RelationalColumnState?>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT TRIM(f.RDB$FIELD_NAME)
                FROM RDB$RELATION_FIELDS f
                WHERE f.RDB$RELATION_NAME = @name
                ORDER BY f.RDB$FIELD_POSITION
                """;
            command.Parameters.Add("@name", table.Name);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columns[reader.GetString(0)] = null;
        }

        // No relational store holds a table with no columns, so an empty catalogue is an absent table.
        if (columns.Count == 0) return null;

        var key = new List<(int Ordinal, string Name)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT TRIM(s.RDB$FIELD_NAME), s.RDB$FIELD_POSITION
                FROM RDB$RELATION_CONSTRAINTS rc
                JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
                WHERE rc.RDB$RELATION_NAME = @name AND rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'
                ORDER BY s.RDB$FIELD_POSITION
                """;
            command.Parameters.Add("@name", table.Name);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                key.Add((reader.GetInt32(1), reader.GetString(0)));
        }

        return new RelationalTableShape(columns, key.OrderBy(static part => part.Ordinal)
            .Select(static part => part.Name).ToArray());
    }

    public async Task Create(RelationalTableDefinition table, CancellationToken ct = default)
    {
        var identity = table.Columns.Where(static column => column.IsIdentity).ToArray();
        // A single store-generated integer key becomes an IDENTITY column; anything else takes an
        // ordinary PRIMARY KEY and the application supplies the value.
        var generated = identity.Length == 1 && identity[0].IsGenerated && IsInteger(identity[0].ClrType);
        var definitions = table.Columns.Select(column => Definition(column, generated)).ToList();
        definitions.AddRange(_shadowColumns.Select(ShadowColumn));
        definitions.Add($"PRIMARY KEY ({string.Join(", ", table.Identity.Select(FirebirdDialect.Quote))})");
        await DdlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenOrCreateAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {FirebirdDialect.Quote(table.Name)} ({string.Join(", ", definitions)})";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            DdlGate.Release();
        }
    }

    public async Task AddColumn(RelationalTableDefinition table, RelationalColumnDefinition column, CancellationToken ct = default) =>
        // Never NOT NULL: Firebird refuses to add one to a populated table, and the only columns this
        // store declares NOT NULL are identity columns, which cannot arrive after the table.
        await Execute(
            $"ALTER TABLE {FirebirdDialect.Quote(table.Name)} ADD {Definition(column, generatedIdentity: false)}",
            ct).ConfigureAwait(false);

    public async Task CreateIndex(
        RelationalTableDefinition table,
        RelationalIndexDefinition index,
        CancellationToken ct = default)
    {
        await using var connection = await OpenOrCreateAsync(ct).ConfigureAwait(false);

        // Firebird has no CREATE INDEX IF NOT EXISTS, so existence is a question asked first.
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM RDB$INDICES WHERE RDB$INDEX_NAME = @name";
            probe.Parameters.Add("@name", index.Name.PadRight(31));
            if (await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return;
        }

        var parts = index.Parts.Select(part => ColumnFor(table, part));
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {FirebirdDialect.Quote(index.Name)} " +
            $"ON {FirebirdDialect.Quote(table.Name)} ({string.Join(", ", parts)})";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The physical column an index part covers. A flat part names its own column; a top-level scalar
    /// of the document is served by the shadow column reads resolve through; anything deeper has no
    /// column and no expression route (Firebird indexes only columns), so the disagreement with the
    /// mapping surfaces by name instead of as an inert index.
    /// </summary>
    private string ColumnFor(RelationalTableDefinition table, RelationalIndexPart part)
    {
        if (part.Path.IsNested && part.Path.Segments.Count == 1)
            return FirebirdDialect.Quote(part.Path.Segments[0]);
        var column = part.Path.IsNested
            ? null
            : table.Columns.FirstOrDefault(value => string.Equals(value.Name, part.Path.Name, StringComparison.Ordinal));
        return column is null
            ? throw new InvalidOperationException(
                $"Index part '{part.Path}' for {table} has no physical column to index. Firebird indexes columns only, " +
                "so a declared index must read a flat scalar property of the mapped table.")
            : FirebirdDialect.Quote(column.Name);
    }

    private string Definition(RelationalColumnDefinition column, bool generatedIdentity)
    {
        var identity = column.IsIdentity && generatedIdentity ? " GENERATED BY DEFAULT AS IDENTITY" : string.Empty;
        return $"{FirebirdDialect.Quote(column.Name)} " +
               $"{StoreTypeOf(column.ClrType, column.IsIdentity, column.Shape == RelationalStorageShape.Structured)}{identity}" +
               (column.IsIdentity ? " NOT NULL" : string.Empty);
    }

    /// <summary>
    /// A shadow column: the mapped scalar it mirrors takes the encoded physical type the write path
    /// produces, so a filter comparand, a stored value and an index key are the same bytes; a managed
    /// discriminator is text. Nullable: rows written outside any scope carry no discriminator value.
    /// </summary>
    private string ShadowColumn(FirebirdShadowColumn column) => column.Managed
        ? $"{FirebirdDialect.Quote(column.Name)} VARCHAR(255)"
        : $"{FirebirdDialect.Quote(column.Name)} {StoreTypeOf(column.ClrType, identity: false, structured: false)}";

    private async Task Execute(string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static bool IsDatabaseAbsent(FbException error) =>
        error.ErrorCode == 335544344; // isc_io_error — the server could not open the database file.

    private static bool IsInteger(Type type)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (value.IsEnum) value = typeof(string);
        return value == typeof(byte) || value == typeof(sbyte) || value == typeof(short) || value == typeof(ushort) ||
               value == typeof(int) || value == typeof(uint) || value == typeof(long) || value == typeof(ulong);
    }

    internal static string StoreTypeOf(Type clrType, bool identity, bool structured)
    {
        if (structured) return "BLOB SUB_TYPE TEXT";
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type.IsEnum) type = typeof(string);
        if (type == typeof(bool)) return "BOOLEAN";
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)) return "SMALLINT";
        if (type == typeof(int) || type == typeof(uint)) return "INTEGER";
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(TimeSpan)) return "BIGINT";
        if (type == typeof(float)) return "FLOAT";
        if (type == typeof(double)) return "DOUBLE PRECISION";
        if (type == typeof(decimal)) return "DECIMAL(38, 10)";
        if (type == typeof(Guid)) return "CHAR(16) CHARACTER SET OCTETS";
        if (type == typeof(DateTime)) return "TIMESTAMP";
        if (type == typeof(DateTimeOffset)) return "VARCHAR(35)";
        if (type == typeof(DateOnly)) return "VARCHAR(10)";
        if (type == typeof(TimeOnly)) return "VARCHAR(16)";
        if (type == typeof(byte[])) return "BLOB SUB_TYPE BINARY";
        return identity ? "VARCHAR(255)" : "VARCHAR(8191)";
    }
}
