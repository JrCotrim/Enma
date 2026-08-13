using Enma.Application.Authorization;

namespace Enma.Application.Deadlines.GetById;

public sealed class GetLegalDeadlineUseCase
{
    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly ILegalDeadlineReadQueries _readQueries;

    public GetLegalDeadlineUseCase(
        DeadlineActionAuthorization actionAuthorization,
        ILegalDeadlineReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<GetLegalDeadlineResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid deadlineId,
        CancellationToken cancellationToken = default)
    {
        DeadlineActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                DeadlineAction.View,
                cancellationToken);

        if (authorization == DeadlineActionAuthorizationResult.Denied)
        {
            return GetLegalDeadlineResult.AccessDenied;
        }

        if (deadlineId == Guid.Empty)
        {
            return GetLegalDeadlineResult.NotFound;
        }

        LegalDeadlineDetailReadModel? legalDeadline = await _readQueries.FindAsync(
            deadlineId,
            organizationId,
            cancellationToken);

        return legalDeadline is null
            ? GetLegalDeadlineResult.NotFound
            : GetLegalDeadlineResult.Success(legalDeadline);
    }
}
