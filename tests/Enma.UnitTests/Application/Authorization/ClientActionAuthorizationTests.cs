using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class ClientActionAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "6f270777-cbaa-4a16-a165-96c907220b19");

    private static readonly Guid OrganizationId = Guid.Parse(
        "6c464926-a57c-4829-b6a9-685239e5f83f");

    [Theory]
    [InlineData(ClientAction.View, OrganizationRole.Owner, true)]
    [InlineData(ClientAction.View, OrganizationRole.Administrator, true)]
    [InlineData(ClientAction.View, OrganizationRole.Member, true)]
    [InlineData(ClientAction.Create, OrganizationRole.Owner, true)]
    [InlineData(ClientAction.Create, OrganizationRole.Administrator, true)]
    [InlineData(ClientAction.Create, OrganizationRole.Member, false)]
    [InlineData(ClientAction.Update, OrganizationRole.Owner, true)]
    [InlineData(ClientAction.Update, OrganizationRole.Administrator, true)]
    [InlineData(ClientAction.Update, OrganizationRole.Member, false)]
    [InlineData(ClientAction.Deactivate, OrganizationRole.Owner, true)]
    [InlineData(ClientAction.Deactivate, OrganizationRole.Administrator, true)]
    [InlineData(ClientAction.Deactivate, OrganizationRole.Member, false)]
    [InlineData(ClientAction.Reactivate, OrganizationRole.Owner, true)]
    [InlineData(ClientAction.Reactivate, OrganizationRole.Administrator, true)]
    [InlineData(ClientAction.Reactivate, OrganizationRole.Member, false)]
    public async Task AuthorizeAsync_WithLiveRole_AppliesExplicitActionRule(
        ClientAction action,
        OrganizationRole role,
        bool expectedAllowed)
    {
        var lookup = new StubOrganizationAccessLookup(role);
        ClientActionAuthorization authorization = CreateAuthorization(lookup);

        ClientActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            action);

        Assert.Equal(
            expectedAllowed
                ? ClientActionAuthorizationResult.Allowed
                : ClientActionAuthorizationResult.Denied,
            result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithDeniedOrganizationAccess_ReturnsDenied()
    {
        var lookup = new StubOrganizationAccessLookup(null);
        ClientActionAuthorization authorization = CreateAuthorization(lookup);

        ClientActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ClientAction.View);

        Assert.Equal(ClientActionAuthorizationResult.Denied, result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorizeAsync_WithEmptyContext_DeniesWithoutOrganizationLookup(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        ClientActionAuthorization authorization = CreateAuthorization(lookup);

        ClientActionAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId,
            ClientAction.Create);

        Assert.Equal(ClientActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedAction_DeniesWithoutOrganizationLookup()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        ClientActionAuthorization authorization = CreateAuthorization(lookup);

        ClientActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            (ClientAction)int.MaxValue);

        Assert.Equal(ClientActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithContext_ForwardsOnlyContextToLiveAuthority()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Member);
        ClientActionAuthorization authorization = CreateAuthorization(lookup);
        using var cancellationTokenSource = new CancellationTokenSource();

        await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ClientAction.View,
            cancellationTokenSource.Token);

        Assert.Equal(UserId, lookup.UserId);
        Assert.Equal(OrganizationId, lookup.OrganizationId);
        Assert.Equal(cancellationTokenSource.Token, lookup.CancellationToken);
    }

    [Fact]
    public void ResultContract_PublicValues_ContainNoRoleOrDenialReason()
    {
        Assert.Equal(
            [nameof(ClientActionAuthorizationResult.Denied),
                nameof(ClientActionAuthorizationResult.Allowed)],
            Enum.GetNames<ClientActionAuthorizationResult>());
        Assert.DoesNotContain(
            typeof(ClientActionAuthorizationResult).GetMembers(),
            member => member.Name.Contains("Role", StringComparison.Ordinal));
    }

    private static ClientActionAuthorization CreateAuthorization(
        StubOrganizationAccessLookup lookup)
    {
        return new ClientActionAuthorization(
            new OrganizationAccessAuthorization(lookup));
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Guid UserId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserId = userId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            return Task.FromResult(role);
        }
    }
}
