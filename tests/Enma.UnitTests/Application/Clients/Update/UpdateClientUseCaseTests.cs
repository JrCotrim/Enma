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

    private static readonly Guid MembershipId = Guid.Parse(
        "03ef1710-3fdb-469d-855d-33bc9575e1cf");

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

    [Fact]
    public async Task ExecuteAsync_WithFullProfile_ReplacesNewProfileFields()
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        UpdateClientResult result = await ExecuteProfileAsync(
            useCase,
            personType: "company",
            cnpj: "11.222.333/0001-81",
            address: " Rua Sintetica, 200 ",
            notes: " Synthetic notes ");

        Assert.Equal(UpdateClientResultStatus.Succeeded, result.Status);
        Assert.Equal(PersonType.Company, persistence.Client.PersonType);
        Assert.Equal("11222333000181", persistence.Client.Cnpj);
        Assert.Equal("Rua Sintetica, 200", persistence.Client.Address);
        Assert.Equal("Synthetic notes", persistence.Client.Notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Company")]
    [InlineData("pf")]
    public async Task ExecuteAsync_WithMissingOrUnknownPersonType_RejectsWithoutMutation(
        string? personType)
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => ExecuteProfileAsync(
                    useCase,
                    personType: personType,
                    address: "Changed address"));

        Assert.Contains("person type", exception.Message);
        Assert.Equal("Acme Legal", persistence.Client.Name);
        Assert.Null(persistence.Client.Address);
        Assert.Equal(PersonType.Individual, persistence.Client.PersonType);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownPersonTypeAndMemberRole_DeniesBeforeValidation()
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        UpdateClientResult result = await ExecuteProfileAsync(
            useCase,
            personType: "unknown");

        Assert.Equal(UpdateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Theory]
    [MemberData(nameof(InvalidNewProfileFields))]
    public async Task ExecuteAsync_WithInvalidNewProfileField_TranslatesToValidationWithoutEchoingValue(
        string personType,
        string? cpf,
        string? cnpj,
        string? address,
        string? notes,
        string expectedError,
        string expectedParameter,
        string rejectedValue)
    {
        var persistence = new FakeClientMutationPersistence();
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => ExecuteProfileAsync(
                    useCase,
                    personType,
                    cpf,
                    cnpj,
                    address,
                    notes));

        Assert.Contains(expectedError, exception.Message);
        Assert.Contains($"'{expectedParameter}'", exception.Message);
        Assert.DoesNotContain(rejectedValue, exception.Message);
        Assert.Equal("Acme Legal", persistence.Client.Name);
        Assert.Null(persistence.Client.Cnpj);
        Assert.Null(persistence.Client.Address);
        Assert.Null(persistence.Client.Notes);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceReportsDuplicateDocument_ReturnsDuplicateDocument()
    {
        var persistence = new FakeClientMutationPersistence(
            ClientMutationPersistenceResult.DuplicateDocument);
        UpdateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        UpdateClientResult result = await ExecuteProfileAsync(
            useCase,
            cpf: "529.982.247-25");

        Assert.Equal(UpdateClientResultStatus.DuplicateDocument, result.Status);
        Assert.Equal(1, persistence.UpdateCallCount);
    }

    public static TheoryData<string, string?, string?, string?, string?, string, string, string>
        InvalidNewProfileFields()
    {
        string longAddress = new('a', 301);
        string longNotes = new('n', 2_001);

        return new()
        {
            { "company", null, "12.345.678/0001-00", null, null, ClientErrors.CnpjInvalid, "cnpj", "12.345.678/0001-00" },
            { "individual", null, "12.ABC.345/01DE-35", null, null, ClientErrors.CnpjNotAllowedForIndividual, "cnpj", "12.ABC.345/01DE-35" },
            { "company", "529.982.247-25", null, null, null, ClientErrors.CpfNotAllowedForCompany, "cpf", "529.982.247-25" },
            { "individual", null, null, longAddress, null, ClientErrors.AddressTooLong, "address", longAddress },
            { "individual", null, null, null, longNotes, ClientErrors.NotesTooLong, "notes", longNotes }
        };
    }

    private static Task<UpdateClientResult> ExecuteProfileAsync(
        UpdateClientUseCase useCase,
        string? personType = "individual",
        string? cpf = null,
        string? cnpj = null,
        string? address = null,
        string? notes = null)
    {
        return useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Profile Client",
            null,
            null,
            cpf,
            personType,
            cnpj,
            address,
            notes);
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

        public async Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            OrganizationRole? role = await FindActiveRoleAsync(
                userId,
                organizationId,
                cancellationToken);

            return role is OrganizationRole value
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    MembershipId,
                    value)
                : null;
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
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            ClientId = request.ClientId;
            OrganizationId = request.OrganizationId;
            CancellationToken = cancellationToken;

            if (result == ClientMutationPersistenceResult.Succeeded)
            {
                ClientMutationDecision decision = decide(CreateState(request));
                return Task.FromResult(
                    decision.Status == ClientMutationDecisionStatus.Persist
                        ? result
                        : ClientMutationPersistenceResult.AccessDenied);
            }

            return Task.FromResult(result);
        }

        public Task<ClientMutationPersistenceResult> DeactivateAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "DeactivateAsync must not be called by Update Client tests.");
        }

        public Task<ClientMutationPersistenceResult> ReactivateAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ReactivateAsync must not be called by Update Client tests.");
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
