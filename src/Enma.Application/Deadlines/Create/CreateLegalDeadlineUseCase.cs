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
        DeadlineActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                DeadlineAction.Create,
                cancellationToken);

        if (authorization == DeadlineActionAuthorizationResult.Denied)
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

        LegalDeadline legalDeadline = CreateLegalDeadline(
            organizationId,
            processId,
            title,
            dueDate,
            _timeProvider.GetUtcNow());

        await _creationPersistence.PersistAsync(legalDeadline, cancellationToken);

        return CreateLegalDeadlineResult.Created(legalDeadline.Id);
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
