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

    private static readonly Guid MembershipId = Guid.Parse(
        "4599c275-0601-46a2-98a8-095d99defc74");

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
        Assert.Equal(1, persistence.CallCount);
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
        Assert.Equal(1, persistence.CallCount);
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

    [Fact]
    public async Task ExecuteAsync_WithoutPersonType_CreatesIndividualClient()
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        CreateClientResult result = await ExecuteProfileAsync(
            useCase,
            personType: null,
            cpf: "529.982.247-25");

        Assert.Equal(CreateClientResultStatus.Succeeded, result.Status);
        Assert.Equal(PersonType.Individual, persistence.PersistedClient?.PersonType);
        Assert.Equal("52998224725", persistence.PersistedClient?.Cpf);
        Assert.Null(persistence.PersistedClient?.Cnpj);
    }

    [Fact]
    public async Task ExecuteAsync_WithCompanyProfile_PersistsNewProfileFields()
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        CreateClientResult result = await ExecuteProfileAsync(
            useCase,
            personType: "company",
            cnpj: "12.ABC.345/01DE-35",
            address: "  Rua Sintetica, 100  ",
            notes: "  Synthetic notes  ");

        Assert.Equal(CreateClientResultStatus.Succeeded, result.Status);
        Client? client = persistence.PersistedClient;
        Assert.NotNull(client);
        Assert.Equal(PersonType.Company, client.PersonType);
        Assert.Equal("12ABC34501DE35", client.Cnpj);
        Assert.Null(client.Cpf);
        Assert.Equal("Rua Sintetica, 100", client.Address);
        Assert.Equal("Synthetic notes", client.Notes);
    }

    [Theory]
    [InlineData("Company")]
    [InlineData("INDIVIDUAL")]
    [InlineData("pj")]
    [InlineData("")]
    [InlineData("1")]
    public async Task ExecuteAsync_WithUnknownPersonType_RejectsBeforePersistence(
        string personType)
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => ExecuteProfileAsync(useCase, personType: personType));

        Assert.Contains("person type", exception.Message);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownPersonTypeAndMemberRole_DeniesBeforeValidation()
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        CreateClientResult result = await ExecuteProfileAsync(
            useCase,
            personType: "unknown");

        Assert.Equal(CreateClientResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [MemberData(nameof(InvalidNewProfileFields))]
    public async Task ExecuteAsync_WithInvalidNewProfileField_TranslatesToValidationWithoutEchoingValue(
        string? personType,
        string? cpf,
        string? cnpj,
        string? address,
        string? notes,
        string expectedError,
        string expectedParameter,
        string rejectedValue)
    {
        var persistence = new FakeClientCreationPersistence();
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
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
        Assert.Null(persistence.PersistedClient);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceReportsDuplicateDocument_ReturnsDuplicateDocument()
    {
        var persistence = new FakeClientCreationPersistence(
            ClientCreationPersistenceResult.DuplicateDocument);
        CreateClientUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        CreateClientResult result = await ExecuteProfileAsync(
            useCase,
            cpf: "529.982.247-25");

        Assert.Equal(CreateClientResultStatus.DuplicateDocument, result.Status);
        Assert.Null(result.ClientId);
        Assert.Equal(1, persistence.CallCount);
    }

    public static TheoryData<string?, string?, string?, string?, string?, string, string, string>
        InvalidNewProfileFields()
    {
        string longAddress = new('a', 301);
        string longNotes = new('n', 2_001);

        return new()
        {
            { "company", null, "12.345.678/0001-00", null, null, ClientErrors.CnpjInvalid, "cnpj", "12.345.678/0001-00" },
            { "individual", null, "11.222.333/0001-81", null, null, ClientErrors.CnpjNotAllowedForIndividual, "cnpj", "11.222.333/0001-81" },
            { null, null, "11.222.333/0001-81", null, null, ClientErrors.CnpjNotAllowedForIndividual, "cnpj", "11.222.333/0001-81" },
            { "company", "529.982.247-25", null, null, null, ClientErrors.CpfNotAllowedForCompany, "cpf", "529.982.247-25" },
            { "individual", null, null, longAddress, null, ClientErrors.AddressTooLong, "address", longAddress },
            { "individual", null, null, null, longNotes, ClientErrors.NotesTooLong, "notes", longNotes }
        };
    }

    private static Task<CreateClientResult> ExecuteProfileAsync(
        CreateClientUseCase useCase,
        string? personType = null,
        string? cpf = null,
        string? cnpj = null,
        string? address = null,
        string? notes = null)
    {
        return useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "Profile Client",
            null,
            null,
            cpf,
            personType,
            cnpj,
            address,
            notes);
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

    private sealed class FakeClientCreationPersistence(
        ClientCreationPersistenceResult? acceptedDecisionResult = null)
        : IClientCreationPersistence
    {
        public int CallCount { get; private set; }

        public Client? PersistedClient { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<ClientCreationPersistenceResult> ExecuteAsync(
            ClientCreationPersistenceRequest request,
            Func<ClientCreationLockedState, ClientCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            ClientCreationDecision decision = decide(
                new ClientCreationLockedState(
                    IsOrganizationActive: true,
                    new ClientLockedActorState(
                        request.ActorMembershipId,
                        request.OrganizationId,
                        request.UserId,
                        OrganizationRole.Owner,
                        IsMembershipActive: true,
                        IsUserActive: true)));

            if (decision.Status != ClientCreationDecisionStatus.Persist ||
                decision.Client is not { } client)
            {
                return Task.FromResult(
                    ClientCreationPersistenceResult.AccessDenied);
            }

            if (acceptedDecisionResult is { } overriddenResult)
            {
                return Task.FromResult(overriddenResult);
            }

            PersistedClient = client;
            return Task.FromResult(
                ClientCreationPersistenceResult.Created(client.Id));
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
