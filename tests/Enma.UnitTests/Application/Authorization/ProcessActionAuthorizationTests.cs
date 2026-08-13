using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class ProcessActionAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "693e7eef-3868-4a3d-a975-d94ffad9d58d");

    private static readonly Guid OrganizationAId = Guid.Parse(
        "168b3365-128b-408e-af4f-69ffc0d0acf1");

    private static readonly Guid OrganizationBId = Guid.Parse(
        "39d80873-6845-4f38-b278-f3c28ee11a28");

    [Theory]
    [InlineData(ProcessAction.View, OrganizationRole.Owner, true)]
    [InlineData(ProcessAction.View, OrganizationRole.Administrator, true)]
    [InlineData(ProcessAction.View, OrganizationRole.Member, true)]
    [InlineData(ProcessAction.Create, OrganizationRole.Owner, true)]
    [InlineData(ProcessAction.Create, OrganizationRole.Administrator, true)]
    [InlineData(ProcessAction.Create, OrganizationRole.Member, false)]
    public async Task AuthorizeAsync_WithLiveRole_AppliesExplicitActionRule(
        ProcessAction action,
        OrganizationRole role,
        bool expectedAllowed)
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            role);
        ProcessActionAuthorization authorization = CreateAuthorization(lookup);

        ProcessActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            action);

        Assert.Equal(
            expectedAllowed
                ? ProcessActionAuthorizationResult.Allowed
                : ProcessActionAuthorizationResult.Denied,
            result);
    }

    [Fact]
    public async Task AuthorizeAsync_AfterLiveRoleChanges_UsesCurrentRoleEachTime()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Member);
        ProcessActionAuthorization authorization = CreateAuthorization(lookup);

        ProcessActionAuthorizationResult memberResult =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationAId,
                ProcessAction.Create);
        lookup.FirstRole = OrganizationRole.Administrator;
        ProcessActionAuthorizationResult administratorResult =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationAId,
                ProcessAction.Create);
        lookup.FirstRole = OrganizationRole.Member;
        ProcessActionAuthorizationResult changedBackResult =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationAId,
                ProcessAction.Create);

        Assert.Equal(ProcessActionAuthorizationResult.Denied, memberResult);
        Assert.Equal(ProcessActionAuthorizationResult.Allowed, administratorResult);
        Assert.Equal(ProcessActionAuthorizationResult.Denied, changedBackResult);
        Assert.Equal(3, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithContextualRoles_DoesNotBleedRoleAcrossOrganizations()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Member,
            OrganizationBId,
            OrganizationRole.Owner);
        ProcessActionAuthorization authorization = CreateAuthorization(lookup);

        ProcessActionAuthorizationResult viewA = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            ProcessAction.View);
        ProcessActionAuthorizationResult createA = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            ProcessAction.Create);
        ProcessActionAuthorizationResult viewB = await authorization.AuthorizeAsync(
            UserId,
            OrganizationBId,
            ProcessAction.View);
        ProcessActionAuthorizationResult createB = await authorization.AuthorizeAsync(
            UserId,
            OrganizationBId,
            ProcessAction.Create);

        Assert.Equal(ProcessActionAuthorizationResult.Allowed, viewA);
        Assert.Equal(ProcessActionAuthorizationResult.Denied, createA);
        Assert.Equal(ProcessActionAuthorizationResult.Allowed, viewB);
        Assert.Equal(ProcessActionAuthorizationResult.Allowed, createB);
    }

    [Fact]
    public async Task AuthorizeAsync_WithDeniedOrganizationAccess_ReturnsDenied()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            null);
        ProcessActionAuthorization authorization = CreateAuthorization(lookup);

        ProcessActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            ProcessAction.View);

        Assert.Equal(ProcessActionAuthorizationResult.Denied, result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorizeAsync_WithEmptyContext_DeniesWithoutOrganizationLookup(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Owner);
        ProcessActionAuthorization authorization = CreateAuthorization(lookup);

        ProcessActionAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationAId,
            ProcessAction.Create);

        Assert.Equal(ProcessActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedAction_DeniesWithoutOrganizationLookup()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Owner);
        ProcessActionAuthorization authorization = CreateAuthorization(lookup);

        ProcessActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            (ProcessAction)int.MaxValue);

        Assert.Equal(ProcessActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedLiveRole_ReturnsDenied()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            (OrganizationRole)int.MaxValue);
        ProcessActionAuthorization authorization = CreateAuthorization(lookup);

        ProcessActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            ProcessAction.View);

        Assert.Equal(ProcessActionAuthorizationResult.Denied, result);
    }

    [Fact]
    public void ActionContract_PublicValues_ContainsOnlyViewAndCreate()
    {
        Assert.Equal(
            [nameof(ProcessAction.View), nameof(ProcessAction.Create)],
            Enum.GetNames<ProcessAction>());
    }

    private static ProcessActionAuthorization CreateAuthorization(
        ContextualOrganizationAccessLookup lookup)
    {
        return new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(lookup));
    }

    private sealed class ContextualOrganizationAccessLookup(
        Guid firstOrganizationId,
        OrganizationRole? firstRole,
        Guid? secondOrganizationId = null,
        OrganizationRole? secondRole = null) : IOrganizationAccessLookup
    {
        public OrganizationRole? FirstRole { get; set; } = firstRole;

        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            OrganizationRole? role = organizationId == firstOrganizationId
                ? FirstRole
                : organizationId == secondOrganizationId
                    ? secondRole
                    : null;

            return Task.FromResult(role);
        }
    }
}
