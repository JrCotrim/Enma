using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Processes;

namespace Enma.Application.Processes.Create;

public sealed class CreateLegalProcessUseCase
{
    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly IActiveClientInOrganizationLookup _activeClientLookup;
    private readonly ILegalProcessCreationPersistence _creationPersistence;
    private readonly TimeProvider _timeProvider;

    public CreateLegalProcessUseCase(
        ProcessActionAuthorization actionAuthorization,
        IActiveClientInOrganizationLookup activeClientLookup,
        ILegalProcessCreationPersistence creationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(activeClientLookup);
        ArgumentNullException.ThrowIfNull(creationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _actionAuthorization = actionAuthorization;
        _activeClientLookup = activeClientLookup;
        _creationPersistence = creationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CreateLegalProcessResult> ExecuteAsync(
        CreateLegalProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                command.UserId,
                command.OrganizationId,
                ProcessAction.Create,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return CreateLegalProcessResult.AccessDenied;
        }

        LegalProcessStatus status = command.Status is null
            ? LegalProcessStatus.InProgress
            : LegalProcessStatusParser.Parse(command.Status);

        if (command.ResponsibleMembershipId == Guid.Empty)
        {
            throw new RequestValidationException(
                LegalProcessErrors.ResponsibleMembershipIdInvalid);
        }

        bool activeClientExists = command.ClientId != Guid.Empty &&
            await _activeClientLookup.ExistsAsync(
                command.ClientId,
                command.OrganizationId,
                cancellationToken);

        if (!activeClientExists)
        {
            return CreateLegalProcessResult.RelatedClientUnavailable;
        }

        var request = new LegalProcessCreationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.ClientId,
            command.ResponsibleMembershipId);
        LegalProcessCreationPersistenceResult persistenceResult =
            await _creationPersistence.ExecuteAsync(
                request,
                state => DecideCreation(request, state, command, status),
                cancellationToken);

        return persistenceResult.Status switch
        {
            LegalProcessCreationDecisionStatus.AccessDenied =>
                CreateLegalProcessResult.AccessDenied,
            LegalProcessCreationDecisionStatus.RelatedClientUnavailable =>
                CreateLegalProcessResult.RelatedClientUnavailable,
            LegalProcessCreationDecisionStatus.RelatedResponsibleUnavailable =>
                CreateLegalProcessResult.RelatedResponsibleUnavailable,
            LegalProcessCreationDecisionStatus.DuplicateProcessNumber =>
                CreateLegalProcessResult.DuplicateProcessNumber,
            LegalProcessCreationDecisionStatus.Persist
                when persistenceResult.ProcessId is Guid processId =>
                CreateLegalProcessResult.Success(processId),
            _ => throw new InvalidOperationException(
                "Legal process creation persistence returned an invalid result.")
        };
    }

    private LegalProcessCreationDecision DecideCreation(
        LegalProcessCreationPersistenceRequest request,
        LegalProcessCreationLockedState state,
        CreateLegalProcessCommand command,
        LegalProcessStatus status)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(ProcessAction.Create, actor.Role))
        {
            return LegalProcessCreationDecision.AccessDenied;
        }

        if (!state.IsClientAvailable)
        {
            return LegalProcessCreationDecision.RelatedClientUnavailable;
        }

        if (request.ResponsibleMembershipId is Guid responsibleMembershipId &&
            state.ResponsibleMember?.IsAvailableMemberOf(
                request.OrganizationId,
                responsibleMembershipId) != true)
        {
            return LegalProcessCreationDecision.RelatedResponsibleUnavailable;
        }

        return LegalProcessCreationDecision.Persist(
            CreateLegalProcess(
                request,
                command,
                status,
                _timeProvider.GetUtcNow()));
    }

    private static LegalProcess CreateLegalProcess(
        LegalProcessCreationPersistenceRequest request,
        CreateLegalProcessCommand command,
        LegalProcessStatus status,
        DateTimeOffset createdAt)
    {
        LegalProcess legalProcess;

        try
        {
            legalProcess = new LegalProcess(
                request.OrganizationId,
                request.ClientId,
                command.Title,
                createdAt,
                command.ProcessNumber,
                command.CourtOrAuthority,
                request.ResponsibleMembershipId);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or "processNumber" or "courtOrAuthority")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        if (status != LegalProcessStatus.InProgress)
        {
            legalProcess.ChangeStatus(status);
        }

        return legalProcess;
    }
}
