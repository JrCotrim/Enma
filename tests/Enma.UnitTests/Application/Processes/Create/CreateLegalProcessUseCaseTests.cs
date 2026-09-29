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

    private static readonly Guid MembershipId = Guid.Parse(
        "b4ffd8f4-eaf4-46dc-a047-29683958e996");

    private static readonly Guid ResponsibleMembershipId = Guid.Parse(
        "1c6e0d8f-7a7b-4f9e-8b1e-3b0f3a5a9d11");

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
            CreateCommand("  Contract Review  "));

        Assert.Equal(CreateLegalProcessResultStatus.Succeeded, result.Status);
        Assert.Equal(persistence.PersistedProcess?.Id, result.ProcessId);
        Assert.Equal(OrganizationId, persistence.PersistedProcess?.OrganizationId);
        Assert.Equal(ClientId, persistence.PersistedProcess?.ClientId);
        Assert.Equal("Contract Review", persistence.PersistedProcess?.Title);
        Assert.Equal(UtcNow, persistence.PersistedProcess?.CreatedAt);
        Assert.Null(persistence.PersistedProcess?.ProcessNumber);
        Assert.Equal(
            LegalProcessStatus.InProgress,
            persistence.PersistedProcess?.Status);
        Assert.Null(persistence.PersistedProcess?.CourtOrAuthority);
        Assert.Null(persistence.PersistedProcess?.ResponsibleMembershipId);
        Assert.Null(persistence.Request?.ResponsibleMembershipId);
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
            CreateCommand("Contract Review") with
            {
                Status = "archived",
                ResponsibleMembershipId = Guid.Empty
            });

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
            CreateCommand($"Process for {unavailableCondition} client"));

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
            CreateCommand("Contract Review") with { ClientId = Guid.Empty });

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
                () => useCase.ExecuteAsync(CreateCommand(title)));

        Assert.Contains(LegalProcessErrors.TitleRequired, exception.Message);
        Assert.Equal(1, persistence.CallCount);
        Assert.Null(persistence.PersistedProcess);
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
                () => useCase.ExecuteAsync(CreateCommand(new string('a', 151))));

        Assert.Contains(LegalProcessErrors.TitleTooLong, exception.Message);
        Assert.Equal(1, persistence.CallCount);
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
            CreateCommand("Contract Review"),
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

    [Theory]
    [InlineData(null, LegalProcessStatus.InProgress)]
    [InlineData("inProgress", LegalProcessStatus.InProgress)]
    [InlineData("suspended", LegalProcessStatus.Suspended)]
    [InlineData("closed", LegalProcessStatus.Closed)]
    public async Task ExecuteAsync_WithOperationalFields_CreatesWithInitialStatusAndResponsible(
        string? status,
        LegalProcessStatus expectedStatus)
    {
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeActiveClientLookup(true),
            persistence);

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            CreateCommand("Contract Review") with
            {
                ProcessNumber = "  0001234-56.2026.8.19.0001  ",
                Status = status,
                CourtOrAuthority = "  1ª Vara Cível  ",
                ResponsibleMembershipId = ResponsibleMembershipId
            });

        Assert.Equal(CreateLegalProcessResultStatus.Succeeded, result.Status);
        LegalProcess legalProcess = Assert.IsType<LegalProcess>(
            persistence.PersistedProcess);
        Assert.Equal("0001234-56.2026.8.19.0001", legalProcess.ProcessNumber);
        Assert.Equal("00012345620268190001", legalProcess.NormalizedProcessNumber);
        Assert.Equal(expectedStatus, legalProcess.Status);
        Assert.Equal("1ª Vara Cível", legalProcess.CourtOrAuthority);
        Assert.Equal(ResponsibleMembershipId, legalProcess.ResponsibleMembershipId);
        Assert.Equal(
            ResponsibleMembershipId,
            persistence.Request?.ResponsibleMembershipId);
    }

    [Theory]
    [InlineData("InProgress")]
    [InlineData("archived")]
    [InlineData("")]
    public async Task ExecuteAsync_WithInvalidStatus_RejectsBeforeRelatedClientOrPersistence(
        string status)
    {
        var activeClientLookup = new FakeActiveClientLookup(true);
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            activeClientLookup,
            persistence);

        await Assert.ThrowsAsync<RequestValidationException>(
            () => useCase.ExecuteAsync(
                CreateCommand("Contract Review") with { Status = status }));

        Assert.Equal(0, activeClientLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyResponsible_RejectsBeforeRelatedClientOrPersistence()
    {
        var activeClientLookup = new FakeActiveClientLookup(true);
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            activeClientLookup,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    CreateCommand("Contract Review") with
                    {
                        ResponsibleMembershipId = Guid.Empty
                    }));

        Assert.Contains(
            LegalProcessErrors.ResponsibleMembershipIdInvalid,
            exception.Message);
        Assert.Equal(0, activeClientLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [MemberData(nameof(UnavailableResponsibleStates))]
    public async Task ExecuteAsync_WithUnavailableLockedResponsible_RejectsInAnyInitialStatus(
        string status,
        LegalProcessLockedActorState? responsible)
    {
        var persistence = new FakeLegalProcessCreationPersistence
        {
            Responsible = responsible
        };
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeActiveClientLookup(true),
            persistence);

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            CreateCommand("Contract Review") with
            {
                Status = status,
                ResponsibleMembershipId = ResponsibleMembershipId
            });

        Assert.Same(CreateLegalProcessResult.RelatedResponsibleUnavailable, result);
        Assert.Null(result.ProcessId);
        Assert.Null(persistence.PersistedProcess);
    }

    [Theory]
    [InlineData("processNumber", 101, 0)]
    [InlineData("courtOrAuthority", 0, 201)]
    public async Task ExecuteAsync_WithOperationalFieldBeyondMaximum_TranslatesDomainValidation(
        string field,
        int processNumberLength,
        int courtOrAuthorityLength)
    {
        var persistence = new FakeLegalProcessCreationPersistence();
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeActiveClientLookup(true),
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    CreateCommand("Contract Review") with
                    {
                        ProcessNumber = processNumberLength == 0
                            ? null
                            : new string('1', processNumberLength),
                        CourtOrAuthority = courtOrAuthorityLength == 0
                            ? null
                            : new string('C', courtOrAuthorityLength)
                    }));

        Assert.Equal(
            field,
            Assert.IsAssignableFrom<ArgumentException>(
                exception.InnerException).ParamName);
        Assert.Null(persistence.PersistedProcess);
    }

    [Fact]
    public async Task ExecuteAsync_WithDuplicateNumberFromPersistence_ReturnsDuplicate()
    {
        var persistence = new FakeLegalProcessCreationPersistence
        {
            DuplicateOnPersist = true
        };
        CreateLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeActiveClientLookup(true),
            persistence);

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            CreateCommand("Contract Review") with { ProcessNumber = "ABC-1" });

        Assert.Same(CreateLegalProcessResult.DuplicateProcessNumber, result);
        Assert.Null(result.ProcessId);
    }

    public static TheoryData<string, LegalProcessLockedActorState?>
        UnavailableResponsibleStates()
    {
        var data = new TheoryData<string, LegalProcessLockedActorState?>();

        foreach (string status in new[] { "inProgress", "suspended", "closed" })
        {
            data.Add(status, null);
            data.Add(status, CreateResponsibleState(isMembershipActive: false));
            data.Add(status, CreateResponsibleState(isUserActive: false));
            data.Add(
                status,
                CreateResponsibleState(organizationId: Guid.NewGuid()));
        }

        return data;
    }

    private static LegalProcessLockedActorState CreateResponsibleState(
        Guid? organizationId = null,
        bool isMembershipActive = true,
        bool isUserActive = true)
    {
        return new LegalProcessLockedActorState(
            ResponsibleMembershipId,
            organizationId ?? OrganizationId,
            Guid.Parse("a2d6c2de-0a43-4c1a-94ef-4a7d6ac6b7f2"),
            OrganizationRole.Member,
            isMembershipActive,
            isUserActive);
    }

    private static CreateLegalProcessCommand CreateCommand(string title)
    {
        return new CreateLegalProcessCommand(
            UserId,
            OrganizationId,
            ClientId,
            title);
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

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
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

        public LegalProcessCreationPersistenceRequest? Request { get; private set; }

        public LegalProcess? PersistedProcess { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public LegalProcessLockedActorState? Responsible { get; init; } =
            CreateResponsibleState();

        public bool DuplicateOnPersist { get; init; }

        public Task<LegalProcessCreationPersistenceResult> ExecuteAsync(
            LegalProcessCreationPersistenceRequest request,
            Func<LegalProcessCreationLockedState, LegalProcessCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            LegalProcessCreationDecision decision = decide(
                new LegalProcessCreationLockedState(
                    IsOrganizationActive: true,
                    new LegalProcessLockedActorState(
                        request.ActorMembershipId,
                        request.OrganizationId,
                        request.UserId,
                        OrganizationRole.Owner,
                        IsMembershipActive: true,
                        IsUserActive: true),
                    IsClientAvailable: true,
                    request.ResponsibleMembershipId is null ? null : Responsible));

            if (decision.Status != LegalProcessCreationDecisionStatus.Persist ||
                decision.LegalProcess is not { } legalProcess)
            {
                return Task.FromResult(
                    LegalProcessCreationPersistenceResult.Rejected(
                        decision.Status));
            }

            if (DuplicateOnPersist)
            {
                return Task.FromResult(
                    LegalProcessCreationPersistenceResult.Rejected(
                        LegalProcessCreationDecisionStatus.DuplicateProcessNumber));
            }

            PersistedProcess = legalProcess;
            return Task.FromResult(
                LegalProcessCreationPersistenceResult.Created(legalProcess.Id));
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
