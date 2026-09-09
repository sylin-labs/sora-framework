using System.Collections;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace Koan.Data.Relational.Ado;

/// <summary>
/// AOT-clean parameter set for the raw-ADO.NET relational helpers (<see cref="AdoCommands"/>). Ordered name→value
/// pairs with Dapper-style IN-expansion: a non-string enumerable value expands its <c>@name</c> token to
/// <c>(@name__0, @name__1, …)</c> and binds each element. Parameters are bound through
/// <see cref="DbCommand.CreateParameter"/> — no <see cref="System.Reflection.Emit"/>, so this survives NativeAOT,
/// where Dapper's emitted deserializers throw <see cref="PlatformNotSupportedException"/>.
/// </summary>
/// <remarks>
/// Names are stored without the <c>@</c> sigil and bound as <c>@name</c> (which Microsoft.Data.Sqlite and the
/// other ADO providers match against the SQL token). Provider mechanisms may translate this model to their
/// native command API.
/// </remarks>
public sealed class SqlParameters
{
    private readonly List<KeyValuePair<string, object?>> _items;

    public SqlParameters() => _items = new List<KeyValuePair<string, object?>>();

    private SqlParameters(List<KeyValuePair<string, object?>> items) => _items = items;

    /// <summary>The empty set — bound as no parameters.</summary>
    public static SqlParameters None { get; } = new SqlParameters(new List<KeyValuePair<string, object?>>(0));

    public int Count => _items.Count;

    /// <summary>The raw ordered (name, value) pairs (sigil-stripped names). Used by the Dapper shim.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Items => _items;

    public SqlParameters Add(string name, object? value)
    {
        _items.Add(new KeyValuePair<string, object?>(Normalize(name), value));
        return this;
    }

    /// <summary>Positional <c>p0, p1, …</c> matching the relational dialect's <c>@p{index}</c> tokens.</summary>
    public static SqlParameters Positional(IReadOnlyList<object?> values)
    {
        var list = new List<KeyValuePair<string, object?>>(values.Count);
        for (var i = 0; i < values.Count; i++) list.Add(new KeyValuePair<string, object?>($"p{i}", values[i]));
        return new SqlParameters(list);
    }

    public static SqlParameters FromDictionary(IReadOnlyDictionary<string, object?> values)
    {
        var list = new List<KeyValuePair<string, object?>>(values.Count);
        foreach (var kv in values) list.Add(new KeyValuePair<string, object?>(Normalize(kv.Key), kv.Value));
        return new SqlParameters(list);
    }

    /// <summary>
    /// The raw-query escape hatch: whatever a caller handed to <c>QueryRaw</c>. A dictionary binds by key; any
    /// other object binds by public property, which is what a caller passing an anonymous object expects.
    ///
    /// <para>Property reflection rather than emitted accessors, so this survives NativeAOT where Dapper's
    /// anonymous-object binder does not. Each relational adapter used to carry its own copy of this.</para>
    /// </summary>
    public static SqlParameters? FromObject(object? values)
    {
        if (values is null) return null;
        if (values is SqlParameters already) return already;
        if (values is IReadOnlyDictionary<string, object?> readOnly) return FromDictionary(readOnly);
        if (values is IDictionary<string, object?> dictionary)
            return FromDictionary((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(dictionary));

        var parameters = new SqlParameters();
        foreach (var property in values.GetType()
                     .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                     .Where(static property => property.GetMethod is not null && property.GetIndexParameters().Length == 0))
            parameters.Add(property.Name, property.GetValue(values));
        return parameters;
    }

    private static string Normalize(string name) => name.StartsWith('@') ? name[1..] : name;

    /// <summary>Binds every parameter onto <paramref name="cmd"/>, applying IN-expansion, and returns the
    /// (possibly rewritten) SQL.</summary>
    internal string Bind(DbCommand cmd, string sql)
    {
        foreach (var (name, value) in _items)
        {
            if (value is not (null or string or byte[]) && value is IEnumerable seq)
            {
                sql = ExpandInClause(cmd, sql, name, seq);
                continue;
            }
            AddScalar(cmd, name, value);
        }
        return sql;
    }

    private static void AddScalar(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = "@" + name;
        p.Value = Bindable(value);
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// The value as a provider will accept it.
    ///
    /// <para>Enums bind as the same strings used by Entity storage and mapping. Provider-specific implicit
    /// enum conversions must not change a domain state into its underlying number.</para>
    /// </summary>
    private static object Bindable(object? value)
    {
        if (value is null) return DBNull.Value;
        return value is Enum enumeration
            ? Koan.Data.Abstractions.EnumStorageEncoding.Format(enumeration)
            : value;
    }

    private static string ExpandInClause(DbCommand cmd, string sql, string name, IEnumerable seq)
    {
        var bound = new List<string>();
        var i = 0;
        foreach (var item in seq)
        {
            var element = $"{name}__{i}";
            AddScalar(cmd, element, item);
            bound.Add("@" + element);
            i++;
        }
        // Empty IN list: a self-contradicting subselect matches nothing (Dapper's behaviour), and keeps the
        // surrounding `IN (…)` syntactically valid where `IN ()` would be a parse error.
        var replacement = bound.Count == 0 ? "(SELECT NULL WHERE 1=0)" : "(" + string.Join(", ", bound) + ")";
        return Regex.Replace(sql, "@" + Regex.Escape(name) + @"\b", replacement);
    }
}
