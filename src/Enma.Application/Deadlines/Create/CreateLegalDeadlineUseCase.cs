using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Deadlines;

namespace Enma.Application.Deadlines.Create;

public sealed class CreateLegalDeadlineUseCase
{
    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly IProcessOrganizationOwnershipLookup _processOwnershipLookup;
    private readonly ILegalDeadlineCreationPersistence _creationPersistence;
    private readonly TimeProvider _timeProvider;

    public CreateLegalDeadlineUseCase(
        DeadlineActionAuthorization actionAuthorization,
        IProcessOrganizationOwnershipLookup processOwnershipLookup,
        ILegalDeadlineCreationPersistence creationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(processOwnershipLookup);
        ArgumentNullException.ThrowIfNull(creationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _actionAuthorization = actionAuthorization;
        _processOwnershipLookup = processOwnershipLookup;
        _creationPersistence = creationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CreateLegalDeadlineResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        string title,
        DateOnly dueDate,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                DeadlineAction.Create,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return CreateLegalDeadlineResult.AccessDenied;
        }

        bool processExists = processId != Guid.Empty &&
            await _processOwnershipLookup.ExistsInOrganizationAsync(
                processId,
                organizationId,
                cancellationToken);

        if (!processExists)
        {
            return CreateLegalDeadlineResult.RelatedProcessUnavailable;
        }

        var request = new LegalDeadlineCreationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            processId);
        LegalDeadlineCreationPersistenceResult persistenceResult =
            await _creationPersistence.ExecuteAsync(
                request,
                state => DecideCreation(
                    request,
                    state,
                    title,
                    dueDate),
                cancellationToken);

        return persistenceResult.Status switch
        {
            LegalDeadlineCreationDecisionStatus.AccessDenied =>
                CreateLegalDeadlineResult.AccessDenied,
            LegalDeadlineCreationDecisionStatus.RelatedProcessUnavailable =>
                CreateLegalDeadlineResult.RelatedProcessUnavailable,
            LegalDeadlineCreationDecisionStatus.Persist
                when persistenceResult.DeadlineId is Guid deadlineId =>
                CreateLegalDeadlineResult.Created(deadlineId),
            _ => throw new InvalidOperationException(
                "Legal deadline creation persistence returned an invalid result.")
        };
    }

    private LegalDeadlineCreationDecision DecideCreation(
        LegalDeadlineCreationPersistenceRequest request,
        LegalDeadlineCreationLockedState state,
        string title,
        DateOnly dueDate)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(DeadlineAction.Create, actor.Role))
        {
            return LegalDeadlineCreationDecision.AccessDenied;
        }

        if (!state.IsProcessAvailable)
        {
            return LegalDeadlineCreationDecision.RelatedProcessUnavailable;
        }

        return LegalDeadlineCreationDecision.Persist(
            CreateLegalDeadline(
                request.OrganizationId,
                request.ProcessId,
                title,
                dueDate,
                _timeProvider.GetUtcNow()));
    }

    private static LegalDeadline CreateLegalDeadline(
        Guid organizationId,
        Guid processId,
        string title,
        DateOnly dueDate,
        DateTimeOffset createdAt)
    {
        try
        {
            return new LegalDeadline(
                organizationId,
                processId,
                title,
                dueDate,
                createdAt);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or "dueDate")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }
}
