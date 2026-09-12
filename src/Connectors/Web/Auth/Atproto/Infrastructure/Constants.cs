namespace Koan.Web.Auth.Connector.Atproto.Infrastructure;

internal static class Constants
{
    internal const string Provider = "atproto";
    internal const string Protocol = "atproto";
    internal const string Section = "Koan:Web:Auth:Atproto";
    internal const string ProviderSection = "Koan:Web:Auth:Providers:atproto";
    internal const string Callback = "/auth/atproto/callback";
    internal const string Metadata = "/auth/atproto/client-metadata.json";
    internal const string RequestedScopes = ".atproto.requested_scopes";
    internal const string ProtectionPurpose = "Koan.Web.Auth.Atproto.Protocol.v1";
    internal const string PropertiesPurpose = "Koan.Web.Auth.Atproto.Browser.v1";
    internal const string BaseScope = "atproto";
    internal const string PdsServiceType = "AtprotoPersonalDataServer";
}
