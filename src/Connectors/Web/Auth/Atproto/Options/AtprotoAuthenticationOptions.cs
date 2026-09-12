using Microsoft.AspNetCore.Authentication;

namespace Koan.Web.Auth.Connector.Atproto.Options;

public sealed class AtprotoAuthenticationOptions : RemoteAuthenticationOptions
{
    public ISecureDataFormat<AuthenticationProperties> StateDataFormat { get; set; } = null!;
}
