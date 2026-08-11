using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class ClientAccessAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "739bf701-974a-43db-ac39-e47e846b4157");

    private static readonly Guid OrganizationId = Guid.Parse(
        "6efe4c9d-c0c9-4ea8-8602-bb09ce84853c");

    private static readonly Guid ClientId = Guid.Parse(
        "61139e82-713e-4baa-8425-7623d2a6510a");

    [Fact]
    public async Task AuthorizeAsync_WithDeniedOrganizationAccess_DeniesWithoutOwnershipLookup()
    {
        var organizationLookup = new StubOrganizationAccessLookup(null);
        var ownershipLookup = new StubClientOrganizationOwnershipLookup(true);
        ClientAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);

        ClientAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ClientAccessAuthorizationResult.Denied, result);
        Assert.Equal(1, organizationLookup.CallCount);
        Assert.Equal(0, ownershipLookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithOrganizationAccessAndMissingOwnership_ReturnsDenied()
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Owner);
        var ownershipLookup = new StubClientOrganizationOwnershipLookup(false);
        ClientAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);

        ClientAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ClientAccessAuthorizationResult.Denied, result);
        Assert.Equal(1, ownershipLookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithOrganizationAccessAndMatchingOwnership_ReturnsAllowed()
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Member);
        var ownershipLookup = new StubClientOrganizationOwnershipLookup(true);
        ClientAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);

        ClientAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ClientAccessAuthorizationResult.Allowed, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithContextualIds_PassesSameOrganizationToBothLookups()
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Administrator);
        var ownershipLookup = new StubClientOrganizationOwnershipLookup(true);
        ClientAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);
        using var cancellationTokenSource = new CancellationTokenSource();

        await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ClientId,
            cancellationTokenSource.Token);

        Assert.Equal(UserId, organizationLookup.UserId);
        Assert.Equal(OrganizationId, organizationLookup.OrganizationId);
        Assert.Equal(ClientId, ownershipLookup.ClientId);
        Assert.Equal(OrganizationId, ownershipLookup.OrganizationId);
        Assert.Equal(
            cancellationTokenSource.Token,
            organizationLookup.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            ownershipLookup.CancellationToken);
    }

    [Fact]
    public async Task AuthorizeAsync_WithMissingAndCrossTenantClients_ReturnsSameDeniedShape()
    {
        Guid missingClientId = Guid.Parse(
            "b687d0fe-ec61-4698-b939-251641502454");
        Guid crossTenantClientId = Guid.Parse(
            "ef9ee5e6-d39b-4864-b006-2f47d2d39eb9");
        ClientAccessAuthorization missingAuthorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Owner),
            new StubClientOrganizationOwnershipLookup(false));
        ClientAccessAuthorization crossTenantAuthorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Owner),
            new StubClientOrganizationOwnershipLookup(false));

        ClientAccessAuthorizationResult missingResult =
            await missingAuthorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                missingClientId);
        ClientAccessAuthorizationResult crossTenantResult =
            await crossTenantAuthorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                crossTenantClientId);

        Assert.Equal(ClientAccessAuthorizationResult.Denied, missingResult);
        Assert.Equal(missingResult, crossTenantResult);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task AuthorizeAsync_WithEmptyIdentifier_DeniesWithoutLookups(
        bool emptyUserId,
        bool emptyOrganizationId,
        bool emptyClientId)
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Owner);
        var ownershipLookup = new StubClientOrganizationOwnershipLookup(true);
        ClientAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);

        ClientAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId,
            emptyClientId ? Guid.Empty : ClientId);

        Assert.Equal(ClientAccessAuthorizationResult.Denied, result);
        Assert.Equal(0, organizationLookup.CallCount);
        Assert.Equal(0, ownershipLookup.CallCount);
    }

    [Fact]
    public void ResultContract_PublicValues_ContainsOnlyDeniedAndAllowed()
    {
        Assert.Equal(
            [nameof(ClientAccessAuthorizationResult.Denied),
                nameof(ClientAccessAuthorizationResult.Allowed)],
            Enum.GetNames<ClientAccessAuthorizationResult>());
    }

    private static ClientAccessAuthorization CreateAuthorization(
        StubOrganizationAccessLookup organizationLookup,
        StubClientOrganizationOwnershipLookup ownershipLookup)
    {
        return new ClientAccessAuthorization(
            new OrganizationAccessAuthorization(organizationLookup),
            ownershipLookup);
    }

    private sealed class StubOrganizationAccessLookup(
        OrganizationRole? role) : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Guid? UserId { get; private set; }

        public Guid? OrganizationId { get; private set; }

        public CancellationToken? CancellationToken { get; private set; }

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

    private sealed class StubClientOrganizationOwnershipLookup(
        bool exists) : IClientOrganizationOwnershipLookup
    {
        public int CallCount { get; private set; }

        public Guid? ClientId { get; private set; }

        public Guid? OrganizationId { get; private set; }

        public CancellationToken? CancellationToken { get; private set; }

        public Task<bool> ExistsInOrganizationAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ClientId = clientId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            return Task.FromResult(exists);
        }
    }
}
