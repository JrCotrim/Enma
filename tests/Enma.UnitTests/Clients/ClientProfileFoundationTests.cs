using Enma.Application.Auditing;
using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.Create;
using Enma.Application.Clients.Update;
using Enma.Application.Validation;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Clients;

public sealed class ClientProfileFoundationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "64791461-1308-4ba7-bc9a-d3ab42635e47");

    private static readonly Guid OrganizationId = Guid.Parse(
        "240fbb70-0b14-46f5-8d14-ddaa914ca695");

    private static readonly Guid MembershipId = Guid.Parse(
        "45c74d72-f007-42dd-a657-a420c82743ec");

    private static readonly Guid ClientId = Guid.Parse(
        "e440965b-c83a-4a15-ac48-5338ea3350a6");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        9,
        5,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Constructor_WithProfile_NormalizesOptionalFields()
    {
        var client = new Client(
            OrganizationId,
            "  Maria Silva  ",
            CreatedAt,
            "  MARIA.SILVA@EXAMPLE.COM  ",
            " +55 (22) 98888-7777 ",
            "123.456.789-09");

        Assert.Equal("Maria Silva", client.Name);
        Assert.Equal("maria.silva@example.com", client.Email);
        Assert.Equal("5522988887777", client.Phone);
        Assert.Equal("12345678909", client.Cpf);
        Assert.True(client.IsActive);
        Assert.Equal(CreatedAt, client.CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithMissingOptionalFields_StoresNull(
        string? value)
    {
        var client = new Client(
            OrganizationId,
            "Maria Silva",
            CreatedAt,
            value,
            value,
            value);

        Assert.Null(client.Email);
        Assert.Null(client.Phone);
        Assert.Null(client.Cpf);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("person@")]
    public void Constructor_WithInvalidEmail_Rejects(string email)
    {
        ArgumentException exception =
            Assert.ThrowsAny<ArgumentException>(
                () => new Client(
                    OrganizationId,
                    "Maria Silva",
                    CreatedAt,
                    email));

        Assert.Equal("email", exception.ParamName);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("abcdefgh")]
    [InlineData("+55 (22) ABCD-9999")]
    [InlineData("1234567890123456")]
    public void Constructor_WithInvalidPhone_Rejects(string phone)
    {
        ArgumentException exception =
            Assert.ThrowsAny<ArgumentException>(
                () => new Client(
                    OrganizationId,
                    "Maria Silva",
                    CreatedAt,
                    phone: phone));

        Assert.Equal("phone", exception.ParamName);
    }

    [Theory]
    [InlineData("111.111.111-11")]
    [InlineData("123.456.789-00")]
    [InlineData("1234567890")]
    [InlineData("123456789012")]
    public void Constructor_WithInvalidCpf_Rejects(string cpf)
    {
        ArgumentException exception =
            Assert.ThrowsAny<ArgumentException>(
                () => new Client(
                    OrganizationId,
                    "Maria Silva",
                    CreatedAt,
                    cpf: cpf));

        Assert.Equal("cpf", exception.ParamName);
    }

    [Fact]
    public void UpdateProfile_WithInvalidFinalField_IsAtomic()
    {
        var client = new Client(
            OrganizationId,
            "Original Name",
            CreatedAt,
            "original@example.com",
            "(22) 98888-7777",
            "123.456.789-09");

        ArgumentException exception =
            Assert.ThrowsAny<ArgumentException>(
                () => client.UpdateProfile(
                    "Changed Name",
                    "changed@example.com",
                    "(22) 97777-6666",
                    "111.111.111-11"));

        Assert.Equal("cpf", exception.ParamName);

        Assert.Equal("Original Name", client.Name);
        Assert.Equal("original@example.com", client.Email);
        Assert.Equal("22988887777", client.Phone);
        Assert.Equal("12345678909", client.Cpf);
    }

    [Fact]
    public void UpdateProfile_WithBlankOptionals_ClearsProfileFields()
    {
        var client = new Client(
            OrganizationId,
            "Original Name",
            CreatedAt,
            "original@example.com",
            "(22) 98888-7777",
            "123.456.789-09");

        client.UpdateProfile(
            "Updated Name",
            " ",
            null,
            "");

        Assert.Equal("Updated Name", client.Name);
        Assert.Null(client.Email);
        Assert.Null(client.Phone);
        Assert.Null(client.Cpf);
    }

    [Fact]
    public async Task CreateUseCase_WithProfile_PersistsNormalizedProfile()
    {
        var persistence = new FakeCreationPersistence();
        CreateClientUseCase useCase =
            CreateCreateUseCase(persistence);

        CreateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "  Maria Silva  ",
            " MARIA@EXAMPLE.COM ",
            "+55 (22) 98888-7777",
            "123.456.789-09");

        Assert.Equal(
            CreateClientResultStatus.Succeeded,
            result.Status);

        Client persisted = Assert.IsType<Client>(
            persistence.PersistedClient);

        Assert.Equal(persisted.Id, result.ClientId);
        Assert.Equal("Maria Silva", persisted.Name);
        Assert.Equal("maria@example.com", persisted.Email);
        Assert.Equal("5522988887777", persisted.Phone);
        Assert.Equal("12345678909", persisted.Cpf);
    }

    [Fact]
    public async Task CreateUseCase_OldOverload_RemainsCompatible()
    {
        var persistence = new FakeCreationPersistence();
        CreateClientUseCase useCase =
            CreateCreateUseCase(persistence);

        CreateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "Legacy Client");

        Assert.Equal(
            CreateClientResultStatus.Succeeded,
            result.Status);

        Client persisted = Assert.IsType<Client>(
            persistence.PersistedClient);

        Assert.Equal("Legacy Client", persisted.Name);
        Assert.Null(persisted.Email);
        Assert.Null(persisted.Phone);
        Assert.Null(persisted.Cpf);
    }

    [Fact]
    public async Task CreateUseCase_WithInvalidProfile_TranslatesValidation()
    {
        var persistence = new FakeCreationPersistence();
        CreateClientUseCase useCase =
            CreateCreateUseCase(persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    "Maria Silva",
                    "invalid-email",
                    null,
                    null));

        Assert.Contains(
            ClientErrors.EmailInvalid,
            exception.Message);

        Assert.Equal(1, persistence.CallCount);
        Assert.Null(persistence.PersistedClient);
    }

    [Fact]
    public async Task UpdateUseCase_WithProfile_UpdatesNormalizedProfile()
    {
        var persistence = new FakeMutationPersistence();
        UpdateClientUseCase useCase =
            CreateUpdateUseCase(persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "  Updated Client  ",
            " UPDATED@EXAMPLE.COM ",
            "(22) 98888-7777",
            "123.456.789-09");

        Assert.Equal(
            UpdateClientResultStatus.Succeeded,
            result.Status);

        Assert.Equal("Updated Client", persistence.Client.Name);
        Assert.Equal(
            "updated@example.com",
            persistence.Client.Email);
        Assert.Equal(
            "22988887777",
            persistence.Client.Phone);
        Assert.Equal(
            "12345678909",
            persistence.Client.Cpf);
    }

    [Fact]
    public async Task UpdateUseCase_OldOverload_PreservesProfile()
    {
        var persistence = new FakeMutationPersistence(
            email: "existing@example.com",
            phone: "22988887777",
            cpf: "12345678909");

        UpdateClientUseCase useCase =
            CreateUpdateUseCase(persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Renamed Client");

        Assert.Equal(
            UpdateClientResultStatus.Succeeded,
            result.Status);

        Assert.Equal("Renamed Client", persistence.Client.Name);
        Assert.Equal(
            "existing@example.com",
            persistence.Client.Email);
        Assert.Equal(
            "22988887777",
            persistence.Client.Phone);
        Assert.Equal(
            "12345678909",
            persistence.Client.Cpf);
    }

    [Fact]
    public async Task UpdateUseCase_InvalidProfile_IsTranslatedAndAtomic()
    {
        var persistence = new FakeMutationPersistence(
            email: "existing@example.com",
            phone: "22988887777",
            cpf: "12345678909");

        UpdateClientUseCase useCase =
            CreateUpdateUseCase(persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ClientId,
                    "Changed Client",
                    "changed@example.com",
                    "(22) 97777-6666",
                    "111.111.111-11"));

        Assert.Contains(
            ClientErrors.CpfInvalid,
            exception.Message);

        Assert.Equal(
            "Original Client",
            persistence.Client.Name);

        Assert.Equal(
            "existing@example.com",
            persistence.Client.Email);

        Assert.Equal(
            "22988887777",
            persistence.Client.Phone);

        Assert.Equal(
            "12345678909",
            persistence.Client.Cpf);
    }

    [Fact]
    public void AuditEvent_ProfileUpdated_HasExpectedContract()
    {
        Assert.Equal(
            29,
            (int)AuditEventType.ClientProfileUpdated);

        Assert.Equal(
            "client.profile_updated",
            AuditEventType.ClientProfileUpdated.ToCode());

        Assert.Equal(
            AuditEntityType.Client,
            AuditEventType.ClientProfileUpdated.GetEntityType());

        AuditEventType.ClientProfileUpdated.ValidateDetails(null);

        var intent = new AuditIntent(
            AuditEventType.ClientProfileUpdated,
            ClientId);

        Assert.Equal(
            AuditEventType.ClientProfileUpdated,
            intent.EventType);

        Assert.Equal(
            AuditEntityType.Client,
            intent.EntityType);

        Assert.Equal(ClientId, intent.EntityId);
        Assert.Null(intent.Details);
    }

    private static CreateClientUseCase CreateCreateUseCase(
        FakeCreationPersistence persistence)
    {
        return new CreateClientUseCase(
            new ClientActionAuthorization(
                new OrganizationAccessAuthorization(
                    new OwnerAccessLookup())),
            persistence,
            new FixedTimeProvider(CreatedAt));
    }

    private static UpdateClientUseCase CreateUpdateUseCase(
        FakeMutationPersistence persistence)
    {
        return new UpdateClientUseCase(
            new ClientActionAuthorization(
                new OrganizationAccessAuthorization(
                    new OwnerAccessLookup())),
            persistence);
    }

    private sealed class OwnerAccessLookup :
        IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrganizationRole?>(
                organizationId == OrganizationId
                    ? OrganizationRole.Owner
                    : null);
        }

        public Task<OrganizationAccessLookupResult?>
            FindActiveAccessAsync(
                Guid userId,
                Guid organizationId,
                CancellationToken cancellationToken = default)
        {
            OrganizationAccessLookupResult? result =
                organizationId == OrganizationId
                    ? new OrganizationAccessLookupResult(
                        userId,
                        organizationId,
                        MembershipId,
                        OrganizationRole.Owner)
                    : null;

            return Task.FromResult(result);
        }
    }

    private sealed class FakeCreationPersistence :
        IClientCreationPersistence
    {
        public int CallCount { get; private set; }

        public Client? PersistedClient { get; private set; }

        public Task<ClientCreationPersistenceResult> ExecuteAsync(
            ClientCreationPersistenceRequest request,
            Func<
                ClientCreationLockedState,
                ClientCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            ClientCreationDecision decision = decide(
                new ClientCreationLockedState(
                    true,
                    new ClientLockedActorState(
                        request.ActorMembershipId,
                        request.OrganizationId,
                        request.UserId,
                        OrganizationRole.Owner,
                        true,
                        true)));

            if (
                decision.Status !=
                ClientCreationDecisionStatus.Persist)
            {
                return Task.FromResult(
                    ClientCreationPersistenceResult.AccessDenied);
            }

            PersistedClient =
                decision.Client
                ?? throw new InvalidOperationException(
                    "Expected persisted client.");

            return Task.FromResult(
                ClientCreationPersistenceResult.Created(
                    PersistedClient.Id));
        }
    }

    private sealed class FakeMutationPersistence :
        IClientMutationPersistence
    {
        public FakeMutationPersistence(
            string? email = null,
            string? phone = null,
            string? cpf = null)
        {
            Client = new Client(
                OrganizationId,
                "Original Client",
                CreatedAt,
                email,
                phone,
                cpf);
        }

        public Client Client { get; }

        public Task<ClientMutationPersistenceResult>
            UpdateNameAsync(
                ClientMutationPersistenceRequest request,
                Func<
                    ClientMutationLockedState,
                    ClientMutationDecision> decide,
                CancellationToken cancellationToken = default)
        {
            ClientMutationDecision decision =
                decide(CreateState(request));

            return Task.FromResult(
                decision.Status ==
                    ClientMutationDecisionStatus.Persist
                    ? ClientMutationPersistenceResult.Succeeded
                    : ClientMutationPersistenceResult.AccessDenied);
        }

        public Task<ClientMutationPersistenceResult>
            DeactivateAsync(
                ClientMutationPersistenceRequest request,
                Func<
                    ClientMutationLockedState,
                    ClientMutationDecision> decide,
                CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "DeactivateAsync must not be called.");
        }

        public Task<ClientMutationPersistenceResult>
            ReactivateAsync(
                ClientMutationPersistenceRequest request,
                Func<
                    ClientMutationLockedState,
                    ClientMutationDecision> decide,
                CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ReactivateAsync must not be called.");
        }

        private ClientMutationLockedState CreateState(
            ClientMutationPersistenceRequest request)
        {
            return new ClientMutationLockedState(
                Client,
                true,
                new ClientLockedActorState(
                    request.ActorMembershipId,
                    request.OrganizationId,
                    request.UserId,
                    OrganizationRole.Owner,
                    true,
                    true));
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}