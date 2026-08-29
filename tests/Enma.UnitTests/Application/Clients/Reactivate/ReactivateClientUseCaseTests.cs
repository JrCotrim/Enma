using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.Reactivate;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Clients.Reactivate;

public sealed class ReactivateClientUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "728b3f7a-9ef3-4053-9d5a-ea8155f49715");

    private static readonly Guid OrganizationId = Guid.Parse(
        "fe7411af-14d0-4c31-84d1-f3d87fe55e03");

    private static readonly Guid ClientId = Guid.Parse(
        "856b9bd3-a2c2-4d17-adb7-4a083b687703");

    private static readonly Guid MembershipId = Guid.Parse(
        "9839be27-9df0-4977-b42f-347623c06a49");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_ReactivatesContextualClient(
        OrganizationRole role)
    {
        var persistence = new FakeClientMutationPersistence(initiallyActive: false);
        ReactivateClientUseCase useCase = CreateUseCase(role, persistence);

        ReactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ReactivateClientResultStatus.Succeeded, result.Status);
        Assert.True(persistence.Client.IsActive);
        Assert.Equal(ClientId, persistence.ClientId);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence(initiallyActive: false);
        ReactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        ReactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ReactivateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.ReactivateCallCount);
        Assert.False(persistence.Client.IsActive);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedOrganizationAccess_DeniesWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence(initiallyActive: false);
        ReactivateClientUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            persistence);

        ReactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ReactivateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.ReactivateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithAlreadyActiveClient_ReturnsSucceeded()
    {
        var persistence = new FakeClientMutationPersistence();
        ReactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ReactivateClientResultStatus.Succeeded, result.Status);
        Assert.True(persistence.Client.IsActive);
    }

    [Fact]
    public async Task ExecuteAsync_WithAuthorizedMissingClient_ReturnsNotFound()
    {
        var persistence = new FakeClientMutationPersistence(
            ClientMutationPersistenceResult.NotFound,
            initiallyActive: false);
        ReactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ReactivateClientResultStatus.NotFound, result.Status);
        Assert.Equal(1, persistence.ReactivateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithCrossTenantShape_ReturnsSameNotFoundContract()
    {
        var persistence = new FakeClientMutationPersistence(
            ClientMutationPersistenceResult.NotFound,
            initiallyActive: false);
        ReactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        ReactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(ReactivateClientResultStatus.NotFound, result.Status);
        Assert.False(persistence.Client.IsActive);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyClientId_ReturnsNotFoundWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence(initiallyActive: false);
        ReactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty);

        Assert.Equal(ReactivateClientResultStatus.NotFound, result.Status);
        Assert.Equal(0, persistence.ReactivateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsCancellationAndTenantInputs()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var persistence = new FakeClientMutationPersistence(initiallyActive: false);
        ReactivateClientUseCase useCase = CreateUseCase(lookup, persistence);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, lookup.CancellationToken);
        Assert.Equal(cancellationTokenSource.Token, persistence.CancellationToken);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(ClientId, persistence.ClientId);
    }

    private static ReactivateClientUseCase CreateUseCase(
        OrganizationRole? role,
        FakeClientMutationPersistence persistence)
    {
        return CreateUseCase(new StubOrganizationAccessLookup(role), persistence);
    }

    private static ReactivateClientUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeClientMutationPersistence persistence)
    {
        return new ReactivateClientUseCase(
            new ClientActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            return Task.FromResult(role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            OrganizationAccessLookupResult? access = role is OrganizationRole value
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    MembershipId,
                    value)
                : null;
            return Task.FromResult(access);
        }
    }

    private sealed class FakeClientMutationPersistence : IClientMutationPersistence
    {
        private readonly ClientMutationPersistenceResult _result;

        public FakeClientMutationPersistence(
            ClientMutationPersistenceResult result =
                ClientMutationPersistenceResult.Succeeded,
            bool initiallyActive = true)
        {
            _result = result;
            Client = new Client(
                ReactivateClientUseCaseTests.OrganizationId,
                "Acme Legal",
                DateTimeOffset.Parse("2026-08-12T16:00:00+00:00"));

            if (!initiallyActive)
            {
                Client.Deactivate();
            }
        }

        public Client Client { get; }

        public int ReactivateCallCount { get; private set; }

        public Guid ClientId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<ClientMutationPersistenceResult> UpdateNameAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "UpdateNameAsync must not be called by Reactivate Client tests.");
        }

        public Task<ClientMutationPersistenceResult> DeactivateAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "DeactivateAsync must not be called by Reactivate Client tests.");
        }

        public Task<ClientMutationPersistenceResult> ReactivateAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            ReactivateCallCount++;
            ClientId = request.ClientId;
            OrganizationId = request.OrganizationId;
            CancellationToken = cancellationToken;

            if (_result == ClientMutationPersistenceResult.Succeeded)
            {
                ClientMutationDecision decision = decide(CreateState(request));
                return Task.FromResult(
                    decision.Status == ClientMutationDecisionStatus.Persist
                        ? _result
                        : ClientMutationPersistenceResult.AccessDenied);
            }

            return Task.FromResult(_result);
        }

        private ClientMutationLockedState CreateState(
            ClientMutationPersistenceRequest request)
        {
            return new ClientMutationLockedState(
                Client,
                IsOrganizationActive: true,
                new ClientLockedActorState(
                    request.ActorMembershipId,
                    request.OrganizationId,
                    request.UserId,
                    OrganizationRole.Owner,
                    IsMembershipActive: true,
                    IsUserActive: true));
        }
    }
}
