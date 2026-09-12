using System.Text.Json;
using Koan.Web.Auth.Connector.Atproto.Infrastructure;
using Microsoft.AspNetCore.Authentication;

namespace Koan.Web.Auth.Connector.Atproto;

public static class AtprotoChallenge
{
    /// <summary>Request additional declared permissions from trusted server code. Ordinary sign-in requests only atproto.</summary>
    public static AuthenticationProperties WithScopes(AuthenticationProperties properties, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(scopes);
        properties.Items[Constants.RequestedScopes] = JsonSerializer.Serialize(scopes);
        return properties;
    }
}
