namespace Koan.Web.Auth.Connector.Atproto.Protocol;

/// <summary>The returned callback grant omitted access protected in the authorization request.</summary>
internal sealed class AtprotoMissingScopesException()
    : InvalidOperationException("The account provider did not grant all access required for this connection.");
