using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Processes.Update;

public sealed class UpdateLegalProcessUseCase
{
    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessMutationPersistence _mutationPersistence;

    public UpdateLegalProcessUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<UpdateLegalProcessResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ProcessActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ProcessAction.Update,
                cancellationToken);

        if (authorization == ProcessActionAuthorizationResult.Denied)
        {
            return UpdateLegalProcessResult.AccessDenied;
        }

        if (processId == Guid.Empty)
        {
            return UpdateLegalProcessResult.NotFound;
        }

        LegalProcessMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _mutationPersistence.UpdateTitleAsync(
                processId,
                organizationId,
                title,
                cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "title")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult == LegalProcessMutationPersistenceResult.Updated
            ? UpdateLegalProcessResult.Updated
            : UpdateLegalProcessResult.NotFound;
    }
}
