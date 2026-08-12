using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.Create;
using Enma.Application.Validation;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Clients.Create;

public sealed class CreateClientUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "04598110-7239-436d-a93f-b8443c03ce65");

    private static readonly Guid OrganizationId = Guid.Parse(
        "50504893-43f3-4c41-acb6-baa208e8b7dc");

    private static readonly DateTimeOffset UtcNow = new(
        2026,
        8,
        12,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_CreatesClientInContextualOrganization(
        OrganizationRole role)
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(role, persistence);

        CreateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "  Acme Legal  ");

        Assert.Equal(CreateClientResultStatus.Succeeded, result.Status);
        Assert.Equal(persistence.PersistedClient?.Id, result.ClientId);
        Assert.Equal(OrganizationId, persistence.PersistedClient?.OrganizationId);
        Assert.Equal("Acme Legal", persistence.PersistedClient?.Name);
        Assert.True(persistence.PersistedClient?.IsActive);
        Assert.Equal(UtcNow, persistence.PersistedClient?.CreatedAt);
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(null)]
    public async Task ExecuteAsync_WithoutCreateAuthority_DeniesWithoutPersistence(
        OrganizationRole? role)
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(role, persistence);

        CreateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "Acme Legal");

        Assert.Equal(CreateClientResultStatus.AccessDenied, result.Status);
        Assert.Null(result.ClientId);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithOwnerElsewhereAndMemberInContext_DeniesContextualCreate()
    {
        Guid otherOrganizationId = Guid.Parse(
            "43c926a8-90f2-4ed4-98b7-bd8d10bc0ad0");
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationId,
            OrganizationRole.Member,
            otherOrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(lookup, persistence);

        CreateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "Acme Legal");

        Assert.Equal(CreateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithInvalidName_TranslatesKnownDomainValidation(
        string name)
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(UserId, OrganizationId, name));

        Assert.Contains(ClientErrors.NameRequired, exception.Message);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithNameBeyondMaximum_TranslatesKnownDomainValidation()
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    new string('a', 151)));

        Assert.Contains(ClientErrors.NameTooLong, exception.Message);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellationToken_ForwardsTokenToAuthorityAndPersistence()
    {
        var lookup = new ContextualOrganizationAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(lookup, persistence);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "Acme Legal",
            cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, lookup.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            persistence.CancellationToken);
    }

    private static CreateClientUseCase CreateUseCase(
        OrganizationRole? role,
        FakeClientCreationPersistence persistence)
    {
        return CreateUseCase(
            new ContextualOrganizationAccessLookup(OrganizationId, role),
            persistence);
    }

    private static CreateClientUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeClientCreationPersistence persistence)
    {
        var organizationAuthorization = new OrganizationAccessAuthorization(lookup);
        var actionAuthorization = new ClientActionAuthorization(
            organizationAuthorization);

        return new CreateClientUseCase(
            actionAuthorization,
            persistence,
            new FixedTimeProvider(UtcNow));
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

    private sealed class FakeClientCreationPersistence : IClientCreationPersistence
    {
        public int CallCount { get; private set; }

        public Client? PersistedClient { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task PersistAsync(
            Client client,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            PersistedClient = client;
            CancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
