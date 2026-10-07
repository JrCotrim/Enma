namespace Enma.Domain.Organizations;

public sealed class OrganizationMembership
{
    public OrganizationMembership(
        Guid organizationId,
        Guid userId,
        OrganizationRole role,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                OrganizationMembershipErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                OrganizationMembershipErrors.UserIdRequired,
                nameof(userId));
        }

        OrganizationRole validatedRole = ValidateRole(role);

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                OrganizationMembershipErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        UserId = userId;
        Role = validatedRole;
        IsActive = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public OrganizationRole Role { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeRole(OrganizationRole role)
    {
        Role = ValidateRole(role);
    }

    /// <summary>
    /// Ownership transfer only: the transferring Owner always becomes an Administrator.
    /// </summary>
    public void DemoteOwnerToAdministrator()
    {
        if (!IsActive || Role != OrganizationRole.Owner)
        {
            throw new InvalidOperationException(
                OrganizationMembershipErrors.OwnerDemotionInvalidState);
        }

        Role = OrganizationRole.Administrator;
    }

    /// <summary>
    /// Ownership transfer only: an active Administrator becomes the Owner.
    /// </summary>
    public void PromoteAdministratorToOwner()
    {
        if (!IsActive || Role != OrganizationRole.Administrator)
        {
            throw new InvalidOperationException(
                OrganizationMembershipErrors.OwnerPromotionInvalidState);
        }

        Role = OrganizationRole.Owner;
    }

    public void Activate()
    {
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }

    private static OrganizationRole ValidateRole(OrganizationRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                OrganizationMembershipErrors.RoleInvalid);
        }

        return role;
    }
}
