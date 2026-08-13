using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class ProcessAccessAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "fd2f70dc-0060-4354-9ab8-ef212f1b9c47");

    private static readonly Guid OrganizationId = Guid.Parse(
        "bf67512a-5d18-4df8-86c7-8fdbba5ec0c7");

    private static readonly Guid ProcessId = Guid.Parse(
        "e4cd42f5-d96f-49f5-8f34-79c7f830895d");

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
        bool emptyProcessId)
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Owner);
        var ownershipLookup = new StubProcessOrganizationOwnershipLookup(true);
        ProcessAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);

        ProcessAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId,
            emptyProcessId ? Guid.Empty : ProcessId);

        Assert.Equal(ProcessAccessAuthorizationResult.Denied, result);
        Assert.Equal(0, organizationLookup.CallCount);
        Assert.Equal(0, ownershipLookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithDeniedOrganizationAccess_DeniesWithoutOwnershipLookup()
    {
        var organizationLookup = new StubOrganizationAccessLookup(null);
        var ownershipLookup = new StubProcessOrganizationOwnershipLookup(true);
        ProcessAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);

        ProcessAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ProcessId);

        Assert.Equal(ProcessAccessAuthorizationResult.Denied, result);
        Assert.Equal(1, organizationLookup.CallCount);
        Assert.Equal(0, ownershipLookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithOrganizationAccessAndMatchingOwnership_ReturnsAllowed()
    {
        ProcessAccessAuthorization authorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Member),
            new StubProcessOrganizationOwnershipLookup(true));

        ProcessAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ProcessId);

        Assert.Equal(ProcessAccessAuthorizationResult.Allowed, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithOrganizationAccessAndMissingOwnership_ReturnsDenied()
    {
        ProcessAccessAuthorization authorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Owner),
            new StubProcessOrganizationOwnershipLookup(false));

        ProcessAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ProcessId);

        Assert.Equal(ProcessAccessAuthorizationResult.Denied, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithMissingAndCrossTenantProcesses_ReturnsSameDeniedShape()
    {
        ProcessAccessAuthorization missingAuthorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Owner),
            new StubProcessOrganizationOwnershipLookup(false));
        ProcessAccessAuthorization crossTenantAuthorization = CreateAuthorization(
            new StubOrganizationAccessLookup(OrganizationRole.Owner),
            new StubProcessOrganizationOwnershipLookup(false));

        ProcessAccessAuthorizationResult missingResult =
            await missingAuthorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                Guid.NewGuid());
        ProcessAccessAuthorizationResult crossTenantResult =
            await crossTenantAuthorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                ProcessId);

        Assert.Equal(ProcessAccessAuthorizationResult.Denied, missingResult);
        Assert.Equal(missingResult, crossTenantResult);
    }

    [Fact]
    public async Task AuthorizeAsync_WithContext_PassesExactTenantInputsToLookups()
    {
        var organizationLookup = new StubOrganizationAccessLookup(
            OrganizationRole.Administrator);
        var ownershipLookup = new StubProcessOrganizationOwnershipLookup(true);
        ProcessAccessAuthorization authorization = CreateAuthorization(
            organizationLookup,
            ownershipLookup);
        using var cancellationTokenSource = new CancellationTokenSource();

        await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            ProcessId,
            cancellationTokenSource.Token);

        Assert.Equal(UserId, organizationLookup.UserId);
        Assert.Equal(OrganizationId, organizationLookup.OrganizationId);
        Assert.Equal(ProcessId, ownershipLookup.ProcessId);
        Assert.Equal(OrganizationId, ownershipLookup.OrganizationId);
        Assert.Equal(
            cancellationTokenSource.Token,
            organizationLookup.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            ownershipLookup.CancellationToken);
    }

    [Fact]
    public void ResultContract_PublicValues_ContainsOnlyDeniedAndAllowed()
    {
        Assert.Equal(
            [nameof(ProcessAccessAuthorizationResult.Denied),
                nameof(ProcessAccessAuthorizationResult.Allowed)],
            Enum.GetNames<ProcessAccessAuthorizationResult>());
    }

    private static ProcessAccessAuthorization CreateAuthorization(
        StubOrganizationAccessLookup organizationLookup,
        StubProcessOrganizationOwnershipLookup ownershipLookup)
    {
        return new ProcessAccessAuthorization(
            new OrganizationAccessAuthorization(organizationLookup),
            ownershipLookup);
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

    private sealed class StubProcessOrganizationOwnershipLookup(bool exists)
        : IProcessOrganizationOwnershipLookup
    {
        public int CallCount { get; private set; }

        public Guid ProcessId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> ExistsInOrganizationAsync(
            Guid processId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ProcessId = processId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            return Task.FromResult(exists);
        }
    }
}
