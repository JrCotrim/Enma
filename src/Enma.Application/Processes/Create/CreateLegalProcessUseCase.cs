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
        Guid userId,
        Guid organizationId,
        Guid clientId,
        string title,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                ProcessAction.Create,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return CreateLegalProcessResult.AccessDenied;
        }

        bool activeClientExists = clientId != Guid.Empty &&
            await _activeClientLookup.ExistsAsync(
                clientId,
                organizationId,
                cancellationToken);

        if (!activeClientExists)
        {
            return CreateLegalProcessResult.RelatedClientUnavailable;
        }

        var request = new LegalProcessCreationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            clientId);
        LegalProcessCreationPersistenceResult persistenceResult =
            await _creationPersistence.ExecuteAsync(
                request,
                state => DecideCreation(request, state, title),
                cancellationToken);

        return persistenceResult.Status switch
        {
            LegalProcessCreationDecisionStatus.AccessDenied =>
                CreateLegalProcessResult.AccessDenied,
            LegalProcessCreationDecisionStatus.RelatedClientUnavailable =>
                CreateLegalProcessResult.RelatedClientUnavailable,
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
        string title)
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

        return LegalProcessCreationDecision.Persist(
            CreateLegalProcess(
                request.OrganizationId,
                request.ClientId,
                title,
                _timeProvider.GetUtcNow()));
    }

    private static LegalProcess CreateLegalProcess(
        Guid organizationId,
        Guid clientId,
        string title,
        DateTimeOffset createdAt)
    {
        try
        {
            return new LegalProcess(
                organizationId,
                clientId,
                title,
                createdAt);
        }
        catch (ArgumentException exception) when (exception.ParamName == "title")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }
}
