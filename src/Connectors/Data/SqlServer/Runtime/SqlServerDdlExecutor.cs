using Koan.Data.Abstractions;
using Koan.Data.Relational.Orchestration;
using Microsoft.Data.SqlClient;

namespace Koan.Data.Connector.SqlServer.Runtime;

/// <summary>
/// How SQL Server spells a schema, a table and a column. Every decision behind these words belongs to the schema
/// orchestrator; this speaks over a connection the repository has already opened, for one schema operation.
/// </summary>
internal sealed class SqlServerDdlExecutor(SqlConnection connection, SqlServerDialect dialect) : IRelationalDdlExecutor
{
    public async Task<RelationalTableShape?> Describe(RelationalTableDefinition table, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, ic.key_ordinal
              FROM sys.columns AS c
              JOIN sys.tables AS t ON t.object_id = c.object_id
              JOIN sys.schemas AS s ON s.schema_id = t.schema_id
              LEFT JOIN sys.indexes AS i ON i.object_id = t.object_id AND i.is_primary_key = 1
              LEFT JOIN sys.index_columns AS ic
                ON ic.object_id = t.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
             WHERE s.name = @schema AND t.name = @table
             ORDER BY c.column_id
            """;
        command.Parameters.AddWithValue("schema", table.Schema);
        command.Parameters.AddWithValue("table", table.Name);

        var columns = new Dictionary<string, RelationalColumnState?>(StringComparer.OrdinalIgnoreCase);
        var key = new List<(int Ordinal, string Name)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                // Presence only. This adapter has never compared column definitions, and starting to would judge
                // every existing database against expectations no release has held it to; that is a capability of
                // its own, with its own proof, not a side effect of moving the decision.
                columns[name] = null;
                if (!reader.IsDBNull(1)) key.Add((reader.GetByte(1), name));
            }
        }

        return columns.Count == 0
            ? null
            : new RelationalTableShape(
                columns,
                key.OrderBy(static part => part.Ordinal).Select(static part => part.Name).ToArray());
    }

    public Task Create(RelationalTableDefinition table, CancellationToken ct = default)
    {
        var definitions = table.Columns.Select(Definition).ToList();
        definitions.Add($"PRIMARY KEY NONCLUSTERED ({string.Join(", ", table.Identity.Select(SqlServerDialect.Quote))})");
        return Execute(
            $"IF SCHEMA_ID(N'{Literal(table.Schema)}') IS NULL " +
            $"EXEC(N'CREATE SCHEMA {SqlServerDialect.Quote(table.Schema)}'); " +
            $"IF OBJECT_ID(N'{Identifier(table)}', N'U') IS NULL " +
            $"CREATE TABLE {Qualify(table)} ({string.Join(", ", definitions)});",
            ct);
    }

    public Task AddColumn(RelationalTableDefinition table, RelationalColumnDefinition column, CancellationToken ct = default) =>
        Execute($"ALTER TABLE {Qualify(table)} ADD {Definition(column)}", ct);

    private string Definition(RelationalColumnDefinition column)
    {
        // A projected column reads through the dialect, so its expression is the one queries emit. SQL Server
        // substitutes a persisted computed column for a matching JSON_VALUE only on that exact match.
        if (column.IsProjected)
        {
            var source = column.ProjectedFrom
                ?? throw new InvalidOperationException(
                    $"Projected column '{column.Name}' carries no source path to compute from.");
            return $"{SqlServerDialect.Quote(column.Name)} AS " +
                   $"{dialect.Read(source, MappingValueShape.Scalar, column.ClrType)} PERSISTED";
        }

        var generated = column.IsIdentity && column.IsGenerated && IsNumeric(column.ClrType)
            ? " IDENTITY(1,1)"
            : string.Empty;
        return $"{SqlServerDialect.Quote(column.Name)} {StoreType(column)}{generated} " +
               (column.IsIdentity ? "NOT NULL" : "NULL");
    }

    /// <summary>
    /// A declared index is built over the columns the store already computes, not over a repeated expression.
    /// /// <para>SQL Server substitutes an indexed persisted computed column for the matching
    /// <c>JSON_VALUE</c> expression on its own, so a query never names the index or the column.</para>
    /// </summary>
    public Task CreateIndex(
        RelationalTableDefinition table,
        RelationalIndexDefinition index,
        CancellationToken ct = default)
    {
        var columns = index.Parts.Select(part => Quoted(table, part));
        return Execute(
            $"IF NOT EXISTS (SELECT 1 FROM sys.indexes " +
            $"WHERE name = N'{Literal(index.Name)}' AND object_id = OBJECT_ID(N'{Identifier(table)}')) " +
            $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX {SqlServerDialect.Quote(index.Name)} " +
            $"ON {Qualify(table)} ({string.Join(", ", columns)})",
            ct);
    }

    /// <summary>
    /// The column that holds an indexed value. A part that reads inside the structured root is served by the
    /// projected column the store already computes for it, which is the column reads resolve through; anything
    /// else is a physical column of its own.
    /// </summary>
    private static string Quoted(RelationalTableDefinition table, RelationalIndexPart part)
    {
        if (!part.Path.IsNested) return SqlServerDialect.Quote(part.Path.Name);
        var projected = table.Columns.FirstOrDefault(column => column.IsProjected && column.ProjectedFrom == part.Path)
            ?? throw new InvalidOperationException(
                $"Index part '{part.Path}' for {table} has no projected column to index. A declared index reads a "
                + "single scalar property, which this store always projects, so this is a mapping the schema owner "
                + "and this executor disagree about.");
        return SqlServerDialect.Quote(projected.Name);
    }

    private async Task Execute(string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Qualify(RelationalTableDefinition table) =>
        $"{SqlServerDialect.Quote(table.Schema)}.{SqlServerDialect.Quote(table.Name)}";

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// The table as OBJECT_ID must be given it: bracketed, then escaped for the string literal that carries it.
    ///
    /// <para>Unbracketed, SQL Server reads the dots in a name like <c>dbo.Koan.Jobs.JobRecord</c> as parts of a
    /// four-part identifier, resolves nothing, and returns NULL - so every guard written against it silently
    /// answers "absent" and the statement it guards runs every time. Koan's default storage names are namespaced
    /// and therefore full of dots, which is exactly the case this hits.</para>
    /// </summary>
    private static string Identifier(RelationalTableDefinition table) =>
        Literal($"{SqlServerDialect.Quote(table.Schema)}.{SqlServerDialect.Quote(table.Name)}");

    private static string StoreType(RelationalColumnDefinition column)
    {
        if (column.Shape == RelationalStorageShape.Structured) return "nvarchar(max)";
        var value = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        if (value.IsEnum) value = typeof(string);
        if (value == typeof(bool)) return "bit";
        if (value == typeof(byte) || value == typeof(sbyte) || value == typeof(short)) return "smallint";
        if (value == typeof(int) || value == typeof(ushort)) return "int";
        if (value == typeof(long) || value == typeof(uint) || value == typeof(TimeSpan)) return "bigint";
        if (value == typeof(float)) return "real";
        if (value == typeof(double)) return "float";
        if (value == typeof(decimal)) return "decimal(38,10)";
        if (value == typeof(Guid)) return "uniqueidentifier";
        if (value == typeof(DateTime)) return "datetime2";
        if (value == typeof(DateTimeOffset)) return "datetimeoffset";
        if (value == typeof(DateOnly)) return "date";
        if (value == typeof(TimeOnly)) return "time";
        if (value == typeof(byte[])) return "varbinary(max)";
        // A key has to fit an index, which nvarchar(max) cannot.
        return column.IsIdentity ? "nvarchar(450)" : "nvarchar(max)";
    }

    private static bool IsNumeric(Type type)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;
        if (value.IsEnum) value = typeof(string);
        return value == typeof(byte) || value == typeof(sbyte) || value == typeof(short) || value == typeof(ushort) ||
               value == typeof(int) || value == typeof(uint) || value == typeof(long) || value == typeof(ulong);
    }
}
