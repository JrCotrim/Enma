using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Processes.List;

public sealed class ListLegalProcessesUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessReadQueries _readQueries;

    public ListLegalProcessesUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListLegalProcessesResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePagination(pageNumber, pageSize);

        ProcessActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ProcessAction.View,
                cancellationToken);

        if (authorization == ProcessActionAuthorizationResult.Denied)
        {
            return ListLegalProcessesResult.AccessDenied;
        }

        IReadOnlyList<LegalProcessReadModel> legalProcesses =
            await _readQueries.ListAsync(
                organizationId,
                pageNumber,
                pageSize,
                cancellationToken);

        return ListLegalProcessesResult.Success(
            legalProcesses,
            pageNumber,
            pageSize);
    }

    private static void ValidatePagination(int pageNumber, int pageSize)
    {
        if (pageNumber < 1)
        {
            throw new RequestValidationException(
                "Page number must be at least 1.");
        }

        if (pageSize < 1 || pageSize > MaximumPageSize)
        {
            throw new RequestValidationException(
                $"Page size must be between 1 and {MaximumPageSize}.");
        }
    }
}
