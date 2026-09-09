using Koan.Data.Abstractions.Annotations;
using Koan.Data.Core.Model;
using Koan.Identity;
using Koan.Tenancy;

namespace Koan.Identity.Tenancy.Invitations;

/// <summary>The lifecycle of one invitation token. <see cref="Pending"/> → <see cref="Claimed"/> →
/// <see cref="Accepted"/> is the happy path; <see cref="Revoked"/> races the claim (an operator escape
/// hatch usable while the token is unclaimed or its holder has not completed the seat), and
/// <see cref="Expired"/> is a derived outcome — the row keeps its stored state and
/// <see cref="TenantInvite.IsRedeemable"/> simply goes false at the expiry instant.</summary>
public enum TenantInviteStatus
{
    Pending = 0,
    Claimed = 1,
    Accepted = 2,
    Revoked = 3,
    Expired = 4,
}

/// <summary>
/// An invitation to one person (matched by a <b>verified</b> <see cref="IdentityEmail"/>) to take one
/// <see cref="Membership"/> seat in one tenant. The row IS the contested resource (PMC-035): claiming is a
/// conditional write on <c>Status == Pending &amp;&amp; ClaimedBy == null</c>, so two identities racing one token
/// across hosts converge to one claimant and one seat. The raw token is never stored — only its SHA-256 hash —
/// and the row is <c>[HostScoped]</c> control-plane state: acceptance runs before any tenant scope exists.
/// </summary>
[HostScoped]
[Index(Name = "ix_tenantinvite_tokenhash")]
[Index(Name = "ix_tenantinvite_tenant")]
public sealed class TenantInvite : Entity<TenantInvite>
{
    /// <summary>The tenant whose seat this invitation grants.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>The normalized (<see cref="IdentityEmail.Normalize"/>) address the invitation was issued to.
    /// Provenance and operator display — authorization binds to a verified email owned by the claimant.</summary>
    public string Email { get; set; } = "";

    /// <summary>The single tenant role the seat will carry. Reserved host roles are refused at issue and at claim.</summary>
    public string Role { get; set; } = "";

    /// <summary>SHA-256 of the raw token (hex). The raw token is returned exactly once at issue and never persisted.</summary>
    public string TokenHash { get; set; } = "";

    public TenantInviteStatus Status { get; set; } = TenantInviteStatus.Pending;

    /// <summary>The canonical identity id that won the claim. Null while <see cref="TenantInviteStatus.Pending"/>.</summary>
    public string? ClaimedBy { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>How many claim attempts this row has seen — a claim plus every same-claimant recovery re-drive.</summary>
    public int ClaimAttempts { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>After this instant the token no longer redeems. Checked inside the claim guard, so expiry races resolve.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    [Timestamp]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>One hashing rule for issue and acceptance so lookups always compare like for like.</summary>
    public static string HashToken(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawToken)));
    }

    /// <summary>Whether the row could still be claimed at <paramref name="now"/>. Expiry is checked inside the
    /// claim guard too, so a row that expires between read and claim loses the CAS like any other loser.</summary>
    public bool IsRedeemable(DateTimeOffset now) => Status == TenantInviteStatus.Pending && now < ExpiresAt;

    /// <summary>Deep copy for CAS attempts: the guarded write must carry a mutated copy, never alias the
    /// caller's read snapshot (same convergence rule as the durable tier's store round-trips).</summary>
    public TenantInvite Clone() => (TenantInvite)MemberwiseClone();
}
