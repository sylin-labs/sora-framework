using System.Web;
using Koan.Core;
using Koan.Web.Auth.Connector.Atproto.Infrastructure;
using Koan.Web.Auth.Connector.Atproto.Options;
using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Koan.Web.Auth.Connector.Atproto.Hosting;

internal sealed class AtprotoProtocol(IOptions<AtprotoOptions> settings, IHostEnvironment environment) : IAuthProtocol
{
    public string Protocol => Constants.Protocol;

    public IReadOnlyList<string> Validate(string providerId, ProviderOptions options)
    {
        var errors = new List<string>();
        if (providerId != Constants.Provider) errors.Add($"AT Protocol currently owns provider '{Constants.Provider}'; use {Constants.ProviderSection}.");
        if (options.CallbackPath is not null && options.CallbackPath != Constants.Callback)
            errors.Add($"{Constants.ProviderSection}:CallbackPath must be {Constants.Callback}.");
        if (!string.IsNullOrEmpty(options.ClientSecret) || !string.IsNullOrEmpty(options.Authority) ||
            !string.IsNullOrEmpty(options.AuthorizationEndpoint) || !string.IsNullOrEmpty(options.TokenEndpoint) || !string.IsNullOrEmpty(options.UserInfoEndpoint))
            errors.Add($"Remove fixed issuer, endpoint and secret settings from {Constants.ProviderSection}; AT identity discovers its issuer and this connector is a public client.");
        var configured = settings.Value;
        var development = KoanEnv.Gate.DevelopmentOnly(environment);
        if ((!development) && (configured.DevelopmentAllowedOrigins.Length > 0 || configured.DevelopmentHandles.Count > 0 || configured.DevelopmentPlcDirectory is not null || configured.DevelopmentConnectHost is not null))
            errors.Add($"{Constants.Section}:DevelopmentAllowedOrigins and DevelopmentHandles require Development; remove them outside that environment.");
        if (configured.MaximumResponseBytes is < 1024 or > 64 * 1024 * 1024 || configured.RequestTimeoutSeconds is < 1 or > 120)
            errors.Add($"{Constants.Section}:MaximumResponseBytes must be 1024..67108864 and RequestTimeoutSeconds 1..120.");
        if (string.IsNullOrWhiteSpace(configured.SessionDirectory)) errors.Add($"Set {Constants.Section}:SessionDirectory to durable private storage.");
        foreach (var origin in configured.DevelopmentAllowedOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.GetLeftPart(UriPartial.Authority) != origin ||
                parsed.UserInfo.Length > 0 || parsed.Scheme is not ("http" or "https"))
                errors.Add($"{Constants.Section}:DevelopmentAllowedOrigins entries must be exact HTTP(S) origins without paths or credentials.");
        var scopes = options.Scopes ?? [Constants.BaseScope];
        if (configured.DevelopmentConnectHost is { } connectHost && Uri.CheckHostName(connectHost) == UriHostNameType.Unknown)
            errors.Add($"{Constants.Section}:DevelopmentConnectHost must be a hostname or IP address without a scheme, path or port.");
        if (configured.DevelopmentPlcDirectory is { } fixturePlc &&
            (!Uri.TryCreate(fixturePlc, UriKind.Absolute, out var fixtureUri) || fixtureUri.UserInfo.Length > 0 || fixtureUri.Query.Length > 0 || fixtureUri.Fragment.Length > 0 ||
             !configured.DevelopmentAllowedOrigins.Contains(fixtureUri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal)))
            errors.Add($"{Constants.Section}:DevelopmentPlcDirectory must use an exact allowed Development origin.");
        if (!scopes.Contains(Constants.BaseScope, StringComparer.Ordinal) || scopes.Any(x => string.IsNullOrWhiteSpace(x) || x.Any(char.IsWhiteSpace)))
            errors.Add($"{Constants.ProviderSection}:Scopes must include atproto and contain one scope token per array entry.");
        if (!Uri.TryCreate(options.ClientId, UriKind.Absolute, out var clientId) || clientId.UserInfo.Length != 0 || clientId.Fragment.Length != 0)
            errors.Add($"Set {Constants.ProviderSection}:ClientId to this application's absolute client-metadata URL.");
        else if (clientId.Scheme == "https")
        {
            if (!clientId.IsDefaultPort || clientId.Query.Length > 0 || clientId.AbsolutePath != Constants.Metadata)
                errors.Add($"{Constants.ProviderSection}:ClientId must be HTTPS on the default port at {Constants.Metadata}, without a query.");
        }
        else
        {
            var query = HttpUtility.ParseQueryString(clientId.Query);
            if (!development || clientId.Scheme != "http" || clientId.Host != "localhost" || !clientId.IsDefaultPort || clientId.AbsolutePath != "/" ||
                query.GetValues("redirect_uri")?.Length != 1 || !Uri.TryCreate(query["redirect_uri"], UriKind.Absolute, out var redirect) ||
                redirect.Scheme != "http" || !redirect.IsLoopback || redirect.Host == "localhost" || redirect.AbsolutePath != Constants.Callback ||
                redirect.Query.Length > 0 || redirect.Fragment.Length > 0 || redirect.UserInfo.Length > 0 ||
                query.GetValues("scope")?.Length != 1 || query["scope"] != string.Join(' ', scopes))
                errors.Add($"{Constants.ProviderSection}:ClientId may use the special localhost profile only in Development, with one IP-loopback redirect_uri at {Constants.Callback} and scope equal to the declared Scopes.");
        }
        if (!Uri.TryCreate(configured.PlcDirectory, UriKind.Absolute, out var plc) || plc.UserInfo.Length > 0 || plc.Query.Length > 0 || plc.Fragment.Length > 0 ||
            (plc.Scheme != "https" && !(development && configured.DevelopmentAllowedOrigins.Contains(plc.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))))
            errors.Add($"{Constants.Section}:PlcDirectory must be HTTPS, or an explicitly allowed Development origin.");
        return errors;
    }
}
