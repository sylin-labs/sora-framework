using System.Security.Cryptography;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Core;
using Koan.Identity;
using Koan.Tenancy;

namespace Koan.Identity.Tenancy.Invitations;

public sealed record IssuedInvite(TenantInvite Invite, string Token);

/// <summary>
/// Operator-side issuance and revocation for <see cref="TenantInvite"/> rows. The raw token exists
/// exactly once — in the <see cref="IssuedInvite.Token"/> of the issue result — and only its SHA-256
/// hash is ever persisted. Both mutations are audited through <see cref="TenantAuditEntry"/>.
/// </summary>
public sealed class InviteIssuanceService
{
    /// <summary>Issue one invitation. Returns the raw token once; only its hash is stored.</summary>
    public async Task<IssuedInvite> IssueAsync(
        string actor, string tenantId, string email, string role,
        DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        if (await TenantRecord.Get(tenantId, ct).ConfigureAwait(false) is null)
            throw new InvalidOperationException(
                $"Cannot issue an invitation for tenant '{tenantId}' — no such tenant exists.");

        if (TenancyRoles.IsReservedHostRole(role))
            throw new InvalidOperationException(
                $"Role '{role}' is a reserved host role and cannot travel through an invitation. " +
                "Grant host roles through the host's own operator surface, never by invitation.");

        var normalized = IdentityEmail.Normalize(email);
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        var invite = await new TenantInvite
        {
            TenantId = tenantId,
            Email = normalized,
            Role = role,
            TokenHash = TenantInvite.HashToken(rawToken),
            Status = TenantInviteStatus.Pending,
            ExpiresAt = expiresAt ?? now + TimeSpan.FromDays(7),
            CreatedAt = now,
        }.Save(ct).ConfigureAwait(false);

        await TenantAuditEntry.Record(
            actor, "invite.issued", tenantId,
            $"invitation {invite.Id} for {normalized} granting '{role}' (expires {invite.ExpiresAt:O}).",
            ct).ConfigureAwait(false);

        return new IssuedInvite(invite, rawToken);
    }

    /// <summary>Revoke an invitation that has not been accepted. Revocation races the claim: whichever
    /// conditional write lands first wins, and the loser reports honestly. Returns false when the row
    /// was not found or had already left the revocable states.</summary>
    public async Task<bool> RevokeAsync(string actor, string inviteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);

        var invite = await TenantInvite.Get(inviteId, ct).ConfigureAwait(false);
        if (invite is null) return false;
        if (invite.Status is not (TenantInviteStatus.Pending or TenantInviteStatus.Claimed)) return false;

        var revoked = invite.Clone();
        revoked.Status = TenantInviteStatus.Revoked;
        if (!await TryRevokeAsync(revoked, ct).ConfigureAwait(false)) return false;

        await TenantAuditEntry.Record(
            actor, "invite.revoked", invite.TenantId,
            $"invitation {invite.Id} for {invite.Email} revoked while {invite.Status}.",
            ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> TryRevokeAsync(TenantInvite revoked, CancellationToken ct)
    {
        if (Data<TenantInvite, string>.Capabilities.Has(DataCaps.Write.ConditionalReplace))
        {
            var cas = Data<TenantInvite, string>.As<IConditionalWriteRepository<TenantInvite, string>>()
                ?? throw new NotSupportedException("The selected repository advertises conditional replacement but cannot execute it.");
            return await cas.ConditionalReplaceAsync(
                revoked,
                Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<TenantInvite>(r => r.Status == TenantInviteStatus.Pending || r.Status == TenantInviteStatus.Claimed),
                ct).ConfigureAwait(false);
        }

        // Verify-then-write floor: revoke is an operator action, not a contested claim, so the same
        // at-least-once honesty the claim's floor carries is acceptable here.
        var stored = await TenantInvite.Get(revoked.Id, ct).ConfigureAwait(false);
        if (stored is null
            || stored.Status is not (TenantInviteStatus.Pending or TenantInviteStatus.Claimed)) return false;
        await revoked.Save(ct).ConfigureAwait(false);
        return true;
    }
}
