using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class CalendarEventActionAuthorizationTests
{
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "db1f2e87-fb75-4247-ad51-8ce673110924");
    private static readonly Guid OtherMembershipId = Guid.Parse(
        "2ed51807-6e80-4199-9477-725572833a3c");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public void ViewAndCreate_AllMvpRolesAreAllowed(OrganizationRole role)
    {
        var authorization = new CalendarEventActionAuthorization();

        Assert.True(authorization.CanView(role));
        Assert.True(authorization.CanCreate(role));
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public void PrivilegedRoles_CanMutateAnyEventAndUseAnyAssignee(
        OrganizationRole role)
    {
        var authorization = new CalendarEventActionAuthorization();
        var otherEvent = new CalendarEventAuthorizationState(OtherMembershipId);

        Assert.True(authorization.CanAssignDuringCreate(
            role,
            ActorMembershipId,
            OtherMembershipId));
        Assert.True(authorization.CanRequestAssigneeChange(
            role,
            ActorMembershipId,
            OtherMembershipId));
        Assert.True(authorization.CanUpdate(
            role,
            ActorMembershipId,
            otherEvent));
        Assert.True(authorization.CanChangeAssignee(
            role,
            ActorMembershipId,
            otherEvent,
            OtherMembershipId));
        Assert.True(authorization.CanDelete(
            role,
            ActorMembershipId,
            otherEvent));
    }

    [Fact]
    public void Member_CanMutateCreatedEventRegardlessOfCurrentAssignee()
    {
        var authorization = new CalendarEventActionAuthorization();
        var ownEvent = new CalendarEventAuthorizationState(ActorMembershipId);

        Assert.True(authorization.CanUpdate(
            OrganizationRole.Member,
            ActorMembershipId,
            ownEvent));
        Assert.True(authorization.CanDelete(
            OrganizationRole.Member,
            ActorMembershipId,
            ownEvent));
        Assert.True(authorization.CanChangeAssignee(
            OrganizationRole.Member,
            ActorMembershipId,
            ownEvent,
            ActorMembershipId));
        Assert.True(authorization.CanChangeAssignee(
            OrganizationRole.Member,
            ActorMembershipId,
            ownEvent,
            null));
    }

    [Fact]
    public void Member_AssignmentMatrix_AllowsOnlySelfOrNull()
    {
        var authorization = new CalendarEventActionAuthorization();
        var ownEvent = new CalendarEventAuthorizationState(ActorMembershipId);

        Assert.True(authorization.CanAssignDuringCreate(
            OrganizationRole.Member,
            ActorMembershipId,
            null));
        Assert.True(authorization.CanAssignDuringCreate(
            OrganizationRole.Member,
            ActorMembershipId,
            ActorMembershipId));
        Assert.False(authorization.CanAssignDuringCreate(
            OrganizationRole.Member,
            ActorMembershipId,
            OtherMembershipId));
        Assert.False(authorization.CanRequestAssigneeChange(
            OrganizationRole.Member,
            ActorMembershipId,
            OtherMembershipId));
        Assert.False(authorization.CanChangeAssignee(
            OrganizationRole.Member,
            ActorMembershipId,
            ownEvent,
            OtherMembershipId));
    }

    [Fact]
    public void Member_AssigneeWithoutCreatorOwnership_CannotMutateEvent()
    {
        var authorization = new CalendarEventActionAuthorization();
        var eventCreatedByOther = new CalendarEventAuthorizationState(
            OtherMembershipId);

        Assert.False(authorization.CanUpdate(
            OrganizationRole.Member,
            ActorMembershipId,
            eventCreatedByOther));
        Assert.False(authorization.CanChangeAssignee(
            OrganizationRole.Member,
            ActorMembershipId,
            eventCreatedByOther,
            ActorMembershipId));
        Assert.False(authorization.CanDelete(
            OrganizationRole.Member,
            ActorMembershipId,
            eventCreatedByOther));
    }

    [Fact]
    public void UnknownRole_DeniesEveryCalendarEventAction()
    {
        var authorization = new CalendarEventActionAuthorization();
        OrganizationRole unknown = (OrganizationRole)int.MaxValue;
        var ownEvent = new CalendarEventAuthorizationState(ActorMembershipId);

        Assert.False(authorization.CanView(unknown));
        Assert.False(authorization.CanCreate(unknown));
        Assert.False(authorization.CanAssignDuringCreate(
            unknown,
            ActorMembershipId,
            null));
        Assert.False(authorization.CanRequestAssigneeChange(
            unknown,
            ActorMembershipId,
            null));
        Assert.False(authorization.CanUpdate(
            unknown,
            ActorMembershipId,
            ownEvent));
        Assert.False(authorization.CanChangeAssignee(
            unknown,
            ActorMembershipId,
            ownEvent,
            null));
        Assert.False(authorization.CanDelete(
            unknown,
            ActorMembershipId,
            ownEvent));
    }
}
