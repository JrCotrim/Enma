namespace Enma.Domain.Authentication;

public sealed class AuthenticationSession
{
    public AuthenticationSession(
        Guid userId,
        AuthenticationSessionSecretHash secretHash,
        long credentialVersionAtIssue,
        DateTimeOffset createdAt,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset absoluteExpiresAt,
        Guid? selectedOrganizationId = null)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                AuthenticationSessionErrors.UserIdRequired,
                nameof(userId));
        }

        if (secretHash is null)
        {
            throw new ArgumentNullException(
                nameof(secretHash),
                AuthenticationSessionErrors.SecretHashRequired);
        }

        if (credentialVersionAtIssue <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(credentialVersionAtIssue),
                AuthenticationSessionErrors.CredentialVersionAtIssueInvalid);
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                AuthenticationSessionErrors.CreatedAtInvalid);
        }

        if (idleExpiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleExpiresAt),
                AuthenticationSessionErrors.IdleExpiresAtInvalid);
        }

        if (absoluteExpiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteExpiresAt),
                AuthenticationSessionErrors.AbsoluteExpiresAtInvalid);
        }

        if (idleExpiresAt > absoluteExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleExpiresAt),
                AuthenticationSessionErrors.IdleExpiresAtInvalid);
        }

        if (selectedOrganizationId == Guid.Empty)
        {
            throw new ArgumentException(
                AuthenticationSessionErrors.SelectedOrganizationIdInvalid,
                nameof(selectedOrganizationId));
        }

        Id = Guid.NewGuid();
        UserId = userId;
        SecretHash = secretHash;
        CredentialVersionAtIssue = credentialVersionAtIssue;
        SelectedOrganizationId = selectedOrganizationId;
        CreatedAt = createdAt;
        LastSeenAt = createdAt;
        IdleExpiresAt = idleExpiresAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
        RevokedAt = null;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public AuthenticationSessionSecretHash SecretHash { get; private set; }

    public long CredentialVersionAtIssue { get; private set; }

    public Guid? SelectedOrganizationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset IdleExpiresAt { get; private set; }

    public DateTimeOffset AbsoluteExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public long ConcurrencyVersion { get; private set; }

    public void Touch(
        DateTimeOffset seenAt,
        DateTimeOffset idleExpiresAt)
    {
        ThrowIfRevoked();

        if (seenAt < LastSeenAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seenAt),
                AuthenticationSessionErrors.LastSeenAtCannotMoveBackward);
        }

        if (seenAt >= IdleExpiresAt || seenAt >= AbsoluteExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seenAt),
                AuthenticationSessionErrors.IdleExpiresAtInvalid);
        }

        if (idleExpiresAt <= seenAt || idleExpiresAt > AbsoluteExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleExpiresAt),
                AuthenticationSessionErrors.IdleExpiresAtInvalid);
        }

        if (idleExpiresAt < IdleExpiresAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleExpiresAt),
                AuthenticationSessionErrors.IdleExpiresAtCannotMoveBackward);
        }

        if (seenAt == LastSeenAt && idleExpiresAt == IdleExpiresAt)
        {
            return;
        }

        long nextConcurrencyVersion = checked(ConcurrencyVersion + 1);

        LastSeenAt = seenAt;
        IdleExpiresAt = idleExpiresAt;
        ConcurrencyVersion = nextConcurrencyVersion;
    }

    public void Revoke(DateTimeOffset revokedAt)
    {
        if (revokedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revokedAt),
                AuthenticationSessionErrors.RevokedAtInvalid);
        }

        if (RevokedAt is not null)
        {
            return;
        }

        long nextConcurrencyVersion = checked(ConcurrencyVersion + 1);

        RevokedAt = revokedAt;
        ConcurrencyVersion = nextConcurrencyVersion;
    }

    public void SelectOrganization(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                AuthenticationSessionErrors.SelectedOrganizationIdInvalid,
                nameof(organizationId));
        }

        ThrowIfRevoked();

        if (SelectedOrganizationId == organizationId)
        {
            return;
        }

        long nextConcurrencyVersion = checked(ConcurrencyVersion + 1);

        SelectedOrganizationId = organizationId;
        ConcurrencyVersion = nextConcurrencyVersion;
    }

    public void ClearSelectedOrganization()
    {
        ThrowIfRevoked();

        if (SelectedOrganizationId is null)
        {
            return;
        }

        long nextConcurrencyVersion = checked(ConcurrencyVersion + 1);

        SelectedOrganizationId = null;
        ConcurrencyVersion = nextConcurrencyVersion;
    }

    private void ThrowIfRevoked()
    {
        if (RevokedAt is not null)
        {
            throw new InvalidOperationException(
                AuthenticationSessionErrors.SessionRevoked);
        }
    }
}
