using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Reopen;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Deadlines.Reopen;

public sealed class ReopenLegalDeadlineUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "f6abf458-40af-4166-b840-bb0f34440112");
    private static readonly Guid OrganizationId = Guid.Parse(
        "c2c25251-d959-4df2-886c-8041dd99d24f");
    private static readonly Guid MembershipId = Guid.Parse(
        "36468d1e-2f21-45d1-b783-a7058a49c4ee");
    private static readonly Guid ProcessId = Guid.Parse(
        "34bc4148-42e2-4b83-8c4d-e34a76067483");
    private static readonly Guid DeadlineId = Guid.Parse(
        "80cfec9e-cc2f-42d6-a654-f0b43ee03271");
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 8, 13, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAuthorizedRole_ReopensCompletedDeadline(
        OrganizationRole role)
    {
        var persistence = new FakeMutationPersistence(initiallyCompleted: true);
        ReopenLegalDeadlineUseCase useCase = CreateUseCase(role, persistence);

        ReopenLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Equal(ReopenLegalDeadlineResultStatus.Succeeded, result.Status);
        Assert.Null(persistence.Deadline.CompletedAt);
        Assert.Equal(OrganizationId, persistence.Deadline.OrganizationId);
        Assert.Equal(ProcessId, persistence.Deadline.ProcessId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesBeforePersistence()
    {
        var persistence = new FakeMutationPersistence(initiallyCompleted: true);
        ReopenLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        ReopenLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Same(ReopenLegalDeadlineResult.AccessDenied, result);
        Assert.Equal(0, persistence.ReopenCallCount);
        Assert.NotNull(persistence.Deadline.CompletedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WithUnavailableDeadline_ReturnsNotFound(
        bool emptyDeadlineId)
    {
        var persistence = new FakeMutationPersistence(
            LegalDeadlineLifecycleMutationPersistenceResult.NotFound,
            initiallyCompleted: true);
        ReopenLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ReopenLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            emptyDeadlineId ? Guid.Empty : DeadlineId);

        Assert.Same(ReopenLegalDeadlineResult.NotFound, result);
        Assert.Equal(emptyDeadlineId ? 0 : 1, persistence.ReopenCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAlreadyPending_RemainsSucceededAndPending()
    {
        var persistence = new FakeMutationPersistence(initiallyCompleted: false);
        ReopenLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        ReopenLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Same(ReopenLegalDeadlineResult.Succeeded, result);
        Assert.Null(persistence.Deadline.CompletedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithOwnerElsewhereAndMemberInContext_DeniesContextualMutation()
    {
        Guid otherOrganizationId = Guid.Parse(
            "ac4061d5-584c-4edf-b664-45dc99a76531");
        var lookup = new ContextualAccessLookup(
            OrganizationId,
            OrganizationRole.Member,
            otherOrganizationId,
            OrganizationRole.Owner);
        var persistence = new FakeMutationPersistence(initiallyCompleted: true);
        ReopenLegalDeadlineUseCase useCase = CreateUseCase(lookup, persistence);

        ReopenLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Same(ReopenLegalDeadlineResult.AccessDenied, result);
        Assert.Equal(0, persistence.ReopenCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsTenantAndCancellation()
    {
        var persistence = new FakeMutationPersistence(initiallyCompleted: true);
        var lookup = new ContextualAccessLookup(
            OrganizationId,
            OrganizationRole.Owner);
        ReopenLegalDeadlineUseCase useCase = CreateUseCase(lookup, persistence);
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

    private static ReopenLegalDeadlineUseCase CreateUseCase(
        OrganizationRole? role,
        FakeMutationPersistence persistence)
    {
        return CreateUseCase(
            new ContextualAccessLookup(OrganizationId, role),
            persistence);
    }

    private static ReopenLegalDeadlineUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeMutationPersistence persistence)
    {
        return new ReopenLegalDeadlineUseCase(
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

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
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

    private sealed class FakeMutationPersistence : ILegalDeadlineMutationPersistence
    {
        private readonly LegalDeadlineLifecycleMutationPersistenceResult _reopenResult;

        public FakeMutationPersistence(
            LegalDeadlineLifecycleMutationPersistenceResult reopenResult =
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
            bool initiallyCompleted = false)
        {
            _reopenResult = reopenResult;
            Deadline = new LegalDeadline(
                ReopenLegalDeadlineUseCaseTests.OrganizationId,
                ReopenLegalDeadlineUseCaseTests.ProcessId,
                "Initial title",
                new DateOnly(2026, 9, 1),
                CreatedAt);

            if (initiallyCompleted)
            {
                Deadline.Complete(CreatedAt.AddHours(1));
            }
        }

        public LegalDeadline Deadline { get; }
        public int ReopenCallCount { get; private set; }
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
            throw new NotSupportedException();
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            ReopenCallCount++;
            DeadlineId = request.DeadlineId;
            OrganizationId = request.OrganizationId;
            CancellationToken = cancellationToken;

            if (_reopenResult !=
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded)
            {
                return Task.FromResult(_reopenResult);
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
    }
}
