using Koan.Core.Composition;
using Koan.Core.Providers;
using Koan.Web.Auth.Infrastructure;
using Koan.Web.Auth.Options;
using Microsoft.Extensions.Options;

namespace Koan.Web.Auth.Providers;

/// <summary>Immutable provider availability, eligibility, and default election for one host.</summary>
internal sealed class AuthProviderPlan : IAuthProviderCatalog
{
    private readonly IReadOnlyDictionary<string, AuthProviderRoute> _routes;

    public AuthProviderPlan(
        IOptions<AuthOptions> options,
        IEnumerable<AuthProviderDefinition> definitions,
        IEnumerable<IAuthProtocol>? protocols = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(definitions);

        var configured = options.Value;
        var protocolCatalog = ProviderCatalog<IAuthProtocol>.Compile(
            protocols ?? [],
            static protocol => new ProviderCandidateDescriptor(protocol.Protocol));
        foreach (var protocol in protocolCatalog.Candidates)
        {
            if (string.Equals(protocol.Id, AuthProviderProtocols.Oidc, StringComparison.OrdinalIgnoreCase)
                || string.Equals(protocol.Id, AuthProviderProtocols.OAuth2, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Authentication protocol '{protocol.Id}' is owned by Koan Web Auth and cannot be replaced by a connector.");
            }
        }

        var catalog = ProviderCatalog<AuthProviderDefinition>.Compile(
            definitions,
            static definition => new ProviderCandidateDescriptor(
                definition.Id,
                priority: definition.Defaults.Priority ?? 0));

        var routes = new Dictionary<string, AuthProviderRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in catalog.Candidates)
        {
            var definition = candidate.Value;
            var isExplicit = configured.Providers.TryGetValue(candidate.Id, out var overlay);
            var effective = isExplicit
                ? ProviderOptions.Merge(definition.Defaults, overlay!)
                : definition.Defaults;
            routes.Add(candidate.Id, Compile(candidate.Id, effective, definition, isExplicit, protocolCatalog));
        }

        // Web Auth itself owns generic OIDC/OAuth2 mechanics. An explicitly configured provider therefore needs no
        // behaviorless "generic OIDC connector" package; config-only ids become exact candidates here.
        foreach (var (id, provider) in configured.Providers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (routes.ContainsKey(id)) continue;
            var definition = new AuthProviderDefinition(
                id,
                provider,
                Automatic: false,
                Available: true,
                AvailabilityReason: "configuration-defined");
            routes.Add(id, Compile(id, provider, definition, isExplicit: true, protocolCatalog));
        }

        _routes = routes;
        Routes = routes.Values.OrderBy(static route => route.Info.Id, StringComparer.Ordinal).ToArray();
        Providers = Routes.Select(static route => route.Info).ToArray();

        DefaultRoute = ElectDefault(configured, Routes);
        Default = DefaultRoute?.Info;
        Receipt = DefaultRoute is null
            ? null
            : new ProviderSelectionReceipt(
                "auth:provider:default",
                DefaultRoute.Info.Id,
                DefaultRoute.Info.Explicit ? ProviderIntentPosture.Required : ProviderIntentPosture.Automatic,
                DefaultRoute.Info.Priority,
                DefaultRoute.Info.Reason);
    }

    internal IReadOnlyList<AuthProviderRoute> Routes { get; }
    internal AuthProviderRoute? DefaultRoute { get; }
    internal ProviderSelectionReceipt? Receipt { get; }

    public IReadOnlyList<AuthProviderInfo> Providers { get; }
    public AuthProviderInfo? Default { get; }

    public AuthProviderInfo? Find(string? id)
        => FindRoute(id)?.Info;

    internal AuthProviderRoute? FindRoute(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _routes.TryGetValue(id.Trim(), out var route) ? route : null;
    }

    private static AuthProviderRoute Compile(
        string id,
        ProviderOptions effective,
        AuthProviderDefinition definition,
        bool isExplicit,
        ProviderCatalog<IAuthProtocol> protocols)
    {
        var protocol = NormalizeProtocol(effective.Type);
        var displayName = string.IsNullOrWhiteSpace(effective.DisplayName) ? id : effective.DisplayName.Trim();
        var priority = effective.Priority ?? definition.Defaults.Priority ?? 0;
        var scopes = effective.Scopes ?? [];
        var customProtocol = protocols.Find(protocol);
        var challenge = protocol is AuthProviderProtocols.Oidc or AuthProviderProtocols.OAuth2 || customProtocol is not null
            ? $"/auth/{id}/challenge"
            : null;

        if (!definition.Available)
        {
            var reason = definition.AvailabilityReason ?? "connector-unavailable";
            return Route("unavailable", false, reason,
                $"Enable the connector capability for provider '{id}' or remove its configuration.");
        }

        if (!effective.Enabled)
            return Route("disabled", false, "explicitly-disabled", null);

        if (!isExplicit && !definition.Automatic)
            return Route("inactive", false, "configuration-required",
                $"Configure Koan:Web:Auth:Providers:{id} with the provider credentials to activate it.");

        if (customProtocol is not null)
        {
            var corrections = customProtocol.Validate(id, effective)
                ?? throw new InvalidOperationException(
                    $"Authentication protocol '{protocol}' returned no configuration validation result for provider '{id}'.");
            if (corrections.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Koan Web Auth provider '{id}' has incomplete or unsupported '{protocol}' configuration. " +
                    string.Join(" ", corrections));
            }
        }

