using Koan.Data.Abstractions;
using Koan.Data.Relational.Orchestration;
using MySqlConnector;

namespace Koan.Data.Connector.MySql.Runtime;

/// <summary>
/// How MySQL spells a table, a column and a generated column. Every decision behind these words belongs to the
/// schema orchestrator; this speaks over a connection the repository has already opened, for one schema
/// operation.
///
/// <para>This is the one store that describes its own columns well enough to compare them, so it is the one that
/// judges them: a character set is invisible to a CLR type, and this adapter was catching that drift before the
/// seam existed.</para>
/// </summary>
internal sealed class MySqlDdlExecutor(MySqlConnection connection, MySqlDialect dialect) : IRelationalDdlExecutor
{
    private const string RequiredEngine = "InnoDB";

    public async Task<RelationalTableShape?> Describe(RelationalTableDefinition table, CancellationToken ct = default)
    {
        string? engine;
        bool isMariaDb;
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT engine, VERSION() FROM information_schema.tables " +
                "WHERE table_schema=@database AND table_name=@table LIMIT 1";
            probe.Parameters.AddWithValue("database", table.Schema);
            probe.Parameters.AddWithValue("table", table.Name);
            await using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            engine = Convert.ToString(reader.GetValue(0));
            isMariaDb = (Convert.ToString(reader.GetValue(1)) ?? string.Empty)
                .Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        }
        if (engine is null) return null;

        await using var command = connection.CreateCommand();
        // MariaDB implements its JSON alias as LONGTEXT plus an exact json_valid(column) check and
        // reports LONGTEXT through information_schema.columns. Preserve that semantic distinction:
        // an ordinary LONGTEXT must not satisfy a structured mapping merely because its collation matches.
        var jsonAlias = isMariaDb
            ? """
              EXISTS (
                  SELECT 1
                    FROM information_schema.check_constraints AS cc
                   WHERE cc.constraint_schema = c.table_schema
                     AND cc.table_name = c.table_name
                     AND REPLACE(REPLACE(LOWER(cc.check_clause), '`', ''), ' ', '') =
                         CONCAT('json_valid(', LOWER(c.column_name), ')')
              )
              """
            : "FALSE";
        command.CommandText = $"""
            SELECT c.column_name, c.column_type, c.is_nullable,
                   c.character_set_name, c.collation_name, c.extra, pk.ordinal_position,
                   c.column_comment, {jsonAlias} AS is_json_alias
              FROM information_schema.columns AS c
              LEFT JOIN information_schema.key_column_usage AS pk
                ON pk.table_schema = c.table_schema
               AND pk.table_name = c.table_name
               AND pk.column_name = c.column_name
               AND pk.constraint_name = 'PRIMARY'
             WHERE c.table_schema = @database AND c.table_name = @table
             ORDER BY c.ordinal_position
            """;
        command.Parameters.AddWithValue("database", table.Schema);
        command.Parameters.AddWithValue("table", table.Name);

