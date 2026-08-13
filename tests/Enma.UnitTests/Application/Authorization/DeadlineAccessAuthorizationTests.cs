using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class DeadlineAccessAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "886558f1-4879-4613-8544-fe6045b67895");

    private static readonly Guid OrganizationId = Guid.Parse(
        "78f1a275-b74f-477f-98c0-bfd03ba6e278");

    private static readonly Guid DeadlineId = Guid.Parse(
        "89d68c8e-19be-4879-80f3-4a5b7446f0fd");

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task AuthorizeAsync_WithEmptyIdentifier_DeniesWithoutLookups(
        bool emptyUserId,
        bool emptyOrganizationId,
        bool emptyDeadlineId)
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Owner);
        var ownershipLookup = new StubDeadlineOwnershipLookup(true);
        DeadlineAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);

        DeadlineAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId,
            emptyDeadlineId ? Guid.Empty : DeadlineId);

        Assert.Equal(DeadlineAccessAuthorizationResult.Denied, result);
        Assert.Equal(0, organizationLookup.CallCount);
        Assert.Equal(0, ownershipLookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithDeniedOrganizationAccess_DeniesWithoutOwnershipLookup()
    {
        var ownershipLookup = new StubDeadlineOwnershipLookup(true);
        DeadlineAccessAuthorization authorization = CreateAuthorization(
            new StubOrganizationAccessLookup(null),
            ownershipLookup);

        DeadlineAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Equal(DeadlineAccessAuthorizationResult.Denied, result);
        Assert.Equal(0, ownershipLookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithOrganizationAccessAndSameTenantDeadline_Allows()
    {
        DeadlineAccessAuthorization authorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Member),
            new StubDeadlineOwnershipLookup(true));

        DeadlineAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Equal(DeadlineAccessAuthorizationResult.Allowed, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithMissingAndCrossTenantDeadlines_ReturnsSameDenial()
    {
        DeadlineAccessAuthorization missingAuthorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Owner),
            new StubDeadlineOwnershipLookup(false));
        DeadlineAccessAuthorization crossTenantAuthorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Owner),
            new StubDeadlineOwnershipLookup(false));

        DeadlineAccessAuthorizationResult missingResult =
            await missingAuthorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                Guid.NewGuid());
        DeadlineAccessAuthorizationResult crossTenantResult =
            await crossTenantAuthorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                DeadlineId);

        Assert.Equal(DeadlineAccessAuthorizationResult.Denied, missingResult);
        Assert.Equal(missingResult, crossTenantResult);
    }

    [Fact]
    public async Task AuthorizeAsync_WithContext_ForwardsExactTenantScopeAndCancellation()
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Administrator);
        var ownershipLookup = new StubDeadlineOwnershipLookup(true);
        DeadlineAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);
        using var cancellationTokenSource = new CancellationTokenSource();

        await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            cancellationTokenSource.Token);

        Assert.Equal(UserId, organizationLookup.UserId);
        Assert.Equal(OrganizationId, organizationLookup.OrganizationId);
        Assert.Equal(DeadlineId, ownershipLookup.DeadlineId);
        Assert.Equal(OrganizationId, ownershipLookup.OrganizationId);
        Assert.Equal(cancellationTokenSource.Token, ownershipLookup.CancellationToken);
    }

    [Fact]
    public void ResultContract_PublicValues_ContainsOnlyDeniedAndAllowed()
    {
        Assert.Equal(
            [
                nameof(DeadlineAccessAuthorizationResult.Denied),
                nameof(DeadlineAccessAuthorizationResult.Allowed)
            ],
            Enum.GetNames<DeadlineAccessAuthorizationResult>());
    }

    private static DeadlineAccessAuthorization CreateAuthorization(
        StubOrganizationAccessLookup organizationLookup,
        StubDeadlineOwnershipLookup ownershipLookup)
    {
        return new DeadlineAccessAuthorization(
            new OrganizationAccessAuthorization(organizationLookup),
            ownershipLookup);
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Guid UserId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserId = userId;
            OrganizationId = organizationId;
            return Task.FromResult(role);
        }
    }

    private sealed class StubDeadlineOwnershipLookup(bool exists)
        : IDeadlineOrganizationOwnershipLookup
    {
        public int CallCount { get; private set; }

        public Guid DeadlineId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> ExistsInOrganizationAsync(
            Guid deadlineId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            DeadlineId = deadlineId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(exists);
        }
    }
}
