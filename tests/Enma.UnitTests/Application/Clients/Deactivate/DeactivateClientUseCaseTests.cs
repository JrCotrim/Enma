using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.Deactivate;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Clients.Deactivate;

public sealed class DeactivateClientUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "c39712f9-e0ad-48b7-b3ae-59438eb31ef4");

    private static readonly Guid OrganizationId = Guid.Parse(
        "d5592675-fe6f-4284-8bbe-ea14a52a3f85");

    private static readonly Guid ClientId = Guid.Parse(
        "d79952f8-e29f-4e97-861d-c1ec45fbf55b");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_DeactivatesContextualClient(
        OrganizationRole role)
    {
        var persistence = new FakeClientMutationPersistence();
        DeactivateClientUseCase useCase = CreateUseCase(role, persistence);

        DeactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(DeactivateClientResultStatus.Succeeded, result.Status);
        Assert.False(persistence.Client.IsActive);
        Assert.Equal(ClientId, persistence.ClientId);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence();
        DeactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        DeactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(DeactivateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.DeactivateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedOrganizationAccess_DeniesWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence();
        DeactivateClientUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            persistence);

        DeactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(DeactivateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.DeactivateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithAlreadyInactiveClient_ReturnsSucceeded()
    {
        var persistence = new FakeClientMutationPersistence(initiallyActive: false);
        DeactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        DeactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(DeactivateClientResultStatus.Succeeded, result.Status);
        Assert.False(persistence.Client.IsActive);
    }

    [Fact]
    public async Task ExecuteAsync_WithAuthorizedMissingClient_ReturnsNotFound()
    {
        var persistence = new FakeClientMutationPersistence(
            ClientMutationPersistenceResult.NotFound);
        DeactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        DeactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(DeactivateClientResultStatus.NotFound, result.Status);
        Assert.Equal(1, persistence.DeactivateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithCrossTenantShape_ReturnsSameNotFoundContract()
    {
        var persistence = new FakeClientMutationPersistence(
            ClientMutationPersistenceResult.NotFound);
        DeactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        DeactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(DeactivateClientResultStatus.NotFound, result.Status);
        Assert.True(persistence.Client.IsActive);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyClientId_ReturnsNotFoundWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence();
        DeactivateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        DeactivateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty);

        Assert.Equal(DeactivateClientResultStatus.NotFound, result.Status);
        Assert.Equal(0, persistence.DeactivateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsCancellationAndTenantInputs()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var persistence = new FakeClientMutationPersistence();
        DeactivateClientUseCase useCase = CreateUseCase(lookup, persistence);
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

    private static DeactivateClientUseCase CreateUseCase(
        OrganizationRole? role,
        FakeClientMutationPersistence persistence)
    {
        return CreateUseCase(new StubOrganizationAccessLookup(role), persistence);
    }

    private static DeactivateClientUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeClientMutationPersistence persistence)
    {
        return new DeactivateClientUseCase(
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
                DeactivateClientUseCaseTests.OrganizationId,
                "Acme Legal",
                DateTimeOffset.Parse("2026-08-12T16:00:00+00:00"));

            if (!initiallyActive)
            {
                Client.Deactivate();
            }
        }

        public Client Client { get; }

        public int DeactivateCallCount { get; private set; }

        public Guid ClientId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<ClientMutationPersistenceResult> UpdateNameAsync(
            Guid clientId,
            Guid organizationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "UpdateNameAsync must not be called by Deactivate Client tests.");
        }

        public Task<ClientMutationPersistenceResult> DeactivateAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            DeactivateCallCount++;
            ClientId = clientId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            if (_result == ClientMutationPersistenceResult.Succeeded)
            {
                Client.Deactivate();
            }

            return Task.FromResult(_result);
        }

        public Task<ClientMutationPersistenceResult> ReactivateAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ReactivateAsync must not be called by Deactivate Client tests.");
        }
    }
}
