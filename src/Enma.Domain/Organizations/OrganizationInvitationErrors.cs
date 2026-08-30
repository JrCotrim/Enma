namespace Enma.Domain.Organizations;

public static class OrganizationInvitationErrors
{
    public const string OrganizationIdRequired =
        "Organization id cannot be empty.";
    public const string CreatedByMembershipIdRequired =
        "Creator membership id cannot be empty.";
    public const string RoleInvalid =
        "Invitation role must be Administrator or Member.";
    public const string TokenHashRequired =
        "Organization invitation token hash is required.";
    public const string TokenHashLengthInvalid =
        "Organization invitation token hash must contain exactly 32 bytes.";
    public const string CreatedAtInvalid =
        "Organization invitation creation timestamp is invalid.";
    public const string TokenIssuedAtInvalid =
        "Organization invitation token issue timestamp cannot be before creation.";
    public const string TokenIssuedAtCannotMoveBackward =
        "Organization invitation token issue timestamp cannot move backward.";
    public const string ExpiresAtInvalid =
        "Organization invitation expiration must be after token issuance.";
    public const string AcceptedByUserIdRequired =
        "Accepted-by user id cannot be empty.";
    public const string AcceptedAtInvalid =
        "Organization invitation acceptance must occur while the token is valid.";
    public const string RevokedAtInvalid =
        "Organization invitation revocation must occur while the token is valid.";
    public const string ExpirationNotObserved =
        "Organization invitation cannot be expired before its expiration timestamp.";
    public const string InvitationTerminal =
        "A terminal organization invitation cannot transition again.";
    public const string InvitationExpired =
        "An expired organization invitation cannot rotate its token.";
    public const string TokenHashMustChange =
        "Organization invitation token rotation must use a new token hash.";
}
