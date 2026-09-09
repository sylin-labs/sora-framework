using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Failures;
using Koan.Data.Core;
using Koan.Data.Core.Failures;
using Koan.Identity;
using Koan.Tenancy;

namespace Koan.Identity.Tenancy.Invitations;

public enum InviteAcceptOutcome
{
    /// <summary>The claimant took the seat and the invitation is consumed.</summary>
    Accepted = 0,
    /// <summary>The claimant already holds the seat; nothing was consumed.</summary>
    AlreadyMember = 1,
    /// <summary>No invitation matches the token.</summary>
    NotFound = 2,
    /// <summary>Another identity won the claim.</summary>
    AlreadyClaimed = 3,
    /// <summary>The token's expiry passed before the claim landed.</summary>
    Expired = 4,
    /// <summary>An operator revoked the invitation before the claim.</summary>
    Revoked = 5,
    /// <summary>The claimant does not own a <b>verified</b> email matching the invitation.</summary>
    EmailNotOwned = 6,
    /// <summary>The invited role is a reserved host role and cannot travel through an invitation.</summary>
    ReservedRoleRefused = 7,
}

public sealed record InviteAcceptResult(InviteAcceptOutcome Outcome, Membership? Membership)
{
    public bool TookSeat => Outcome == InviteAcceptOutcome.Accepted;
}

/// <summary>
/// The PMC-035 acceptance ceremony: one raw token, one verified claimant, exactly one seat. The
/// invitation row itself is the contested resource — the claim is a capability-REQUIRED conditional
/// write (<c>Status == Pending &amp;&amp; ClaimedBy == null</c>), so two identities racing one token across
/// hosts converge to one claimant. A claimant whose seat write was interrupted re-drives the same
/// token idempotently: the claim is theirs until they complete or an operator revokes.
/// </summary>
/// <remarks>
/// Recovery boundary (the retired design's hole): a row left <see cref="TenantInviteStatus.Claimed"/>
/// by an interrupted run is only ever continued by its own claimant, and the seat id is deterministic
/// (<see cref="Membership.KeyFor"/>), so retrying converges without a second seat and without
/// consuming a second invitation. Adapters without <c>write.conditionalReplace</c> are refused
/// correctively — an optimistic verify-then-write floor would resurrect the exact check-then-write
/// race this ceremony exists to close.
/// </remarks>
public sealed class InviteAcceptanceService
{
    private const int ClaimAttempts = 3;

