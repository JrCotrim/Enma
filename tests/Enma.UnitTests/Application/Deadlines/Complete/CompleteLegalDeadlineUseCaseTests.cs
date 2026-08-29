using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Complete;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Deadlines.Complete;

public sealed class CompleteLegalDeadlineUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "c7d308be-4f6c-4840-bf52-659bed052c0d");
    private static readonly Guid OrganizationId = Guid.Parse(
        "0d7bfc8c-daef-4596-9d93-18ff9e2db066");
    private static readonly Guid MembershipId = Guid.Parse(
        "df31c407-d23a-4a5f-9219-1ca6afee807f");
    private static readonly Guid ProcessId = Guid.Parse(
        "97c12f36-fd87-4567-8f6c-86659d918ab5");
    private static readonly Guid DeadlineId = Guid.Parse(
        "9cc57d3a-f48f-4ab6-ac9f-3453b47c3ffc");
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 8, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = CreatedAt.AddHours(2);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_UsesServerTimestamp(
        OrganizationRole role)
    {
        var persistence = new FakeMutationPersistence();
        var timeProvider = new CountingTimeProvider(CompletedAt);
        CompleteLegalDeadlineUseCase useCase = CreateUseCase(
            role,
            persistence,
            timeProvider);

        CompleteLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Equal(CompleteLegalDeadlineResultStatus.Succeeded, result.Status);
        Assert.Equal(CompletedAt, persistence.Deadline.CompletedAt);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
        Assert.Equal(OrganizationId, persistence.Deadline.OrganizationId);
        Assert.Equal(ProcessId, persistence.Deadline.ProcessId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesBeforeTimeAndPersistence()
    {
        var persistence = new FakeMutationPersistence();
        var timeProvider = new CountingTimeProvider(CompletedAt);
        CompleteLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence,
            timeProvider);

        CompleteLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Same(CompleteLegalDeadlineResult.AccessDenied, result);
        Assert.Equal(0, persistence.CompleteCallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WithUnavailableDeadline_ReturnsNotFound(
        bool emptyDeadlineId)
    {
        var persistence = new FakeMutationPersistence(
            LegalDeadlineLifecycleMutationPersistenceResult.NotFound);
        var timeProvider = new CountingTimeProvider(CompletedAt);
        CompleteLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            timeProvider);

        CompleteLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            emptyDeadlineId ? Guid.Empty : DeadlineId);

        Assert.Same(CompleteLegalDeadlineResult.NotFound, result);
        Assert.Equal(emptyDeadlineId ? 0 : 1, persistence.CompleteCallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAlreadyCompleted_PreservesFirstTimestamp()
    {
        DateTimeOffset firstCompletion = CreatedAt.AddHours(1);
        var persistence = new FakeMutationPersistence();
        persistence.Deadline.Complete(firstCompletion);
        CompleteLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence,
            new CountingTimeProvider(CompletedAt));

        CompleteLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Same(CompleteLegalDeadlineResult.Succeeded, result);
        Assert.Equal(firstCompletion, persistence.Deadline.CompletedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithServerClockBeforeCreation_PropagatesInvariantFailure()
    {
        var persistence = new FakeMutationPersistence();
        CompleteLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence,
            new CountingTimeProvider(CreatedAt.AddTicks(-1)));

        ArgumentOutOfRangeException exception =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                useCase.ExecuteAsync(UserId, OrganizationId, DeadlineId));

        Assert.Equal("completedAt", exception.ParamName);
        Assert.Null(persistence.Deadline.CompletedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsTenantAndCancellation()
    {
        var persistence = new FakeMutationPersistence();
        var lookup = new ContextualAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);
        CompleteLegalDeadlineUseCase useCase = CreateUseCase(
            lookup,
            persistence,
            new CountingTimeProvider(CompletedAt));
        using var cancellation = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            cancellation.Token);

        Assert.Equal(cancellation.Token, lookup.CancellationToken);
        Assert.Equal(cancellation.Token, persistence.CancellationToken);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(DeadlineId, persistence.DeadlineId);
    }

    private static CompleteLegalDeadlineUseCase CreateUseCase(
        OrganizationRole? role,
        FakeMutationPersistence persistence,
        TimeProvider timeProvider)
    {
        return CreateUseCase(
            new ContextualAccessLookup(OrganizationId, role),
            persistence,
            timeProvider);
    }

    private static CompleteLegalDeadlineUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeMutationPersistence persistence,
        TimeProvider timeProvider)
    {
        return new CompleteLegalDeadlineUseCase(
            new DeadlineActionAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence,
            timeProvider);
    }

    private sealed class ContextualAccessLookup(
        Guid organizationId,
        OrganizationRole? role) : IOrganizationAccessLookup
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid requestedOrganizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            return Task.FromResult(
                requestedOrganizationId == organizationId ? role : null);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid requestedOrganizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            OrganizationAccessLookupResult? result =
                requestedOrganizationId == organizationId && role.HasValue
                    ? new OrganizationAccessLookupResult(
                        userId,
                        requestedOrganizationId,
                        MembershipId,
                        role.Value)
                    : null;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeMutationPersistence(
        LegalDeadlineLifecycleMutationPersistenceResult completeResult =
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded)
        : ILegalDeadlineMutationPersistence
    {
        public LegalDeadline Deadline { get; } = new(
            CompleteLegalDeadlineUseCaseTests.OrganizationId,
            CompleteLegalDeadlineUseCaseTests.ProcessId,
            "Initial title",
            new DateOnly(2026, 9, 1),
            CreatedAt);

        public int CompleteCallCount { get; private set; }
        public Guid DeadlineId { get; private set; }
        public Guid OrganizationId { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalDeadlineDetailsMutationPersistenceResult> UpdateDetailsAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CompleteCallCount++;
            DeadlineId = request.DeadlineId;
            OrganizationId = request.OrganizationId;
            CancellationToken = cancellationToken;

            if (completeResult !=
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded)
            {
                return Task.FromResult(completeResult);
            }

            LegalDeadlineMutationDecision decision = decide(
                new LegalDeadlineMutationLockedState(
                    Deadline,
                    true,
                    new LegalDeadlineLockedActorState(
                        MembershipId,
                        request.OrganizationId,
                        request.UserId,
                        OrganizationRole.Owner,
                        true,
                        true)));

            return Task.FromResult(decision.Status ==
                LegalDeadlineMutationDecisionStatus.AccessDenied
                    ? LegalDeadlineLifecycleMutationPersistenceResult.AccessDenied
                    : LegalDeadlineLifecycleMutationPersistenceResult.Succeeded);
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CountingTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public int GetUtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCallCount++;
            return utcNow;
        }
    }
}
