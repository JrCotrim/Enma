using Enma.Application.Authorization;
using Enma.Application.Processes;
using Enma.Application.Processes.Create;
using Enma.Application.Validation;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;

namespace Enma.UnitTests.Application.Processes.Create;

public sealed class CreateLegalProcessUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "daff4d88-215e-42a5-bcb1-461af199f70c");

    private static readonly Guid OrganizationId = Guid.Parse(
        "f3b6d60c-fd32-4af5-b424-5522430c5725");

    private static readonly Guid ClientId = Guid.Parse(
        "a4a86ba3-cb04-4f01-b655-b2128116fc30");

    private static readonly DateTimeOffset UtcNow = new(
        2026,
        8,
        13,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRoleAndActiveClient_CreatesInContextualOrganization(
        OrganizationRole role)
    {
        var activeClientLookup = new FakeActiveClientLookup(true);
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            role,
            activeClientLookup,
            persistence);

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "  Contract Review  ");

        Assert.Equal(CreateLegalProcessResultStatus.Succeeded, result.Status);
        Assert.Equal(persistence.PersistedProcess?.Id, result.ProcessId);
        Assert.Equal(OrganizationId, persistence.PersistedProcess?.OrganizationId);
        Assert.Equal(ClientId, persistence.PersistedProcess?.ClientId);
        Assert.Equal("Contract Review", persistence.PersistedProcess?.Title);
        Assert.Equal(UtcNow, persistence.PersistedProcess?.CreatedAt);
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(null)]
    public async Task ExecuteAsync_WithoutCreateAuthority_DeniesBeforeRelatedClientOrPersistence(
        OrganizationRole? role)
    {
        var activeClientLookup = new FakeActiveClientLookup(true);
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            role,
            activeClientLookup,
            persistence);

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Contract Review");

        Assert.Equal(CreateLegalProcessResultStatus.AccessDenied, result.Status);
        Assert.Null(result.ProcessId);
        Assert.Equal(0, activeClientLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("cross-tenant")]
    public async Task ExecuteAsync_WithUnavailableRelatedClient_ReturnsSameGenericResult(
        string unavailableCondition)
    {
        var activeClientLookup = new FakeActiveClientLookup(false);
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            activeClientLookup,
            persistence);

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            $"Process for {unavailableCondition} client");

        Assert.Same(CreateLegalProcessResult.RelatedClientUnavailable, result);
        Assert.Null(result.ProcessId);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyClientId_ReturnsUnavailableWithoutLookupOrPersistence()
    {
        var activeClientLookup = new FakeActiveClientLookup(true);
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            activeClientLookup,
            persistence);

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty,
            "Contract Review");

        Assert.Same(CreateLegalProcessResult.RelatedClientUnavailable, result);
        Assert.Equal(0, activeClientLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithInvalidTitle_TranslatesKnownDomainValidation(
        string title)
    {
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeActiveClientLookup(true),
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ClientId,
                    title));

        Assert.Contains(LegalProcessErrors.TitleRequired, exception.Message);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithTitleBeyondMaximum_TranslatesKnownDomainValidation()
    {
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            new FakeActiveClientLookup(true),
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ClientId,
                    new string('a', 151)));

        Assert.Contains(LegalProcessErrors.TitleTooLong, exception.Message);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsExactRelatedClientScopeAndCancellation()
    {
        var activeClientLookup = new FakeActiveClientLookup(true);
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            activeClientLookup,
            persistence);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            "Contract Review",
            cancellationTokenSource.Token);

        Assert.Equal(ClientId, activeClientLookup.ClientId);
        Assert.Equal(OrganizationId, activeClientLookup.OrganizationId);
        Assert.Equal(
            cancellationTokenSource.Token,
            activeClientLookup.CancellationToken);
        Assert.Equal(
            cancellationTokenSource.Token,
            persistence.CancellationToken);
    }

    private static CreateLegalProcessUseCase CreateUseCase(
        OrganizationRole? role,
        FakeActiveClientLookup activeClientLookup,
        FakeLegalProcessCreationPersistence persistence)
    {
        var actionAuthorization = new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(
                new StubOrganizationAccessLookup(role)));

        return new CreateLegalProcessUseCase(
            actionAuthorization,
            activeClientLookup,
            persistence,
            new FixedTimeProvider(UtcNow));
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(role);
        }
    }

    private sealed class FakeActiveClientLookup(bool exists)
        : IActiveClientInOrganizationLookup
    {
        public int CallCount { get; private set; }

        public Guid ClientId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> ExistsAsync(
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

    private sealed class FakeLegalProcessCreationPersistence
        : ILegalProcessCreationPersistence
    {
        public int CallCount { get; private set; }

        public LegalProcess? PersistedProcess { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task PersistAsync(
            LegalProcess legalProcess,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            PersistedProcess = legalProcess;
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
