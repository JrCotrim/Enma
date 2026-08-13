using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Update;
using Enma.Application.Validation;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Deadlines.Update;

public sealed class UpdateLegalDeadlineUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "7f3a1af4-b785-4dda-bf23-9df8c55e94e1");
    private static readonly Guid OrganizationId = Guid.Parse(
        "f4aaf7c2-da2f-4407-933f-82cb23e141d1");
    private static readonly Guid ProcessId = Guid.Parse(
        "59f65563-6851-4ab5-9a1c-59214d865b6f");
    private static readonly Guid DeadlineId = Guid.Parse(
        "99b944f7-dfa3-4020-9170-001ce10c58ac");
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 8, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly OriginalDueDate = new(2026, 9, 1);
    private static readonly DateOnly UpdatedDueDate = new(2027, 2, 28);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_UpdatesOnlyTitleAndDueDate(
        OrganizationRole role)
    {
        var persistence = new FakeMutationPersistence();
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(role, persistence);

        UpdateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            "  Updated title  ",
            UpdatedDueDate);

        Assert.Equal(UpdateLegalDeadlineResultStatus.Updated, result.Status);
        Assert.Equal("Updated title", persistence.Deadline.Title);
        Assert.Equal(UpdatedDueDate, persistence.Deadline.DueDate);
        Assert.Equal(OrganizationId, persistence.Deadline.OrganizationId);
        Assert.Equal(ProcessId, persistence.Deadline.ProcessId);
        Assert.Equal(CreatedAt, persistence.Deadline.CreatedAt);
        Assert.Null(persistence.Deadline.CompletedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesBeforePersistence()
    {
        var persistence = new FakeMutationPersistence();
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        UpdateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            "   ",
            DateOnly.MinValue);

        Assert.Same(UpdateLegalDeadlineResult.AccessDenied, result);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyDeadlineId_ReturnsNotFoundWithoutPersistence()
    {
        var persistence = new FakeMutationPersistence();
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        UpdateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty,
            "Updated title",
            UpdatedDueDate);

        Assert.Same(UpdateLegalDeadlineResult.NotFound, result);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    [Theory]
    [InlineData(LegalDeadlineDetailsMutationPersistenceResult.NotFound)]
    [InlineData(LegalDeadlineDetailsMutationPersistenceResult.Conflict)]
    public async Task ExecuteAsync_WithNonsuccessPersistenceResult_ReturnsNarrowOutcome(
        LegalDeadlineDetailsMutationPersistenceResult persistenceResult)
    {
        var persistence = new FakeMutationPersistence(persistenceResult);
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        UpdateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            "Updated title",
            UpdatedDueDate);

        Assert.Equal(
            persistenceResult == LegalDeadlineDetailsMutationPersistenceResult.Conflict
                ? UpdateLegalDeadlineResultStatus.Conflict
                : UpdateLegalDeadlineResultStatus.NotFound,
            result.Status);
        Assert.Equal("Initial title", persistence.Deadline.Title);
        Assert.Equal(OriginalDueDate, persistence.Deadline.DueDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_WithInvalidTitle_TranslatesCallerValidation(
        string title)
    {
        var persistence = new FakeMutationPersistence();
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    DeadlineId,
                    title,
                    UpdatedDueDate));

        Assert.Contains(LegalDeadlineErrors.TitleRequired, exception.Message);
        Assert.Equal("Initial title", persistence.Deadline.Title);
        Assert.Equal(OriginalDueDate, persistence.Deadline.DueDate);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidDueDate_TranslatesCallerValidationAtomically()
    {
        var persistence = new FakeMutationPersistence();
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    DeadlineId,
                    "New title",
                    DateOnly.MinValue));

        Assert.Contains(LegalDeadlineErrors.DueDateInvalid, exception.Message);
        Assert.Equal("Initial title", persistence.Deadline.Title);
        Assert.Equal(OriginalDueDate, persistence.Deadline.DueDate);
    }

    [Fact]
    public async Task ExecuteAsync_WithTitleBeyondMaximum_TranslatesCallerValidation()
    {
        var persistence = new FakeMutationPersistence();
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(() =>
                useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    DeadlineId,
                    new string('a', 151),
                    UpdatedDueDate));

        Assert.Contains(LegalDeadlineErrors.TitleTooLong, exception.Message);
        Assert.Equal("Initial title", persistence.Deadline.Title);
        Assert.Equal(OriginalDueDate, persistence.Deadline.DueDate);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsTenantAndCancellation()
    {
        var persistence = new FakeMutationPersistence();
        var lookup = new ContextualAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(lookup, persistence);
        using var cancellation = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            "Updated title",
            UpdatedDueDate,
            cancellation.Token);

        Assert.Equal(cancellation.Token, lookup.CancellationToken);
        Assert.Equal(cancellation.Token, persistence.CancellationToken);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(DeadlineId, persistence.DeadlineId);
    }

    [Fact]
    public async Task ExecuteAsync_WithOwnerElsewhereAndMemberInContext_DeniesContextualMutation()
    {
        Guid otherOrganizationId = Guid.Parse(
            "c11175b9-e7bb-4eeb-9820-2e0a94d87d45");
        var lookup = new ContextualAccessLookup(
            OrganizationId,
            OrganizationRole.Member,
            otherOrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeMutationPersistence();
        UpdateLegalDeadlineUseCase useCase = CreateUseCase(lookup, persistence);

        UpdateLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            "Updated title",
            UpdatedDueDate);

        Assert.Same(UpdateLegalDeadlineResult.AccessDenied, result);
        Assert.Equal(0, persistence.UpdateCallCount);
    }

    private static UpdateLegalDeadlineUseCase CreateUseCase(
        OrganizationRole? role,
        FakeMutationPersistence persistence)
    {
        return CreateUseCase(
            new ContextualAccessLookup(OrganizationId, role),
            persistence);
    }

    private static UpdateLegalDeadlineUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeMutationPersistence persistence)
    {
        return new UpdateLegalDeadlineUseCase(
            new DeadlineActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private sealed class ContextualAccessLookup(
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

    private sealed class FakeMutationPersistence(
        LegalDeadlineDetailsMutationPersistenceResult updateResult =
            LegalDeadlineDetailsMutationPersistenceResult.Updated)
        : ILegalDeadlineMutationPersistence
    {
        public LegalDeadline Deadline { get; } = new(
            UpdateLegalDeadlineUseCaseTests.OrganizationId,
            UpdateLegalDeadlineUseCaseTests.ProcessId,
            "Initial title",
            OriginalDueDate,
            CreatedAt);

        public int UpdateCallCount { get; private set; }
        public Guid DeadlineId { get; private set; }
        public Guid OrganizationId { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalDeadlineDetailsMutationPersistenceResult> UpdateDetailsAsync(
            Guid deadlineId,
            Guid organizationId,
            string title,
            DateOnly dueDate,
            CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            DeadlineId = deadlineId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            if (updateResult == LegalDeadlineDetailsMutationPersistenceResult.Updated)
            {
                Deadline.ChangeDetails(title, dueDate);
            }

            return Task.FromResult(updateResult);
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
            Guid deadlineId,
            Guid organizationId,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
            Guid deadlineId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