        var missing = customProtocol is null
            ? MissingFields(protocol, effective, protocols.Candidates.Select(static candidate => candidate.Id))
            : [];
        if (missing.Count > 0)
        {
            var source = isExplicit ? "configured" : "automatic";
            throw new InvalidOperationException(
                $"Koan Web Auth provider '{id}' is {source} but incomplete; missing {string.Join(", ", missing)}. " +
                $"Set the missing values under Koan:Web:Auth:Providers:{id} or disable/remove that provider intent.");
        }

        return Route(
            "eligible",
            true,
            isExplicit ? "explicit-configuration" : "automatic-local-provider",
            null);

        AuthProviderRoute Route(string state, bool eligible, string reason, string? correction)
            => new(
                new AuthProviderInfo(
                    id,
                    displayName,
                    protocol,
                    state,
                    eligible,
                    isExplicit,
                    definition.Automatic,
                    priority,
                    reason,
                    correction,
                    effective.Icon,
                    scopes,
                    challenge),
                effective);
    }

    private static AuthProviderRoute? ElectDefault(AuthOptions options, IReadOnlyList<AuthProviderRoute> routes)
    {
        if (!string.IsNullOrWhiteSpace(options.PreferredProviderId))
        {
            var preferred = routes.FirstOrDefault(route =>
                string.Equals(route.Info.Id, options.PreferredProviderId, StringComparison.OrdinalIgnoreCase));
            if (preferred is null)
            {
                throw new InvalidOperationException(
                    $"Koan Web Auth PreferredProviderId '{options.PreferredProviderId}' is unknown. " +
                    $"Available providers: {FormatIds(routes)}.");
            }

            if (!preferred.Info.Eligible)
            {
                throw new InvalidOperationException(
                    $"Koan Web Auth PreferredProviderId '{preferred.Info.Id}' is not eligible ({preferred.Info.Reason}). " +
                    (preferred.Info.Correction ?? "Enable and fully configure that provider."));
            }

            return preferred with
            {
                Info = preferred.Info with { Reason = "preferred-provider" }
            };
        }

        return routes
            .Where(static route => route.Info.Eligible)
            .OrderByDescending(static route => route.Info.Explicit)
            .ThenByDescending(static route => route.Info.Priority)
            .ThenBy(static route => route.Info.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static List<string> MissingFields(string protocol, ProviderOptions options, IEnumerable<string> customProtocols)
    {
        var missing = new List<string>();
        if (protocol == AuthProviderProtocols.Oidc)
        {
            Require(options.Authority, nameof(ProviderOptions.Authority));
            Require(options.ClientId, nameof(ProviderOptions.ClientId));
            Require(options.ClientSecret, nameof(ProviderOptions.ClientSecret));
        }
        else if (protocol == AuthProviderProtocols.OAuth2)
        {
            Require(options.AuthorizationEndpoint, nameof(ProviderOptions.AuthorizationEndpoint));
            Require(options.TokenEndpoint, nameof(ProviderOptions.TokenEndpoint));
            Require(options.UserInfoEndpoint, nameof(ProviderOptions.UserInfoEndpoint));
            Require(options.ClientId, nameof(ProviderOptions.ClientId));
            Require(options.ClientSecret, nameof(ProviderOptions.ClientSecret));
        }
        else
        {
            var supported = new[] { AuthProviderProtocols.Oidc, AuthProviderProtocols.OAuth2 }
                .Concat(customProtocols).Order(StringComparer.Ordinal);
            missing.Add($"Type (supported: {string.Join(", ", supported)}; reference the connector for another protocol)");
        }

        return missing;

        void Require(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) missing.Add(field);
        }
    }

    private static string NormalizeProtocol(string? protocol)
        => string.IsNullOrWhiteSpace(protocol)
            ? AuthProviderProtocols.Oidc
            : protocol.Trim().ToLowerInvariant();

    private static string FormatIds(IEnumerable<AuthProviderRoute> routes)
    {
        var ids = routes.Select(static route => route.Info.Id).Order(StringComparer.Ordinal).ToArray();
        return ids.Length == 0 ? "none" : string.Join(", ", ids);
    }
}

internal sealed record AuthProviderRoute(AuthProviderInfo Info, ProviderOptions Options);
