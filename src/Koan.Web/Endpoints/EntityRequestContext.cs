using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Koan.Core.Capabilities;
using Koan.Web.Hooks;

namespace Koan.Web.Endpoints;

/// <summary>
/// Protocol-neutral context describing an entity endpoint invocation.
/// Captures per-request services, options, headers, and metadata shared across REST, GraphQL, and future adapters.
/// </summary>
public sealed class EntityRequestContext
{
    public EntityRequestContext(
        IServiceProvider services,
        QueryOptions options,
        CancellationToken cancellationToken,
        HttpContext? httpContext = null,
        ClaimsPrincipal? user = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        CancellationToken = cancellationToken;
        HttpContext = httpContext;
        User = user ?? httpContext?.User ?? new ClaimsPrincipal();
        Capabilities = new CapabilitySet();
    }

    public IServiceProvider Services { get; }

    public QueryOptions Options { get; }

    public CancellationToken CancellationToken { get; }

    public HttpContext? HttpContext { get; }

    public ClaimsPrincipal User { get; }

    /// <summary>
    /// The backing provider's capabilities as the unified <see cref="CapabilitySet"/> (ARCH-0084).
    /// Populated by the endpoint from <c>DataCaps.Describe(repo)</c> — carries both query and write tokens.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public Koan.Web.Authorization.FieldAccess? FieldAccess { get; private set; }

    /// <summary>Bind one adapter's stricter prepared field policy after the context establishes its principal.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public void BindFieldAccess(Koan.Web.Authorization.FieldAccess fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (FieldAccess is not null || !fields.IsCurrent(fields.RootType, User))
            throw new InvalidOperationException("Field access must bind once to the effective request principal.");
        FieldAccess = fields;
    }

    /// <summary>The provider capabilities populated by the shared endpoint.</summary>
    public CapabilitySet Capabilities { get; set; }

    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IList<string> Warnings { get; } = new List<string>();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public void Warn(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Warnings.Add(message);
        }
    }
}









