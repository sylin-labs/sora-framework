using System.Security.Claims;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Sorting;
using Koan.Data.Core;

namespace Koan.Web.Endpoints;

/// <summary>Evidence belongs to one executed query and its returned objects, never to a request-wide permission.</summary>
internal sealed class EndpointReadProof<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly EntityRequestContext _context;
    private readonly EntityContext.ContextState? _ambient;
    private readonly Filter? _serverFilter;
    private readonly SortSpec[] _sort;
    private readonly int _page;
    private readonly int _pageSize;
    private readonly string? _q;
    private readonly string _shape;
    private readonly string? _view;
    private readonly bool _includeRelationships;
    private readonly string[] _principal;
    private readonly Dictionary<TEntity, (TKey Id, int Index)> _rows = new(ReferenceEqualityComparer.Instance);
    private IQueryReadEvidence? _evidence;

    private EndpointReadProof(EntityRequestContext context, QueryDefinition query)
    {
        _context = context;
        _ambient = EntityContext.Current;
        _serverFilter = Filter.Snapshot(context.Options.Filter);
        _sort = context.Options.Sort.ToArray();
        _page = context.Options.Page;
        _pageSize = context.Options.PageSize;
        _q = context.Options.Q;
        _shape = context.Options.Shape;
        _view = context.Options.View;
        _includeRelationships = context.Options.IncludeRelationships;
        _principal = PrincipalSnapshot(context.User);
        Query = query with { Filter = Filter.Snapshot(query.Filter), Sort = query.Sort.ToArray() };
    }

    public QueryDefinition Query { get; }

    public static EndpointReadProof<TEntity, TKey>? Prepare(EntityRequestContext context, QueryDefinition query)
        => Filter.HasCounterpart(query.Filter) ? new(context, query) : null;

    public static EndpointReadProof<TEntity, TKey> CaptureRequest(EntityRequestContext context, QueryDefinition query)
        => new(context, query);

    public bool IsRequestUnchanged(EntityRequestContext context)
        => ReferenceEquals(context, _context)
           && Equals(_ambient, EntityContext.Current)
           && Filter.Equivalent(_serverFilter, context.Options.Filter)
           && _sort.SequenceEqual(context.Options.Sort)
           && _page == context.Options.Page && _pageSize == context.Options.PageSize
           && _q == context.Options.Q
           && _shape == context.Options.Shape && _view == context.Options.View
           && _includeRelationships == context.Options.IncludeRelationships
           && _principal.SequenceEqual(PrincipalSnapshot(context.User));

    public bool Bind(IQueryReadEvidence? evidence, IReadOnlyList<TEntity> rows)
    {
        if (_evidence is not null || evidence is null
            || evidence.EntityType != typeof(TEntity) || evidence.KeyType != typeof(TKey)
            || Normalize(evidence.Partition) != Normalize(Query.Partition ?? _ambient?.Partition)
            || !evidence.CoversRows(rows.Cast<object>())) return false;

        _evidence = evidence;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!_rows.TryAdd(row, (row.Id, index))) return false;
        }
        return IsValid(_context, rows);
    }

    public bool Contains(TEntity row)
        => _rows.TryGetValue(row, out var selected) && EqualityComparer<TKey>.Default.Equals(selected.Id, row.Id);

    public bool IsValid(EntityRequestContext context, IReadOnlyList<TEntity> rows)
    {
        if (!IsRequestUnchanged(context) || _evidence is null || !_evidence.IsCurrentScope()) return false;
        return rows.Count == _rows.Count && rows.Select((row, index) =>
            _rows.TryGetValue(row, out var selected) && selected.Index == index
                && EqualityComparer<TKey>.Default.Equals(selected.Id, row.Id)).All(same => same)
            && _evidence.CoversRows(rows.Cast<object>());
    }

    private static string? Normalize(string? partition) => string.IsNullOrWhiteSpace(partition) ? null : partition;

    private static string[] PrincipalSnapshot(ClaimsPrincipal principal)
    {
        // Separate array entries avoid delimiter collisions in arbitrary claim values.
        var values = new List<string>();
        foreach (var identity in principal.Identities)
        {
            values.Add(identity.AuthenticationType ?? string.Empty);
            values.Add(identity.NameClaimType);
            values.Add(identity.RoleClaimType);
            values.Add(identity.IsAuthenticated.ToString());
            values.Add(identity.Claims.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var claim in identity.Claims)
            {
                values.Add(claim.Type);
                values.Add(claim.Value);
                values.Add(claim.ValueType);
                values.Add(claim.Issuer);
                values.Add(claim.OriginalIssuer);
                values.Add(claim.Properties.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var property in claim.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    values.Add(property.Key);
                    values.Add(property.Value);
                }
            }
        }
        return values.ToArray();
    }
}
