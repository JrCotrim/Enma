using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class DeadlineActionAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "5a044f55-b348-4381-ab03-d9b09c5e4a64");

    private static readonly Guid OrganizationAId = Guid.Parse(
        "7a28c4b2-a439-4d8f-889b-0c1f45e7ae1e");

    private static readonly Guid OrganizationBId = Guid.Parse(
        "c3ae759f-b2f4-4f09-a742-f504288d11bf");

    [Theory]
    [InlineData(DeadlineAction.View, OrganizationRole.Owner, true)]
    [InlineData(DeadlineAction.View, OrganizationRole.Administrator, true)]
    [InlineData(DeadlineAction.View, OrganizationRole.Member, true)]
    [InlineData(DeadlineAction.Create, OrganizationRole.Owner, true)]
    [InlineData(DeadlineAction.Create, OrganizationRole.Administrator, true)]
    [InlineData(DeadlineAction.Create, OrganizationRole.Member, false)]
    public async Task AuthorizeAsync_WithLiveRole_AppliesExplicitActionRule(
        DeadlineAction action,
        OrganizationRole role,
        bool expectedAllowed)
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            role);
        DeadlineActionAuthorization authorization = CreateAuthorization(lookup);

        DeadlineActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            action);

        Assert.Equal(
            expectedAllowed
                ? DeadlineActionAuthorizationResult.Allowed
                : DeadlineActionAuthorizationResult.Denied,
            result);
    }

    [Fact]
    public async Task AuthorizeAsync_AfterLiveRoleChanges_UsesCurrentRoleEachTime()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Member);
        DeadlineActionAuthorization authorization = CreateAuthorization(lookup);

        DeadlineActionAuthorizationResult member = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            DeadlineAction.Create);
        lookup.FirstRole = OrganizationRole.Administrator;
        DeadlineActionAuthorizationResult administrator =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationAId,
                DeadlineAction.Create);
        lookup.FirstRole = OrganizationRole.Member;
        DeadlineActionAuthorizationResult demoted = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            DeadlineAction.Create);

        Assert.Equal(DeadlineActionAuthorizationResult.Denied, member);
        Assert.Equal(DeadlineActionAuthorizationResult.Allowed, administrator);
        Assert.Equal(DeadlineActionAuthorizationResult.Denied, demoted);
        Assert.Equal(3, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithDualMembership_UsesOnlyContextualRole()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Member,
            OrganizationBId,
            OrganizationRole.Owner);
        DeadlineActionAuthorization authorization = CreateAuthorization(lookup);

        DeadlineActionAuthorizationResult viewA = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            DeadlineAction.View);
        DeadlineActionAuthorizationResult createA = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            DeadlineAction.Create);
        DeadlineActionAuthorizationResult viewB = await authorization.AuthorizeAsync(
            UserId,
            OrganizationBId,
            DeadlineAction.View);
        DeadlineActionAuthorizationResult createB = await authorization.AuthorizeAsync(
            UserId,
            OrganizationBId,
            DeadlineAction.Create);

        Assert.Equal(DeadlineActionAuthorizationResult.Allowed, viewA);
        Assert.Equal(DeadlineActionAuthorizationResult.Denied, createA);
        Assert.Equal(DeadlineActionAuthorizationResult.Allowed, viewB);
        Assert.Equal(DeadlineActionAuthorizationResult.Allowed, createB);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorizeAsync_WithEmptyContext_DeniesWithoutLookup(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Owner);
        DeadlineActionAuthorization authorization = CreateAuthorization(lookup);

        DeadlineActionAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationAId,
            DeadlineAction.View);

        Assert.Equal(DeadlineActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedAction_DeniesWithoutLookup()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationAId,
            OrganizationRole.Owner);
        DeadlineActionAuthorization authorization = CreateAuthorization(lookup);

        DeadlineActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            (DeadlineAction)int.MaxValue);

        Assert.Equal(DeadlineActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedLiveRole_Denies()
    {
        DeadlineActionAuthorization authorization = CreateAuthorization(
            new ContextualOrganizationAccessLookup(
                OrganizationAId,
                (OrganizationRole)int.MaxValue));

        DeadlineActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationAId,
            DeadlineAction.View);

        Assert.Equal(DeadlineActionAuthorizationResult.Denied, result);
    }

    [Fact]
    public void ActionContract_PublicValues_ContainsOnlyViewAndCreate()
    {
        Assert.Equal(
            [nameof(DeadlineAction.View), nameof(DeadlineAction.Create)],
            Enum.GetNames<DeadlineAction>());
    }

    private static DeadlineActionAuthorization CreateAuthorization(
        ContextualOrganizationAccessLookup lookup)
    {
        return new DeadlineActionAuthorization(
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
