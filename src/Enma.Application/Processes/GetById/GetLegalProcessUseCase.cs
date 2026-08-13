using Enma.Application.Authorization;

namespace Enma.Application.Processes.GetById;

public sealed class GetLegalProcessUseCase
{
    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessReadQueries _readQueries;

    public GetLegalProcessUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<GetLegalProcessResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken = default)
    {
        ProcessActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ProcessAction.View,
                cancellationToken);

        if (authorization == ProcessActionAuthorizationResult.Denied)
        {
            return GetLegalProcessResult.AccessDenied;
        }

        if (processId == Guid.Empty)
        {
            return GetLegalProcessResult.NotFound;
        }

        LegalProcessReadModel? legalProcess = await _readQueries.FindAsync(
            processId,
            organizationId,
            cancellationToken);

        return legalProcess is null
            ? GetLegalProcessResult.NotFound
            : GetLegalProcessResult.Success(legalProcess);
    }
}
