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
        ProcessActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ProcessAction.Create,
                cancellationToken);

        if (authorization == ProcessActionAuthorizationResult.Denied)
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

        LegalProcess legalProcess = CreateLegalProcess(
            organizationId,
            clientId,
            title,
            _timeProvider.GetUtcNow());

        await _creationPersistence.PersistAsync(legalProcess, cancellationToken);

        return CreateLegalProcessResult.Success(legalProcess.Id);
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
