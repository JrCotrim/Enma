using Enma.Domain.Users;

namespace Enma.Domain.Organizations;

public sealed class OrganizationInvitation
{
    private OrganizationInvitation()
    {
        InvitedEmail = null!;
    }

    public OrganizationInvitation(
        Guid organizationId,
        string invitedEmail,
        OrganizationRole role,
        Guid createdByMembershipId,
        OrganizationInvitationTokenHash tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset tokenIssuedAt,
        DateTimeOffset expiresAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                OrganizationInvitationErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (createdByMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                OrganizationInvitationErrors.CreatedByMembershipIdRequired,
                nameof(createdByMembershipId));
        }

        if (role is not (
            OrganizationRole.Administrator or OrganizationRole.Member))
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                OrganizationInvitationErrors.RoleInvalid);
        }

        if (tokenHash is null)
        {
            throw new ArgumentNullException(
                nameof(tokenHash),
                OrganizationInvitationErrors.TokenHashRequired);
        }

        DateTimeOffset normalizedCreatedAt = ValidateCreatedAt(createdAt);
        DateTimeOffset normalizedTokenIssuedAt = tokenIssuedAt.ToUniversalTime();
        DateTimeOffset normalizedExpiresAt = expiresAt.ToUniversalTime();
        ValidateIssueWindow(
            normalizedCreatedAt,
            normalizedTokenIssuedAt,
            normalizedExpiresAt);

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        InvitedEmail = User.NormalizeEmail(invitedEmail);
        Role = role;
        CreatedByMembershipId = createdByMembershipId;
        TokenHash = tokenHash;
        CreatedAt = normalizedCreatedAt;
        TokenIssuedAt = normalizedTokenIssuedAt;
        ExpiresAt = normalizedExpiresAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string InvitedEmail { get; private set; }

    public OrganizationRole Role { get; private set; }

    public Guid CreatedByMembershipId { get; private set; }

    public OrganizationInvitationTokenHash? TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset TokenIssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedByUserId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset? ExpiredAt { get; private set; }

    public OrganizationInvitationState GetState(DateTimeOffset now)
    {
        if (AcceptedAt.HasValue)
        {
            return OrganizationInvitationState.Accepted;
        }

        if (RevokedAt.HasValue)
        {
            return OrganizationInvitationState.Revoked;
        }

        if (ExpiredAt.HasValue || now.ToUniversalTime() >= ExpiresAt)
        {
            return OrganizationInvitationState.Expired;
        }

        return OrganizationInvitationState.Pending;
    }

    public void Accept(Guid acceptedByUserId, DateTimeOffset acceptedAt)
    {
        ThrowIfTerminal();

        if (acceptedByUserId == Guid.Empty)
        {
            throw new ArgumentException(
                OrganizationInvitationErrors.AcceptedByUserIdRequired,
                nameof(acceptedByUserId));
        }

        DateTimeOffset normalizedAcceptedAt = acceptedAt.ToUniversalTime();

        if (normalizedAcceptedAt < TokenIssuedAt ||
            normalizedAcceptedAt >= ExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acceptedAt),
                OrganizationInvitationErrors.AcceptedAtInvalid);
        }

        AcceptedAt = normalizedAcceptedAt;
        AcceptedByUserId = acceptedByUserId;
        TokenHash = null;
    }

    public void Revoke(DateTimeOffset revokedAt)
    {
        ThrowIfTerminal();
        DateTimeOffset normalizedRevokedAt = revokedAt.ToUniversalTime();

        if (normalizedRevokedAt < TokenIssuedAt ||
            normalizedRevokedAt >= ExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revokedAt),
                OrganizationInvitationErrors.RevokedAtInvalid);
        }

        RevokedAt = normalizedRevokedAt;
        TokenHash = null;
    }

    public void Expire(DateTimeOffset observedAt)
    {
        ThrowIfTerminal();

        if (observedAt.ToUniversalTime() < ExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAt),
                OrganizationInvitationErrors.ExpirationNotObserved);
        }

        ExpiredAt = ExpiresAt;
        TokenHash = null;
    }

    public void RotateToken(
        OrganizationInvitationTokenHash tokenHash,
        DateTimeOffset tokenIssuedAt,
        DateTimeOffset expiresAt)
    {
        ThrowIfTerminal();

        if (tokenHash is null)
        {
            throw new ArgumentNullException(
                nameof(tokenHash),
                OrganizationInvitationErrors.TokenHashRequired);
        }

        DateTimeOffset normalizedTokenIssuedAt = tokenIssuedAt.ToUniversalTime();
        DateTimeOffset normalizedExpiresAt = expiresAt.ToUniversalTime();

        if (normalizedTokenIssuedAt >= ExpiresAt)
        {
            throw new InvalidOperationException(
                OrganizationInvitationErrors.InvitationExpired);
        }

        if (normalizedTokenIssuedAt < TokenIssuedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenIssuedAt),
                OrganizationInvitationErrors.TokenIssuedAtCannotMoveBackward);
        }

        ValidateIssueWindow(
            CreatedAt,
            normalizedTokenIssuedAt,
            normalizedExpiresAt);

        if (TokenHash!.Equals(tokenHash))
        {
            throw new ArgumentException(
                OrganizationInvitationErrors.TokenHashMustChange,
                nameof(tokenHash));
        }

        TokenHash = tokenHash;
        TokenIssuedAt = normalizedTokenIssuedAt;
        ExpiresAt = normalizedExpiresAt;
    }

    private static DateTimeOffset ValidateCreatedAt(DateTimeOffset createdAt)
    {
        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                OrganizationInvitationErrors.CreatedAtInvalid);
        }

        return createdAt.ToUniversalTime();
    }

    private static void ValidateIssueWindow(
        DateTimeOffset createdAt,
        DateTimeOffset tokenIssuedAt,
        DateTimeOffset expiresAt)
    {
        if (tokenIssuedAt < createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenIssuedAt),
                OrganizationInvitationErrors.TokenIssuedAtInvalid);
        }

        if (expiresAt <= tokenIssuedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                OrganizationInvitationErrors.ExpiresAtInvalid);
        }
    }

    private void ThrowIfTerminal()
    {
        if (AcceptedAt.HasValue || RevokedAt.HasValue || ExpiredAt.HasValue)
        {
            throw new InvalidOperationException(
                OrganizationInvitationErrors.InvitationTerminal);
        }
    }
}
