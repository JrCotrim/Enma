using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class LegalTaskMutationAuthorizationTests
{
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "53b97de8-4dd5-4c7b-b781-98fedf22a436");
    private static readonly Guid OtherMembershipId = Guid.Parse(
        "fce6646e-c25b-41af-a213-c0d3d9dcc292");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public void CanUpdateOrChangeLifecycle_PrivilegedRole_AllowsAnyTask(
        OrganizationRole role)
    {
        var authorization = new LegalTaskMutationAuthorization();

        bool allowed = authorization.CanUpdateOrChangeLifecycle(
            role,
            ActorMembershipId,
            CreateTaskState(OtherMembershipId, OtherMembershipId));

        Assert.True(allowed);
    }

    [Theory]
    [MemberData(nameof(MemberOwnershipMatrix))]
    public void CanUpdateOrChangeLifecycle_Member_UsesCurrentOwnershipRule(
        Guid? assigneeMembershipId,
        Guid createdByMembershipId,
        bool expected)
    {
        var authorization = new LegalTaskMutationAuthorization();

        bool allowed = authorization.CanUpdateOrChangeLifecycle(
            OrganizationRole.Member,
            ActorMembershipId,
            CreateTaskState(assigneeMembershipId, createdByMembershipId));

        Assert.Equal(expected, allowed);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public void CanChangeAssignee_PrivilegedRole_AllowsAnyTransition(
        OrganizationRole role)
    {
        var authorization = new LegalTaskMutationAuthorization();

        bool allowed = authorization.CanChangeAssignee(
            role,
            ActorMembershipId,
            OtherMembershipId,
            Guid.NewGuid());

        Assert.True(allowed);
    }

    [Theory]
    [MemberData(nameof(MemberAssignmentMatrix))]
    public void CanChangeAssignee_Member_UsesExactTransitionMatrix(
        AssigneeSelection current,
        AssigneeSelection requested,
        bool expected)
    {
        var authorization = new LegalTaskMutationAuthorization();

        bool allowed = authorization.CanChangeAssignee(
            OrganizationRole.Member,
            ActorMembershipId,
            Resolve(current),
            Resolve(requested));

        Assert.Equal(expected, allowed);
    }

    [Fact]
    public void Authorization_UnknownRole_DeniesAllMutations()
    {
        var authorization = new LegalTaskMutationAuthorization();
        var task = CreateTaskState(null, ActorMembershipId);
        OrganizationRole unknownRole = (OrganizationRole)int.MaxValue;

        Assert.False(authorization.CanUpdateOrChangeLifecycle(
            unknownRole,
            ActorMembershipId,
            task));
        Assert.False(authorization.CanChangeAssignee(
            unknownRole,
            ActorMembershipId,
            null,
            null));
    }

    public static TheoryData<Guid?, Guid, bool> MemberOwnershipMatrix =>
        new()
        {
            { ActorMembershipId, OtherMembershipId, true },
            { null, ActorMembershipId, true },
            { null, OtherMembershipId, false },
            { OtherMembershipId, ActorMembershipId, false }
        };

    public static TheoryData<AssigneeSelection, AssigneeSelection, bool>
        MemberAssignmentMatrix =>
        new()
        {
            { AssigneeSelection.None, AssigneeSelection.None, true },
            { AssigneeSelection.None, AssigneeSelection.Self, true },
            { AssigneeSelection.None, AssigneeSelection.Other, false },
            { AssigneeSelection.Self, AssigneeSelection.None, true },
            { AssigneeSelection.Self, AssigneeSelection.Self, true },
            { AssigneeSelection.Self, AssigneeSelection.Other, false },
            { AssigneeSelection.Other, AssigneeSelection.None, false },
            { AssigneeSelection.Other, AssigneeSelection.Self, false },
            { AssigneeSelection.Other, AssigneeSelection.Other, false }
        };

    private static LegalTaskMutationTaskState CreateTaskState(
        Guid? assigneeMembershipId,
        Guid createdByMembershipId)
    {
        return new LegalTaskMutationTaskState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            assigneeMembershipId,
            createdByMembershipId,
            null);
    }

    private static Guid? Resolve(AssigneeSelection selection)
    {
        return selection switch
        {
            AssigneeSelection.None => null,
            AssigneeSelection.Self => ActorMembershipId,
            AssigneeSelection.Other => OtherMembershipId,
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
    }

    public enum AssigneeSelection
    {
        None = 0,
        Self = 1,
        Other = 2
    }
}
