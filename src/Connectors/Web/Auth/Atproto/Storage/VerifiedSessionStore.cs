using CarpaNet.OAuth.Storage;

namespace Koan.Web.Auth.Connector.Atproto.Storage;

/// <summary>SDK callback writes stay private until browser, DID, PDS and issuer checks all pass.</summary>
internal sealed class VerifiedSessionStore(ProtectedAtprotoStore durable,
    Func<OAuthSessionData, CancellationToken, Task>? verifyInitial = null) : IOAuthSessionStore
{
    private OAuthSessionData? staged;
    private string? verifiedDid;
    internal string? GrantedScope => staged?.TokenSet.Scope;

    internal Task Commit(string did, CancellationToken ct)
    {
        if (staged?.TokenSet.Sub != did) throw new InvalidOperationException("Unverified AT session cannot be persisted.");
        verifiedDid = did;
        return durable.StoreAsync(did, staged, ct);
    }

    public async Task StoreAsync(string sub, OAuthSessionData value, CancellationToken cancellationToken = default)
    {
        if (sub != value.TokenSet.Sub || (verifiedDid is not null && sub != verifiedDid)) throw new InvalidOperationException("AT token subject changed.");
        if (verifiedDid is not null) { await durable.StoreAsync(sub, value, cancellationToken); return; }
        if (verifyInitial is not null) await verifyInitial(value, cancellationToken);
        staged = value;
    }

    public Task<OAuthSessionData?> GetAsync(string sub, CancellationToken cancellationToken = default)
        => verifiedDid is null ? Task.FromResult<OAuthSessionData?>(null) : durable.GetAsync(sub, cancellationToken);

    public Task DeleteAsync(string sub, CancellationToken cancellationToken = default)
        => verifiedDid is null ? Task.CompletedTask : durable.DeleteAsync(sub, cancellationToken);
}