        var columns = new Dictionary<string, RelationalColumnState?>(StringComparer.OrdinalIgnoreCase);
        var key = new List<(int Ordinal, string Name)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var extra = Convert.ToString(reader.GetValue(5)) ?? string.Empty;
                var name = reader.GetString(0);
                columns[name] = new RelationalColumnState(
                    name,
                    Nullable: string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                    IsGenerated: extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                    IsProjected: extra.Contains("stored generated", StringComparison.OrdinalIgnoreCase),
                    NativeType: Convert.ToBoolean(reader.GetValue(8))
                        ? "json"
                        : Native(
                            reader.GetString(1),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4)),
                    ProjectionStamp: Stamp(reader.IsDBNull(7) ? null : reader.GetString(7)));
                if (!reader.IsDBNull(6)) key.Add((Convert.ToInt32(reader.GetValue(6)), name));
            }
        }
        if (columns.Count == 0) return null;

        // The engine is a container fact no neutral column model can carry, and Koan's guarantees here - row
        // locking, transactional DDL rollback - are InnoDB's, not MySQL's in general.
        var incompatible = string.Equals(engine, RequiredEngine, StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>()
            : [$"Table {table} must use the {RequiredEngine} engine; found {engine}."];
        return new RelationalTableShape(
            columns,
            key.OrderBy(static part => part.Ordinal).Select(static part => part.Name).ToArray(),
            incompatible);
    }

    /// <summary>
    /// Compares in MySQL's own vocabulary, because a CLR type cannot see a character set and this adapter has
    /// always caught that. Both sides are rendered the same way, so the comparison is symmetric; a collation the
    /// expectation does not name is one the mapping does not care about.
    /// </summary>
    public bool ColumnMatches(RelationalColumnDefinition expected, RelationalColumnState actual)
    {
        if (actual.NativeType is null) return false;
        var (wantType, wantCharacterSet, wantCollation) = Split(StoreType(expected));
        var (haveType, haveCharacterSet, haveCollation) = Split(actual.NativeType);
        // MySQL stores a boolean as tinyint(1) and reports it that way, so the expectation is read back the same.
        if (string.Equals(wantType, "boolean", StringComparison.OrdinalIgnoreCase)) wantType = "tinyint(1)";
        if (expected.IsProjected && !string.Equals(actual.ProjectionStamp, ExpectedStamp(expected), StringComparison.Ordinal))
            return false;
        return string.Equals(Normalize(haveType), Normalize(wantType), StringComparison.Ordinal) &&
               (wantCharacterSet is null ||
                string.Equals(haveCharacterSet, wantCharacterSet, StringComparison.OrdinalIgnoreCase)) &&
               (wantCollation is null ||
                string.Equals(haveCollation, wantCollation, StringComparison.OrdinalIgnoreCase)) &&
               // MySQL constrains its key and nothing else, which is what Create writes and so what a table
               // Koan owns must hold.
               actual.Nullable != expected.IsIdentity;
    }

    public Task Create(RelationalTableDefinition table, CancellationToken ct = default)
    {
        var definitions = table.Columns.Select(Definition).ToList();
        definitions.Add($"PRIMARY KEY ({string.Join(", ", table.Identity.Select(MySqlDialect.Quote))})");
        return Execute(
            $"CREATE TABLE IF NOT EXISTS {Qualify(table)} " +
            $"({string.Join(", ", definitions)}) ENGINE={RequiredEngine}",
            ct);
    }

    public Task AddColumn(RelationalTableDefinition table, RelationalColumnDefinition column, CancellationToken ct = default) =>
        Execute($"ALTER TABLE {Qualify(table)} ADD COLUMN {Definition(column)}", ct);

    /// <summary>
    /// One statement: MySQL restates a generated column in place and rebuilds every index over it, so the
    /// indexes the old expression had quietly retired come back with it.
    /// </summary>
    public Task RebuildProjection(
        RelationalTableDefinition table,
        RelationalColumnDefinition column,
        CancellationToken ct = default) =>
        Execute($"ALTER TABLE {Qualify(table)} MODIFY COLUMN {Definition(column)}", ct);

    private string Definition(RelationalColumnDefinition column)
    {
        if (column.IsProjected)
        {
            var source = column.ProjectedFrom
                ?? throw new InvalidOperationException(
                    $"Projected column '{column.Name}' carries no source path to compute from.");
            // The generated expression is the dialect's own read, so the column holds exactly what a query
            // computing the same value would produce.
            var expression = dialect.Read(source, MappingValueShape.Scalar, column.ClrType);
            return $"{MySqlDialect.Quote(column.Name)} {StoreType(column)} " +
                   $"GENERATED ALWAYS AS ({expression}) STORED COMMENT '{StampPrefix}{Fingerprint(expression)}'";
        }

        var generated = column.IsIdentity && column.IsGenerated && IsInteger(column.ClrType)
            ? " AUTO_INCREMENT"
            : string.Empty;
        return $"{MySqlDialect.Quote(column.Name)} {StoreType(column)}{generated} " +
               (column.IsIdentity ? "NOT NULL" : "NULL");
    }

    /// <summary>
    /// A declared index is built over the columns the store already computes, not over a repeated expression.
    ///
    /// <para>MySQL cannot index a JSON expression directly; a generated column is the supported route, and this
    /// store already holds one per mapped scalar.</para>
    /// </summary>
    public async Task CreateIndex(
        RelationalTableDefinition table,
        RelationalIndexDefinition index,
        CancellationToken ct = default)
    {
        // MySQL has no CREATE INDEX IF NOT EXISTS and no procedural IF outside a routine, so existence is a
        // question asked before the statement rather than a clause inside it.
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = """
                SELECT 1 FROM information_schema.statistics
                 WHERE table_schema = @database AND table_name = @table AND index_name = @name
                 LIMIT 1
                """;
            probe.Parameters.AddWithValue("database", table.Schema);
            probe.Parameters.AddWithValue("table", table.Name);
            probe.Parameters.AddWithValue("name", index.Name);
            if (await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return;
        }

        var columns = index.Parts.Select(part => Quoted(table, part));
        await Execute(
            $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {MySqlDialect.Quote(index.Name)} " +
            $"ON {Qualify(table)} ({string.Join(", ", columns)})",
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The column that holds an indexed value. A part that reads inside the structured root is served by the
    /// projected column the store already computes for it, which is the column reads resolve through; anything
    /// else is a physical column of its own.
    /// </summary>
    private static string Quoted(RelationalTableDefinition table, RelationalIndexPart part)
    {
        var column = (part.Path.IsNested
                ? table.Columns.FirstOrDefault(value => value.IsProjected && value.ProjectedFrom == part.Path)
                : table.Columns.FirstOrDefault(value =>
                    string.Equals(value.Name, part.Path.Name, StringComparison.Ordinal)))
            ?? throw new InvalidOperationException(
                $"Index part '{part.Path}' for {table} has no column to index. A declared index reads a single "
                + "scalar property, which this store either holds physically or projects, so this is a mapping "
                + "the schema owner and this executor disagree about.");

        // MySQL refuses a key over TEXT or BLOB without a prefix length. A prefix is not an approximation: the
        // engine seeks on it and then rechecks the full column, so results are exact and only selectivity
        // changes. This is why text is indexable here and not on SQL Server, which has no prefix index.
        var name = MySqlDialect.Quote(column.Name);
        return StoreType(column) is "longtext" or "longblob" ? $"{name}({TextKeyPrefix})" : name;
    }

    /// <summary>
    /// Characters of a text column taken into an index key.
    ///
    /// <para>255 characters is 1020 bytes in utf8mb4, so up to three text parts fit InnoDB's 3072-byte key
    /// limit on the default DYNAMIC row format. Wider than that and MySQL refuses the statement by name, which
    /// is the right way for a declared index nobody can build to surface.</para>
    /// </summary>
    private const int TextKeyPrefix = 255;

    private async Task Execute(string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Qualify(RelationalTableDefinition table) =>
        $"{MySqlDialect.Quote(table.Schema)}.{MySqlDialect.Quote(table.Name)}";

    private const string StampPrefix = "koan-gen:";

    /// <summary>
    /// The recipe marker written onto a generated column, and read back to tell a current one from a stale one.
    ///
    /// <para>A generated column can go stale without its type changing: the dialect changes how it reads a JSON
    /// scalar, new tables get the new expression, and an existing table keeps the old one. That is not
    /// cosmetic — the old expression is what broke writes of a null before it was fixed, and the optimizer only
    /// substitutes a generated column for a query that spells the value the same way, so a stale column also
    /// quietly stops serving the indexes built on it.</para>
    ///
    /// <para>Comparing the stored expression directly does not work: <c>information_schema</c> returns MySQL's
    /// own canonical rendering, not the text it was given, so a comparison would be against the server's
    /// formatter rather than against Koan. A fingerprint Koan wrote itself is compared against a fingerprint
    /// Koan computes, which is exact. A column with no marker was written by a Koan that did not know to leave
    /// one, which is precisely the population that needs rebuilding.</para>
    /// </summary>
    private string ExpectedStamp(RelationalColumnDefinition column)
    {
        var source = column.ProjectedFrom;
        return source is null
            ? string.Empty
            : Fingerprint(dialect.Read(source, MappingValueShape.Scalar, column.ClrType));
    }

    private static string Fingerprint(string expression) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(expression)))[..12].ToLowerInvariant();

    private static string? Stamp(string? comment) =>
        comment is not null && comment.StartsWith(StampPrefix, StringComparison.Ordinal)
            ? comment[StampPrefix.Length..]
            : null;

    private static string Native(string columnType, string? characterSet, string? collation) =>
        characterSet is null
            ? columnType
            : $"{columnType} CHARACTER SET {characterSet} COLLATE {collation ?? "<none>"}";

    private static (string Type, string? CharacterSet, string? Collation) Split(string spelling)
    {
        const string marker = " CHARACTER SET ";
        var at = spelling.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return (spelling, null, null);
        var suffix = spelling[(at + marker.Length)..].Split(" COLLATE ", 2, StringSplitOptions.TrimEntries);
        return (spelling[..at], suffix[0], suffix.Length == 2 ? suffix[1] : null);
    }

    private static string Normalize(string value)
    {
        var normalized = string.Concat(value.Where(static character => !char.IsWhiteSpace(character)))
            .ToLowerInvariant();

        // MariaDB still reports legacy integer display widths (for example int(11)); MySQL 8.4
        // reports the same storage type as int. Width never changes integer range or representation.
        // Keep tinyint(1), however, because BOOLEAN is intentionally read back through that spelling.
        foreach (var integer in new[] { "tinyint", "smallint", "mediumint", "bigint", "integer", "int" })
        {
            var prefix = integer + "(";
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var close = normalized.IndexOf(')', prefix.Length);
            if (close < 0 || !normalized[prefix.Length..close].All(char.IsAsciiDigit)) break;
            if (integer == "tinyint" && normalized[prefix.Length..close] == "1") break;
            normalized = integer + normalized[(close + 1)..];
            break;
        }

        return normalized;
    }

    private static string StoreType(RelationalColumnDefinition column)
    {
        if (column.Shape == RelationalStorageShape.Structured) return "json";
        var value = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        if (value.IsEnum) value = typeof(string);
        if (value == typeof(bool)) return "boolean";
        if (value == typeof(byte)) return "tinyint unsigned";
        if (value == typeof(sbyte)) return "tinyint";
        if (value == typeof(short)) return "smallint";
        if (value == typeof(ushort)) return "smallint unsigned";
        if (value == typeof(int)) return "int";
        if (value == typeof(uint)) return "int unsigned";
        if (value == typeof(long) || value == typeof(TimeSpan)) return "bigint";
        if (value == typeof(ulong)) return "bigint unsigned";
        if (value == typeof(float)) return "float";
        if (value == typeof(double)) return "double";
        if (value == typeof(decimal)) return "decimal(65,30)";
        if (value == typeof(Guid)) return "char(36)";
        if (value == typeof(DateTime)) return "datetime(6)";
        if (value == typeof(DateTimeOffset)) return "varchar(35)";
        if (value == typeof(DateOnly)) return "date";
        if (value == typeof(TimeOnly)) return "time(6)";
        if (value == typeof(byte[])) return "longblob";
        // A key has to fit an index, and it is compared byte for byte, so its collation is part of the contract.
        return column.IsIdentity ? "varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin" : "longtext";
    }

    private static bool IsInteger(Type type)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (value.IsEnum) value = typeof(string);
        return value == typeof(byte) || value == typeof(sbyte) || value == typeof(short) || value == typeof(ushort) ||
               value == typeof(int) || value == typeof(uint) || value == typeof(long) || value == typeof(ulong);
    }
}
