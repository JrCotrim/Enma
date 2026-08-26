using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Assignment;
using Enma.Application.Tasks.Complete;
using Enma.Application.Tasks.Reopen;
using Enma.Application.Tasks.Update;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;

namespace Enma.UnitTests.Application.Tasks;

public sealed class LegalTaskMutationUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "816736e8-76eb-4c1e-a81b-76ac7e1e8013");
    private static readonly Guid OrganizationId = Guid.Parse(
        "2012e50d-9dfb-4a81-b5e4-7bb172180781");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "d80652f5-e5c3-46f9-be2e-caeaf31e17b3");
    private static readonly Guid OtherMembershipId = Guid.Parse(
        "e42c9531-8f9c-484f-9755-f9a462373e9e");
    private static readonly Guid OtherUserId = Guid.Parse(
        "ef5716f4-85cd-4591-bb0a-6359c0b47606");
    private static readonly Guid ProcessId = Guid.Parse(
        "ba0c187b-07f1-4448-bf6f-137464191f0e");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        20,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = CreatedAt.AddHours(2);

    [Fact]
    public async Task UpdateAsync_NoOrganizationAccess_ReturnsAccessDeniedBeforePersistence()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(
            access: null,
            persistence);

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateUpdateCommand(legalTask.Id, ProcessId));

        Assert.Equal(UpdateLegalTaskResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task UpdateAsync_AuthorizedPendingTask_ValidatesProcessAndUpdatesDetails()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Member);
        persistence.ProcessAvailable = true;
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(
            CreateAccess(OrganizationRole.Member),
            persistence);

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateUpdateCommand(legalTask.Id, ProcessId));

        Assert.Equal(UpdateLegalTaskResult.Succeeded, result);
        Assert.Equal("Updated task", legalTask.Title);
        Assert.Equal(ProcessId, legalTask.ProcessId);
        Assert.Equal(1, persistence.ProcessValidationCount);
    }

    [Fact]
    public async Task UpdateAsync_UnavailableProcess_ReturnsRelatedProcessUnavailableWithoutMutation()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        persistence.ProcessAvailable = false;
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(
            CreateAccess(OrganizationRole.Owner),
            persistence);

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateUpdateCommand(legalTask.Id, ProcessId));

        Assert.Equal(UpdateLegalTaskResult.RelatedProcessUnavailable, result);
        Assert.Equal("Original task", legalTask.Title);
        Assert.Null(legalTask.ProcessId);
    }

    [Fact]
    public async Task UpdateAsync_MemberNonOwnTask_DeniesBeforeProcessValidation()
    {
        LegalTask legalTask = CreateTask(OtherMembershipId, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Member);
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(
            CreateAccess(OrganizationRole.Member),
            persistence);

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateUpdateCommand(legalTask.Id, ProcessId));

        Assert.Equal(UpdateLegalTaskResult.AccessDenied, result);
        Assert.Equal(0, persistence.ProcessValidationCount);
        Assert.Equal("Original task", legalTask.Title);
    }

    [Fact]
    public async Task UpdateAsync_CompletedAuthorizedTask_ReturnsConflictBeforeProcessValidation()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId, completed: true);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(
            CreateAccess(OrganizationRole.Owner),
            persistence);

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateUpdateCommand(legalTask.Id, ProcessId));

        Assert.Equal(UpdateLegalTaskResult.Conflict, result);
        Assert.Equal(0, persistence.ProcessValidationCount);
    }

    [Fact]
    public async Task UpdateAsync_InvalidDomainInput_ReturnsInvalidInput()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(
            CreateAccess(OrganizationRole.Owner),
            persistence);
        var command = new UpdateLegalTaskCommand(
            UserId,
            OrganizationId,
            legalTask.Id,
            "   ",
            null,
            null,
            null);

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(command);

        Assert.Equal(UpdateLegalTaskResult.InvalidInput, result);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_ForbiddenMemberTransition_DeniesWithoutTargetLock()
    {
        LegalTask legalTask = CreateTask(OtherMembershipId, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Member);
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(
            CreateAccess(OrganizationRole.Member),
            persistence);

        ChangeLegalTaskAssigneeResult result = await useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                UserId,
                OrganizationId,
                legalTask.Id,
                Guid.NewGuid()));

        Assert.Equal(ChangeLegalTaskAssigneeResult.AccessDenied, result);
        Assert.Null(persistence.SelectedAssigneeMembershipId);
        Assert.Equal(0, persistence.AssigneeLockRetryCount);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_CompletedAuthorizedTask_ConflictsWithoutTargetLock()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId, completed: true);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Administrator);
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(
            CreateAccess(OrganizationRole.Administrator),
            persistence);

        ChangeLegalTaskAssigneeResult result = await useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                UserId,
                OrganizationId,
                legalTask.Id,
                OtherMembershipId));

        Assert.Equal(ChangeLegalTaskAssigneeResult.Conflict, result);
        Assert.Null(persistence.SelectedAssigneeMembershipId);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_UnavailableTarget_CollapsesToRelatedAssigneeUnavailable()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        persistence.Assignee = CreateMemberState(
            OtherMembershipId,
            OtherUserId,
            OrganizationRole.Member,
            isMembershipActive: false,
            isUserActive: true);
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(
            CreateAccess(OrganizationRole.Owner),
            persistence);

        ChangeLegalTaskAssigneeResult result = await useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                UserId,
                OrganizationId,
                legalTask.Id,
                OtherMembershipId));

        Assert.Equal(
            ChangeLegalTaskAssigneeResult.RelatedAssigneeUnavailable,
            result);
        Assert.Null(legalTask.AssigneeMembershipId);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_EligibleTarget_AssignsExactMembership()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        persistence.Assignee = CreateMemberState(
            OtherMembershipId,
            OtherUserId,
            OrganizationRole.Member,
            isMembershipActive: true,
            isUserActive: true);
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(
            CreateAccess(OrganizationRole.Owner),
            persistence);

        ChangeLegalTaskAssigneeResult result = await useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                UserId,
                OrganizationId,
                legalTask.Id,
                OtherMembershipId));

        Assert.Equal(ChangeLegalTaskAssigneeResult.Succeeded, result);
        Assert.Equal(OtherMembershipId, legalTask.AssigneeMembershipId);
        Assert.Equal(OtherMembershipId, persistence.SelectedAssigneeMembershipId);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_IdempotentExistingTarget_DoesNotRevalidateHistoricalIdentity()
    {
        LegalTask legalTask = CreateTask(OtherMembershipId, ActorMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(
            CreateAccess(OrganizationRole.Owner),
            persistence);

        ChangeLegalTaskAssigneeResult result = await useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                UserId,
                OrganizationId,
                legalTask.Id,
                OtherMembershipId));

        Assert.Equal(ChangeLegalTaskAssigneeResult.Succeeded, result);
        Assert.Null(persistence.SelectedAssigneeMembershipId);
        Assert.Equal(OtherMembershipId, legalTask.AssigneeMembershipId);
    }

    [Fact]
    public async Task CompleteAsync_PendingOwnTask_UsesServerTime()
    {
        LegalTask legalTask = CreateTask(ActorMembershipId, OtherMembershipId);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Member);
        var timeProvider = new FixedTimeProvider(CompletedAt);
        var useCase = new CompleteLegalTaskUseCase(
            CreateOrganizationAccess(CreateAccess(OrganizationRole.Member)),
            new LegalTaskMutationAuthorization(),
            persistence,
            timeProvider);

        CompleteLegalTaskResult result = await useCase.ExecuteAsync(
            new CompleteLegalTaskCommand(UserId, OrganizationId, legalTask.Id));

        Assert.Equal(CompleteLegalTaskResult.Succeeded, result);
        Assert.Equal(CompletedAt, legalTask.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_AlreadyCompleted_PreservesFirstTimestamp()
    {
        LegalTask legalTask = CreateTask(null, ActorMembershipId, completed: true);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        var useCase = new CompleteLegalTaskUseCase(
            CreateOrganizationAccess(CreateAccess(OrganizationRole.Owner)),
            new LegalTaskMutationAuthorization(),
            persistence,
            new FixedTimeProvider(CompletedAt.AddHours(1)));

        CompleteLegalTaskResult result = await useCase.ExecuteAsync(
            new CompleteLegalTaskCommand(UserId, OrganizationId, legalTask.Id));

        Assert.Equal(CompleteLegalTaskResult.Succeeded, result);
        Assert.Equal(CompletedAt, legalTask.CompletedAt);
    }

    [Fact]
    public async Task ReopenAsync_CompletedOwnTask_Reopens()
    {
        LegalTask legalTask = CreateTask(ActorMembershipId, OtherMembershipId, true);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Member);
        var useCase = new ReopenLegalTaskUseCase(
            CreateOrganizationAccess(CreateAccess(OrganizationRole.Member)),
            new LegalTaskMutationAuthorization(),
            persistence);

        ReopenLegalTaskResult result = await useCase.ExecuteAsync(
            new ReopenLegalTaskCommand(UserId, OrganizationId, legalTask.Id));

        Assert.Equal(ReopenLegalTaskResult.Succeeded, result);
        Assert.Null(legalTask.CompletedAt);
    }

    [Fact]
    public async Task ReopenAsync_CompletedTaskWithUnavailableAssignee_IsRejected()
    {
        LegalTask legalTask = CreateTask(
            OtherMembershipId,
            ActorMembershipId,
            true);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Owner);
        var useCase = new ReopenLegalTaskUseCase(
            CreateOrganizationAccess(CreateAccess(OrganizationRole.Owner)),
            new LegalTaskMutationAuthorization(),
            persistence);

        ReopenLegalTaskResult result = await useCase.ExecuteAsync(
            new ReopenLegalTaskCommand(UserId, OrganizationId, legalTask.Id));

        Assert.Equal(
            ReopenLegalTaskResult.RelatedAssigneeUnavailable,
            result);
        Assert.Equal(CompletedAt, legalTask.CompletedAt);
    }

    [Fact]
    public async Task ReopenAsync_MemberNonOwnTask_ReturnsAccessDenied()
    {
        LegalTask legalTask = CreateTask(OtherMembershipId, ActorMembershipId, true);
        var persistence = CreatePersistence(legalTask, OrganizationRole.Member);
        var useCase = new ReopenLegalTaskUseCase(
            CreateOrganizationAccess(CreateAccess(OrganizationRole.Member)),
            new LegalTaskMutationAuthorization(),
            persistence);

        ReopenLegalTaskResult result = await useCase.ExecuteAsync(
            new ReopenLegalTaskCommand(UserId, OrganizationId, legalTask.Id));

        Assert.Equal(ReopenLegalTaskResult.AccessDenied, result);
        Assert.Equal(CompletedAt, legalTask.CompletedAt);
    }

    private static UpdateLegalTaskUseCase CreateUpdateUseCase(
        OrganizationAccessLookupResult? access,
        StubMutationPersistence persistence)
    {
        return new UpdateLegalTaskUseCase(
            CreateOrganizationAccess(access),
            new LegalTaskMutationAuthorization(),
            persistence);
    }

    private static ChangeLegalTaskAssigneeUseCase CreateAssignmentUseCase(
        OrganizationAccessLookupResult? access,
        StubMutationPersistence persistence)
    {
        return new ChangeLegalTaskAssigneeUseCase(
            CreateOrganizationAccess(access),
            new LegalTaskMutationAuthorization(),
            persistence);
    }

    private static OrganizationAccessAuthorization CreateOrganizationAccess(
        OrganizationAccessLookupResult? access)
    {
        return new OrganizationAccessAuthorization(new StubAccessLookup(access));
    }

    private static StubMutationPersistence CreatePersistence(
        LegalTask legalTask,
        OrganizationRole role)
    {
        return new StubMutationPersistence(
            legalTask,
            CreateMemberState(
                ActorMembershipId,
                UserId,
                role,
                isMembershipActive: true,
                isUserActive: true));
    }

    private static LegalTaskMutationMemberState CreateMemberState(
        Guid membershipId,
        Guid userId,
        OrganizationRole role,
        bool isMembershipActive,
        bool isUserActive)
    {
        return new LegalTaskMutationMemberState(
            membershipId,
            OrganizationId,
            userId,
            role,
            isMembershipActive,
            isUserActive);
    }

    private static OrganizationAccessLookupResult CreateAccess(OrganizationRole role)
    {
        return new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            ActorMembershipId,
            role);
    }

    private static UpdateLegalTaskCommand CreateUpdateCommand(
        Guid taskId,
        Guid? processId)
    {
        return new UpdateLegalTaskCommand(
            UserId,
            OrganizationId,
            taskId,
            "Updated task",
            "Updated description",
            new DateOnly(2026, 9, 1),
            processId);
    }

    private static LegalTask CreateTask(
        Guid? assigneeMembershipId,
        Guid createdByMembershipId,
        bool completed = false)
    {
        var legalTask = new LegalTask(
            OrganizationId,
            "Original task",
            null,
            null,
            null,
            assigneeMembershipId,
            createdByMembershipId,
            CreatedAt);

        if (completed)
        {
            legalTask.Complete(CompletedAt);
        }

        return legalTask;
    }

    private sealed class StubAccessLookup(OrganizationAccessLookupResult? access)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Task mutations must use the full live access lookup.");
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(access);
        }
    }

    private sealed class StubMutationPersistence(
        LegalTask legalTask,
        LegalTaskMutationMemberState actor)
        : ILegalTaskMutationPersistence
    {
        public int CallCount { get; private set; }

        public int ProcessValidationCount { get; private set; }

        public int AssigneeLockRetryCount { get; private set; }

        public Guid? SelectedAssigneeMembershipId { get; private set; }

        public bool ProcessAvailable { get; set; }

        public LegalTaskMutationMemberState? Assignee { get; set; }

        public Task<LegalTaskMutationPersistenceResult> ExecuteAsync(
            LegalTaskMutationPersistenceRequest request,
            Func<LegalTaskMutationPreviewState, Guid?> selectAssigneeToLock,
            Func<LegalTaskMutationLockedState, LegalTaskMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            SelectedAssigneeMembershipId = selectAssigneeToLock(
                new LegalTaskMutationPreviewState(
                    LegalTaskMutationTaskState.From(legalTask),
                    actor));

            LegalTaskMutationDecision decision = decide(CreateLockedState(
                SelectedAssigneeMembershipId is not null));

            if (decision.Status == LegalTaskMutationDecisionStatus.LockAssignee)
            {
                AssigneeLockRetryCount++;
                SelectedAssigneeMembershipId = decision.RelationId;
                decision = decide(CreateLockedState(assigneeLookupPerformed: true));
            }

            if (decision.Status == LegalTaskMutationDecisionStatus.ValidateProcess)
            {
                ProcessValidationCount++;
                decision = decide(CreateLockedState(
                    SelectedAssigneeMembershipId is not null) with
                {
                    ValidatedProcessId = decision.RelationId,
                    IsProcessAvailable = ProcessAvailable
                });
            }

            return Task.FromResult(Map(decision.Status));
        }

        private LegalTaskMutationLockedState CreateLockedState(
            bool assigneeLookupPerformed)
        {
            LegalTaskMutationMemberState? assignee = Assignee;

            if (assignee is null &&
                SelectedAssigneeMembershipId == actor.MembershipId)
            {
                assignee = actor;
            }

            return new LegalTaskMutationLockedState(
                legalTask,
                actor,
                assigneeLookupPerformed,
                assigneeLookupPerformed ? assignee : null,
                null,
                null);
        }

        private static LegalTaskMutationPersistenceResult Map(
            LegalTaskMutationDecisionStatus status)
        {
            return status switch
            {
                LegalTaskMutationDecisionStatus.AccessDenied =>
                    LegalTaskMutationPersistenceResult.AccessDenied,
                LegalTaskMutationDecisionStatus.RelatedProcessUnavailable =>
                    LegalTaskMutationPersistenceResult.RelatedProcessUnavailable,
                LegalTaskMutationDecisionStatus.RelatedAssigneeUnavailable =>
                    LegalTaskMutationPersistenceResult.RelatedAssigneeUnavailable,
                LegalTaskMutationDecisionStatus.InvalidInput =>
                    LegalTaskMutationPersistenceResult.InvalidInput,
                LegalTaskMutationDecisionStatus.Conflict =>
                    LegalTaskMutationPersistenceResult.Conflict,
                LegalTaskMutationDecisionStatus.Persist =>
                    LegalTaskMutationPersistenceResult.Succeeded,
                _ => throw new InvalidOperationException(
                    "The use case returned an incomplete mutation decision.")
            };
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
