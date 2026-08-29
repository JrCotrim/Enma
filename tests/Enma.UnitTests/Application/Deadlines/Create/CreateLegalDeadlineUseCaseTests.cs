using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Create;
using Enma.Application.Validation;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Deadlines.Create;

public sealed class CreateLegalDeadlineUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "32a074f2-167c-456f-b9d7-e36343284cdb");

    private static readonly Guid OrganizationId = Guid.Parse(
        "684d23b6-ebec-4f4b-9389-a9c33609fc6f");

    private static readonly Guid MembershipId = Guid.Parse(
        "74f0cd5c-53b8-4afb-8278-810718d03fd4");

    private static readonly Guid ProcessId = Guid.Parse(
        "1f1dc5ad-229e-41d5-bebf-6e19b6bbeea2");

    private static readonly DateOnly DueDate = new(2026, 9, 15);

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
    public async Task ExecuteAsync_WithAuthorizedRoleAndSameTenantProcess_CreatesFromContext(
        OrganizationRole role)
    {
        var processLookup = new FakeProcessOwnershipLookup(true);
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            role,
            processLookup,
            persistence);

        CreateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "  File Appellate Brief  ",
            DueDate);

        Assert.Equal(CreateLegalDeadlineResultStatus.Created, result.Status);
        Assert.Equal(persistence.PersistedDeadline?.Id, result.DeadlineId);
        Assert.Equal(OrganizationId, persistence.PersistedDeadline?.OrganizationId);
        Assert.Equal(ProcessId, persistence.PersistedDeadline?.ProcessId);
        Assert.Equal("File Appellate Brief", persistence.PersistedDeadline?.Title);
        Assert.Equal(DueDate, persistence.PersistedDeadline?.DueDate);
        Assert.Equal(UtcNow, persistence.PersistedDeadline?.CreatedAt);
        Assert.Null(persistence.PersistedDeadline?.CompletedAt);
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(null)]
    public async Task ExecuteAsync_WithoutCreateAuthority_ShortCircuitsLookupAndPersistence(
        OrganizationRole? role)
    {
        var processLookup = new FakeProcessOwnershipLookup(true);
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            role,
            processLookup,
            persistence);

        CreateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "File Appellate Brief",
            DueDate);

        Assert.Same(CreateLegalDeadlineResult.AccessDenied, result);
        Assert.Equal(0, processLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyProcess_ReturnsUnavailableWithoutLookupOrPersistence()
    {
        var processLookup = new FakeProcessOwnershipLookup(true);
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            processLookup,
            persistence);

        CreateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty,
            "File Appellate Brief",
            DueDate);

        Assert.Same(CreateLegalDeadlineResult.RelatedProcessUnavailable, result);
        Assert.Equal(0, processLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingOrCrossTenantProcess_ReturnsSameUnavailableResult()
    {
        var missingPersistence = new FakeDeadlineCreationPersistence();
        var crossTenantPersistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase missingUseCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeProcessOwnershipLookup(false),
            missingPersistence);
        CreateLegalDeadlineUseCase crossTenantUseCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeProcessOwnershipLookup(false),
            crossTenantPersistence);

        CreateLegalDeadlineResult missing = await missingUseCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.NewGuid(),
            "Missing Process Deadline",
            DueDate);
        CreateLegalDeadlineResult crossTenant = await crossTenantUseCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Cross-tenant Process Deadline",
            DueDate);

        Assert.Same(CreateLegalDeadlineResult.RelatedProcessUnavailable, missing);
        Assert.Same(missing, crossTenant);
        Assert.Equal(0, missingPersistence.CallCount);
        Assert.Equal(0, crossTenantPersistence.CallCount);
    }

    [Theory]
    [MemberData(nameof(ValidDueDates))]
    public async Task ExecuteAsync_WithAnyValidCalendarDate_PreservesExactDateOnly(
        DateOnly dueDate)
    {
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            new FakeProcessOwnershipLookup(true),
            persistence);

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Calendar Deadline",
            dueDate);

        Assert.Equal(dueDate, persistence.PersistedDeadline?.DueDate);
    }

    public static TheoryData<DateOnly> ValidDueDates => new()
    {
        new DateOnly(1999, 1, 2),
        new DateOnly(2026, 8, 13),
        new DateOnly(2099, 12, 31)
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithBlankTitle_TranslatesKnownDomainValidation(
        string title)
    {
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeProcessOwnershipLookup(true),
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ProcessId,
                    title,
                    DueDate));

        Assert.Contains(LegalDeadlineErrors.TitleRequired, exception.Message);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithTitleBeyondMaximum_TranslatesKnownDomainValidation()
    {
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeProcessOwnershipLookup(true),
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ProcessId,
                    new string('a', 151),
                    DueDate));

        Assert.Contains(LegalDeadlineErrors.TitleTooLong, exception.Message);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidDueDate_TranslatesKnownDomainValidation()
    {
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeProcessOwnershipLookup(true),
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ProcessId,
                    "Calendar Deadline",
                    DateOnly.MinValue));

        Assert.Contains(LegalDeadlineErrors.DueDateInvalid, exception.Message);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnexpectedServerTimeInvariant_DoesNotMapToRequestValidation()
    {
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeProcessOwnershipLookup(true),
            new FakeDeadlineCreationPersistence(),
            DateTimeOffset.MinValue);

        ArgumentOutOfRangeException exception =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    ProcessId,
                    "Calendar Deadline",
                    DueDate));

        Assert.Equal("createdAt", exception.ParamName);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsExactProcessScopeAndCancellation()
    {
        var processLookup = new FakeProcessOwnershipLookup(true);
        var persistence = new FakeDeadlineCreationPersistence();
        CreateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            processLookup,
            persistence);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            "Calendar Deadline",
            DueDate,
            cancellationTokenSource.Token);

        Assert.Equal(ProcessId, processLookup.ProcessId);
        Assert.Equal(OrganizationId, processLookup.OrganizationId);
        Assert.Equal(cancellationTokenSource.Token, processLookup.CancellationToken);
        Assert.Equal(cancellationTokenSource.Token, persistence.CancellationToken);
    }

    private static CreateLegalDeadlineUseCase CreateUseCase(
        OrganizationRole? role,
        FakeProcessOwnershipLookup processLookup,
        FakeDeadlineCreationPersistence persistence,
        DateTimeOffset? utcNow = null)
    {
        var actionAuthorization = new DeadlineActionAuthorization(
            new OrganizationAccessAuthorization(
                new StubOrganizationAccessLookup(role)));

        return new CreateLegalDeadlineUseCase(
            actionAuthorization,
            processLookup,
            persistence,
            new FixedTimeProvider(utcNow ?? UtcNow));
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
            OrganizationAccessLookupResult? result = role.HasValue
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    MembershipId,
                    role.Value)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeProcessOwnershipLookup(bool exists)
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

    private sealed class FakeDeadlineCreationPersistence
        : ILegalDeadlineCreationPersistence
    {
        public int CallCount { get; private set; }

        public LegalDeadline? PersistedDeadline { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalDeadlineCreationPersistenceResult> ExecuteAsync(
            LegalDeadlineCreationPersistenceRequest request,
            Func<LegalDeadlineCreationLockedState, LegalDeadlineCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            LegalDeadlineCreationDecision decision = decide(
                new LegalDeadlineCreationLockedState(
                    true,
                    new LegalDeadlineLockedActorState(
                        MembershipId,
                        request.OrganizationId,
                        request.UserId,
                        OrganizationRole.Owner,
                        true,
                        true),
                    true));

            if (decision.Status != LegalDeadlineCreationDecisionStatus.Persist)
            {
                return Task.FromResult(
                    LegalDeadlineCreationPersistenceResult.Rejected(decision.Status));
            }

            PersistedDeadline = decision.LegalDeadline;
            return Task.FromResult(
                LegalDeadlineCreationPersistenceResult.Created(
                    decision.LegalDeadline!.Id));
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
