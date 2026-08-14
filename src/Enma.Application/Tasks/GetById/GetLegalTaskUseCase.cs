using Enma.Application.Authorization;

namespace Enma.Application.Tasks.GetById;

public sealed class GetLegalTaskUseCase
{
    private readonly LegalTaskViewAuthorization _viewAuthorization;
    private readonly ILegalTaskReadQueries _readQueries;

    public GetLegalTaskUseCase(
        LegalTaskViewAuthorization viewAuthorization,
        ILegalTaskReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(viewAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);
        _viewAuthorization = viewAuthorization;
        _readQueries = readQueries;
    }

    public async Task<GetLegalTaskResult> ExecuteAsync(
        GetLegalTaskQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        LegalTaskViewAuthorizationResult authorization =
            await _viewAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                cancellationToken);

        if (authorization.Status == LegalTaskViewAuthorizationStatus.Denied)
        {
            return GetLegalTaskResult.AccessDenied;
        }

        if (query.LegalTaskId == Guid.Empty)
        {
            return GetLegalTaskResult.InvalidInput;
        }

        LegalTaskDetailReadModel? legalTask = await _readQueries.FindAsync(
            query.LegalTaskId,
            query.OrganizationId,
            cancellationToken);

        return legalTask is null
            ? GetLegalTaskResult.NotFound
            : GetLegalTaskResult.Succeeded(legalTask);
    }
}
