using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Deadlines.Update;

public sealed class UpdateLegalDeadlineUseCase
{
    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly ILegalDeadlineMutationPersistence _mutationPersistence;

    public UpdateLegalDeadlineUseCase(
        DeadlineActionAuthorization actionAuthorization,
        ILegalDeadlineMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<UpdateLegalDeadlineResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid deadlineId,
        string title,
        DateOnly dueDate,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                DeadlineAction.Update,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return UpdateLegalDeadlineResult.AccessDenied;
        }

        if (deadlineId == Guid.Empty)
        {
            return UpdateLegalDeadlineResult.NotFound;
        }

        var request = new LegalDeadlineMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            deadlineId);
        LegalDeadlineDetailsMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _mutationPersistence.UpdateDetailsAsync(
                request,
                state => DecideUpdate(request, state, title, dueDate),
                cancellationToken);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or "dueDate")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult switch
        {
            LegalDeadlineDetailsMutationPersistenceResult.AccessDenied =>
                UpdateLegalDeadlineResult.AccessDenied,
            LegalDeadlineDetailsMutationPersistenceResult.Updated =>
                UpdateLegalDeadlineResult.Updated,
            LegalDeadlineDetailsMutationPersistenceResult.Conflict =>
                UpdateLegalDeadlineResult.Conflict,
            _ => UpdateLegalDeadlineResult.NotFound
        };
    }

    private LegalDeadlineMutationDecision DecideUpdate(
        LegalDeadlineMutationPersistenceRequest request,
        LegalDeadlineMutationLockedState state,
        string title,
        DateOnly dueDate)
    {
        if (!IsAuthorized(request, state, DeadlineAction.Update))
        {
            return LegalDeadlineMutationDecision.AccessDenied;
        }

        if (state.LegalDeadline.CompletedAt is not null)
        {
            return LegalDeadlineMutationDecision.Conflict;
        }

        state.LegalDeadline.ChangeDetails(title, dueDate);
        return LegalDeadlineMutationDecision.Persist;
    }

    private bool IsAuthorized(
        LegalDeadlineMutationPersistenceRequest request,
        LegalDeadlineMutationLockedState state,
        DeadlineAction action)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            _actionAuthorization.CanExecute(action, actor.Role);
    }
}
