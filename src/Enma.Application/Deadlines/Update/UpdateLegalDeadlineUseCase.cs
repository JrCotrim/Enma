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
        DeadlineActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                DeadlineAction.Update,
                cancellationToken);

        if (authorization == DeadlineActionAuthorizationResult.Denied)
        {
            return UpdateLegalDeadlineResult.AccessDenied;
        }

        if (deadlineId == Guid.Empty)
        {
            return UpdateLegalDeadlineResult.NotFound;
        }

        LegalDeadlineDetailsMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _mutationPersistence.UpdateDetailsAsync(
                deadlineId,
                organizationId,
                title,
                dueDate,
                cancellationToken);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or "dueDate")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult switch
        {
            LegalDeadlineDetailsMutationPersistenceResult.Updated =>
                UpdateLegalDeadlineResult.Updated,
            LegalDeadlineDetailsMutationPersistenceResult.Conflict =>
                UpdateLegalDeadlineResult.Conflict,
            _ => UpdateLegalDeadlineResult.NotFound
        };
    }
}
