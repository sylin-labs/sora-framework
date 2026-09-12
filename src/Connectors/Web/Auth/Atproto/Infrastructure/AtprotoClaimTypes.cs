namespace Koan.Web.Auth.Connector.Atproto;

/// <summary>Protocol identity claims retained independently of an application's canonical local user identifier.</summary>
public static class AtprotoClaimTypes
{
    public const string Did = "atproto:did";
    public const string Handle = "atproto:handle";
}
