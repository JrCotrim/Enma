using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.Update;
using Enma.Application.Validation;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Clients.Update;

public sealed class UpdateClientUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "9a13fb86-063e-4cec-8f90-b32105265333");

    private static readonly Guid OrganizationId = Guid.Parse(
        "18acd8bb-bd93-44c6-a366-230b72919f7e");

    private static readonly Guid ClientId = Guid.Parse(
        "dfd5f0c7-13a3-481c-b0b1-6c9437bc7bf3");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_UpdatesContextualClient(
        OrganizationRole role)
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(role, persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "  Renamed Legal  ");

        Assert.Equal(UpdateClientResultStatus.Succeeded, result.Status);
        Assert.Equal(ClientId, persistence.ClientId);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal("Renamed Legal", persistence.Client.Name);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Renamed Legal");

        Assert.Equal(UpdateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithDeniedOrganizationAccess_DeniesWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Renamed Legal");

        Assert.Equal(UpdateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithAuthorizedMissingClient_ReturnsNotFound()
    {
        var persistence = new FakeClientMutationPersistence(
            ClientMutationPersistenceResult.NotFound);
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Renamed Legal");

        Assert.Equal(UpdateClientResultStatus.NotFound, result.Status);
        Assert.Equal(1, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithCrossTenantShape_ReturnsSameNotFoundContract()
    {
        var persistence = new FakeClientMutationPersistence(
            ClientMutationPersistenceResult.NotFound);
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Renamed Legal");

        Assert.Equal(UpdateClientResultStatus.NotFound, result.Status);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(ClientId, persistence.ClientId);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyClientId_ReturnsNotFoundWithoutPersistence()
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty,
            "Renamed Legal");

        Assert.Equal(UpdateClientResultStatus.NotFound, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithInvalidName_TranslatesDomainValidation(
        string name)
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                useCase.ExecuteAsync(UserId, OrganizationId, ClientId, name));

        Assert.Contains(ClientErrors.NameRequired, exception.Message);
        Assert.Equal(1, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithNameBeyondMaximum_TranslatesDomainValidation()
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ClientId,
                    new string('a', 151)));

        Assert.Contains(ClientErrors.NameTooLong, exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithOwnerElsewhereAndMemberInContext_DeniesContextualUpdate()
    {
        Guid otherOrganizationId = Guid.Parse(
            "762ca6c0-070f-48cf-ad72-39fab892919d");
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationId,
            OrganizationRole.Member,
            otherOrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(lookup, persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Renamed Legal");

        Assert.Equal(UpdateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsCancellationAndTenantInputs()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(lookup, persistence);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Renamed Legal",
            cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, lookup.CancellationToken);
        Assert.Equal(cancellationTokenSource.Token, persistence.CancellationToken);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(ClientId, persistence.ClientId);
    }

    private static UpdateClientUseCase CreateUseCase(
        OrganizationRole? role,
        FakeClientMutationPersistence persistence)
    {
        return CreateUseCase(
            new ContextualOrganizationAccessLookup(OrganizationId, role),
            persistence);
    }

    private static UpdateClientUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeClientMutationPersistence persistence)
    {
        return new UpdateClientUseCase(
            new ClientActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private sealed class ContextualOrganizationAccessLookup(
        Guid firstOrganizationId,
        OrganizationRole? firstRole,
        Guid? secondOrganizationId = null,
        OrganizationRole? secondRole = null) : IOrganizationAccessLookup
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;

            OrganizationRole? role = organizationId == firstOrganizationId
                ? firstRole
                : organizationId == secondOrganizationId
                    ? secondRole
                    : null;

            return Task.FromResult(role);
        }
    }

    private sealed class FakeClientMutationPersistence(
        ClientMutationPersistenceResult result =
            ClientMutationPersistenceResult.Succeeded) : IClientMutationPersistence
    {
        public Client Client { get; } = new(
            UpdateClientUseCaseTests.OrganizationId,
            "Acme Legal",
            DateTimeOffset.Parse("2026-08-12T16:00:00+00:00"));

        public int UpdateCallCount { get; private set; }

        public Guid ClientId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<ClientMutationPersistenceResult> UpdateNameAsync(
            Guid clientId,
            Guid organizationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            ClientId = clientId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            if (result == ClientMutationPersistenceResult.Succeeded)
            {
                Client.ChangeName(name);
            }

            return Task.FromResult(result);
        }

        public Task<ClientMutationPersistenceResult> DeactivateAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "DeactivateAsync must not be called by Update Client tests.");
        }

        public Task<ClientMutationPersistenceResult> ReactivateAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ReactivateAsync must not be called by Update Client tests.");
        }
    }
}
