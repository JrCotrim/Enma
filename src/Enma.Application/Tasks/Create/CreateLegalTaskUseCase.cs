using Enma.Application.Authorization;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;

namespace Enma.Application.Tasks.Create;

public sealed class CreateLegalTaskUseCase
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly IProcessOrganizationOwnershipLookup _processOwnershipLookup;
    private readonly ILegalTaskCreationPersistence _creationPersistence;
    private readonly TimeProvider _timeProvider;

    public CreateLegalTaskUseCase(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        IProcessOrganizationOwnershipLookup processOwnershipLookup,
        ILegalTaskCreationPersistence creationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(processOwnershipLookup);
        ArgumentNullException.ThrowIfNull(creationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _processOwnershipLookup = processOwnershipLookup;
        _creationPersistence = creationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CreateLegalTaskResult> ExecuteAsync(
        CreateLegalTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationAccessAuthorizationResult organizationAccess;

        try
        {
            organizationAccess = await _organizationAccessAuthorization.AuthorizeAsync(
                command.UserId,
                command.OrganizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return CreateLegalTaskResult.AccessDenied;
        }

        if (organizationAccess.Status == OrganizationAccessAuthorizationStatus.Denied ||
            organizationAccess.UserId is not Guid actorUserId ||
            organizationAccess.OrganizationId is not Guid contextualOrganizationId ||
            organizationAccess.MembershipId is not Guid actorMembershipId ||
            organizationAccess.Role is not OrganizationRole role ||
            actorUserId != command.UserId ||
            contextualOrganizationId != command.OrganizationId)
        {
            return CreateLegalTaskResult.AccessDenied;
        }

        if (!IsAssignmentAuthorized(
                role,
                actorMembershipId,
                command.AssigneeMembershipId))
        {
            return CreateLegalTaskResult.AccessDenied;
        }

        if (HasInvalidOptionalValue(command))
        {
            return CreateLegalTaskResult.InvalidInput;
        }

        if (command.ProcessId is Guid processId &&
            !await _processOwnershipLookup.ExistsInOrganizationAsync(
                processId,
                contextualOrganizationId,
                cancellationToken))
        {
            return CreateLegalTaskResult.RelatedProcessUnavailable;
        }

        var persistenceRequest = new LegalTaskCreationPersistenceRequest(
            actorUserId,
            contextualOrganizationId,
            actorMembershipId,
            command.AssigneeMembershipId,
            command.ProcessId);

        LegalTaskCreationPersistenceResult persistenceResult =
            await _creationPersistence.ExecuteAsync(
                persistenceRequest,
                lockedState => DecideCreation(
                    command,
                    persistenceRequest,
                    lockedState),
                cancellationToken);

        return persistenceResult.Status switch
        {
            LegalTaskCreationDecisionStatus.AccessDenied =>
                CreateLegalTaskResult.AccessDenied,
            LegalTaskCreationDecisionStatus.RelatedAssigneeUnavailable =>
                CreateLegalTaskResult.RelatedAssigneeUnavailable,
            LegalTaskCreationDecisionStatus.RelatedProcessUnavailable =>
                CreateLegalTaskResult.RelatedProcessUnavailable,
            LegalTaskCreationDecisionStatus.InvalidInput =>
                CreateLegalTaskResult.InvalidInput,
            LegalTaskCreationDecisionStatus.Persist
                when persistenceResult.LegalTaskId is Guid legalTaskId =>
                CreateLegalTaskResult.Succeeded(legalTaskId),
            _ => throw new InvalidOperationException(
                "Legal task creation persistence returned an invalid result.")
        };
    }

    private LegalTaskCreationDecision DecideCreation(
        CreateLegalTaskCommand command,
        LegalTaskCreationPersistenceRequest request,
        LegalTaskCreationLockedState lockedState)
    {
        if (!lockedState.IsOrganizationActive ||
            lockedState.Actor is not LegalTaskCreationMemberState actor ||
            !IsAvailableActor(actor, request))
        {
            return LegalTaskCreationDecision.AccessDenied;
        }

        if (!IsAssignmentAuthorized(
                actor.Role,
                actor.MembershipId,
                command.AssigneeMembershipId))
        {
            return LegalTaskCreationDecision.AccessDenied;
        }

        if (command.AssigneeMembershipId is not null &&
            !IsAvailableAssignee(
                lockedState.Assignee,
                request.OrganizationId,
                command.AssigneeMembershipId.Value))
        {
            return LegalTaskCreationDecision.RelatedAssigneeUnavailable;
        }

        try
        {
            var legalTask = new LegalTask(
                request.OrganizationId,
                command.Title,
                command.Description,
                command.DueDate,
                command.ProcessId,
                command.AssigneeMembershipId,
                actor.MembershipId,
                _timeProvider.GetUtcNow());

            return LegalTaskCreationDecision.Persist(legalTask);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or
                "description" or
                "dueDate" or
                "processId" or
                "assigneeMembershipId")
        {
            return LegalTaskCreationDecision.InvalidInput;
        }
    }

    private static bool IsAvailableActor(
        LegalTaskCreationMemberState actor,
        LegalTaskCreationPersistenceRequest request)
    {
        return actor.MembershipId == request.ActorMembershipId &&
            actor.OrganizationId == request.OrganizationId &&
            actor.UserId == request.UserId &&
            actor.IsMembershipActive &&
            actor.IsUserActive &&
            Enum.IsDefined(actor.Role);
    }

    private static bool IsAvailableAssignee(
        LegalTaskCreationMemberState? assignee,
        Guid organizationId,
        Guid assigneeMembershipId)
    {
        return assignee is not null &&
            assignee.MembershipId == assigneeMembershipId &&
            assignee.OrganizationId == organizationId &&
            assignee.IsMembershipActive &&
            assignee.IsUserActive;
    }

    private static bool IsAssignmentAuthorized(
        OrganizationRole role,
        Guid actorMembershipId,
        Guid? assigneeMembershipId)
    {
        return role switch
        {
            OrganizationRole.Owner => true,
            OrganizationRole.Administrator => true,
            OrganizationRole.Member =>
                assigneeMembershipId is null ||
                assigneeMembershipId == actorMembershipId,
            _ => false
        };
    }

    private static bool HasInvalidOptionalValue(CreateLegalTaskCommand command)
    {
        return command.ProcessId == Guid.Empty ||
            command.AssigneeMembershipId == Guid.Empty ||
            command.DueDate == DateOnly.MinValue;
    }
}
