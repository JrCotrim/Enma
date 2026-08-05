using Enma.Domain.Organizations;

namespace Enma.UnitTests.Domain.Organizations;

public sealed class OrganizationMembershipTests
{
    private static readonly Guid OrganizationId = Guid.Parse("97b5cc52-b2bd-46f6-a2db-f878ce13a149");
    private static readonly Guid UserId = Guid.Parse("7e6646a7-dbb7-4241-a0f5-d6722cf3d3b0");
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidData_GeneratesId()
    {
        OrganizationMembership membership = CreateMembership();

        Assert.NotEqual(Guid.Empty, membership.Id);
    }

    [Fact]
    public void Constructor_WithValidData_StoresOrganizationAndUserIds()
    {
        OrganizationMembership membership = CreateMembership();

        Assert.Equal(OrganizationId, membership.OrganizationId);
        Assert.Equal(UserId, membership.UserId);
    }

    [Fact]
    public void Constructor_WithValidData_StoresRole()
    {
        OrganizationMembership membership = CreateMembership();

        Assert.Equal(OrganizationRole.Owner, membership.Role);
    }

    [Fact]
    public void Constructor_WithValidData_ActivatesMembership()
    {
        OrganizationMembership membership = CreateMembership();

        Assert.True(membership.IsActive);
    }

    [Fact]
    public void Constructor_WithValidData_StoresCreatedAt()
    {
        OrganizationMembership membership = CreateMembership();

        Assert.Equal(CreatedAt, membership.CreatedAt);
    }

    [Fact]
    public void Constructor_CalledTwice_GeneratesDistinctIds()
    {
        OrganizationMembership firstMembership = CreateMembership();
        OrganizationMembership secondMembership = CreateMembership();

        Assert.NotEqual(firstMembership.Id, secondMembership.Id);
    }

    [Fact]
    public void Constructor_WithEmptyOrganizationId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new OrganizationMembership(Guid.Empty, UserId, OrganizationRole.Owner, CreatedAt));

        Assert.Equal("organizationId", exception.ParamName);
        Assert.Contains(OrganizationMembershipErrors.OrganizationIdRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithEmptyUserId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new OrganizationMembership(OrganizationId, Guid.Empty, OrganizationRole.Owner, CreatedAt));

        Assert.Equal("userId", exception.ParamName);
        Assert.Contains(OrganizationMembershipErrors.UserIdRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithUndefinedRole_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrganizationMembership(OrganizationId, UserId, (OrganizationRole)999, CreatedAt));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains(OrganizationMembershipErrors.RoleInvalid, exception.Message);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrganizationMembership(
                OrganizationId,
                UserId,
                OrganizationRole.Owner,
                DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(OrganizationMembershipErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void ChangeRole_WithValidRole_UpdatesRole()
    {
        OrganizationMembership membership = CreateMembership();

        membership.ChangeRole(OrganizationRole.Administrator);

        Assert.Equal(OrganizationRole.Administrator, membership.Role);
    }

    [Fact]
    public void ChangeRole_WithUndefinedRole_ThrowsArgumentOutOfRangeException()
    {
        OrganizationMembership membership = CreateMembership();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            membership.ChangeRole((OrganizationRole)999));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains(OrganizationMembershipErrors.RoleInvalid, exception.Message);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
    }

    [Fact]
    public void Deactivate_WhenActive_DeactivatesMembership()
    {
        OrganizationMembership membership = CreateMembership();

        membership.Deactivate();
        membership.Deactivate();

        Assert.False(membership.IsActive);
    }

    [Fact]
    public void Activate_WhenInactive_ActivatesMembership()
    {
        OrganizationMembership membership = CreateMembership();
        membership.Deactivate();

        membership.Activate();
        membership.Activate();

        Assert.True(membership.IsActive);
    }

    private static OrganizationMembership CreateMembership()
    {
        return new OrganizationMembership(
            OrganizationId,
            UserId,
            OrganizationRole.Owner,
            CreatedAt);
    }
}