    public async Task<InviteAcceptResult> AcceptAsync(string rawToken, string identityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        var cas = Cas() ?? throw new InvalidOperationException(
            "Invitation claiming requires the data store's conditional-write capability " +
            "('write.conditionalReplace') because two people may race one token. Reference a provider " +
            "that declares it (SQLite, PostgreSQL, SQL Server, MongoDB) for this application's default store.");

        var tokenHash = TenantInvite.HashToken(rawToken);
        var invite = (await TenantInvite.Query(i => i.TokenHash == tokenHash, ct).ConfigureAwait(false))
            .FirstOrDefault();
        if (invite is null) return new InviteAcceptResult(InviteAcceptOutcome.NotFound, null);

        var now = DateTimeOffset.UtcNow;
        switch (invite.Status)
        {
            case TenantInviteStatus.Accepted:
                var ownSeat = await Membership.Get(Membership.KeyFor(invite.TenantId, identityId), ct).ConfigureAwait(false);
                return ownSeat is not null
                    ? new InviteAcceptResult(InviteAcceptOutcome.AlreadyMember, ownSeat)
                    : new InviteAcceptResult(InviteAcceptOutcome.AlreadyClaimed, null);
            case TenantInviteStatus.Revoked:
                return new InviteAcceptResult(InviteAcceptOutcome.Revoked, null);
            case TenantInviteStatus.Claimed when !string.Equals(invite.ClaimedBy, identityId, StringComparison.Ordinal):
                return new InviteAcceptResult(InviteAcceptOutcome.AlreadyClaimed, null);
            case TenantInviteStatus.Pending when invite.ExpiresAt <= now:
                return new InviteAcceptResult(InviteAcceptOutcome.Expired, null);
        }

        // Inbox ownership: the claimant must hold a VERIFIED email matching the invitation. A leaked
        // token in the wrong inbox never reaches the claim.
        var ownsVerifiedEmail = (await IdentityEmail.Query(
                e => e.IdentityId == identityId && e.Verified, ct).ConfigureAwait(false))
            .Any(e => string.Equals(IdentityEmail.Normalize(e.Address), invite.Email, StringComparison.Ordinal));
        if (!ownsVerifiedEmail) return new InviteAcceptResult(InviteAcceptOutcome.EmailNotOwned, null);

        if (TenancyRoles.IsReservedHostRole(invite.Role))
            return new InviteAcceptResult(InviteAcceptOutcome.ReservedRoleRefused, null);

        var seatId = Membership.KeyFor(invite.TenantId, identityId);
        var existingSeat = await Membership.Get(seatId, ct).ConfigureAwait(false);
        if (existingSeat is not null && invite.Status == TenantInviteStatus.Pending)
            return new InviteAcceptResult(InviteAcceptOutcome.AlreadyMember, existingSeat);

        // --- the claim (the contested write) ---
        if (invite.Status == TenantInviteStatus.Pending)
        {
            ct.ThrowIfCancellationRequested();
            var claimed = invite.Clone();
            claimed.Status = TenantInviteStatus.Claimed;
            claimed.ClaimedBy = identityId;
            claimed.ClaimedAt = DateTimeOffset.UtcNow;
            claimed.ClaimAttempts++;
            if (await cas.ConditionalReplaceAsync(
                    claimed,
                    Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<TenantInvite>(r => r.Status == TenantInviteStatus.Pending
                         && r.ClaimedBy == null
                         && r.ExpiresAt > DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false))
            {
                invite = claimed;
            }
            else
            {
                // Lost the race or the store refused: re-read and classify honestly.
                var stored = await TenantInvite.Get(invite.Id, ct).ConfigureAwait(false)
                             ?? throw new InvalidOperationException($"Invitation {invite.Id} vanished during the claim.");
                if (stored.Status == TenantInviteStatus.Claimed
                    && string.Equals(stored.ClaimedBy, identityId, StringComparison.Ordinal))
                    invite = stored; // Recover our own interrupted claim below.
                else
                    return new InviteAcceptResult(Classify(stored), null);
            }
        }

        // --- seat materialization (idempotent: deterministic id, convergent upsert) ---
        var seat = await Membership.Get(seatId, ct).ConfigureAwait(false);
        if (seat is null)
        {
            seat = await new Membership
            {
                Id = seatId,
                TenantId = invite.TenantId,
                IdentityId = identityId,
                Roles = { invite.Role },
            }.Save(ct).ConfigureAwait(false);
        }

        // --- the accept (guarded to the claimant, so nothing else can complete it) ---
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var accepted = invite.Clone();
            accepted.Status = TenantInviteStatus.Accepted;
            accepted.AcceptedAt = DateTimeOffset.UtcNow;
            if (await cas.ConditionalReplaceAsync(
                    accepted,
                    Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<TenantInvite>(r => r.Status == TenantInviteStatus.Claimed
                         && r.ClaimedBy == identityId),
                    ct).ConfigureAwait(false))
            {
                break;
            }

            var stored = await TenantInvite.Get(invite.Id, ct).ConfigureAwait(false);
            if (stored is { Status: TenantInviteStatus.Accepted }
                && string.Equals(stored.ClaimedBy, identityId, StringComparison.Ordinal))
            {
                break;   // already completed by an earlier attempt of ours
            }
            if (attempt >= ClaimAttempts) break;   // leave Claimed; the claimant's retry is the recovery path
        }

        await TenantAuditEntry.Record(
            identityId,
            "invite.accepted",
            invite.TenantId,
            $"seat '{invite.Role}' claimed via invitation {invite.Id} (attempt {invite.ClaimAttempts}).",
            ct).ConfigureAwait(false);

        return new InviteAcceptResult(InviteAcceptOutcome.Accepted, seat);
    }

    private static InviteAcceptOutcome Classify(TenantInvite stored) => stored.Status switch
    {
        TenantInviteStatus.Accepted => InviteAcceptOutcome.AlreadyClaimed,
        TenantInviteStatus.Claimed => InviteAcceptOutcome.AlreadyClaimed,
        TenantInviteStatus.Revoked => InviteAcceptOutcome.Revoked,
        TenantInviteStatus.Expired => InviteAcceptOutcome.Expired,
        _ => stored.ExpiresAt <= DateTimeOffset.UtcNow
            ? InviteAcceptOutcome.Expired
            : InviteAcceptOutcome.AlreadyClaimed,
    };

    private static IConditionalWriteRepository<TenantInvite, string>? Cas()
        => Data<TenantInvite, string>.Capabilities.Has(DataCaps.Write.ConditionalReplace)
            ? Data<TenantInvite, string>.As<IConditionalWriteRepository<TenantInvite, string>>()
            : null;
}
