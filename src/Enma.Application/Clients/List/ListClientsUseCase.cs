using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Clients.List;

public sealed class ListClientsUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly ClientActionAuthorization _actionAuthorization;
    private readonly IClientReadQueries _readQueries;

    public ListClientsUseCase(
        ClientActionAuthorization actionAuthorization,
        IClientReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListClientsResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePagination(pageNumber, pageSize);

        ClientActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ClientAction.View,
                cancellationToken);

        if (authorization == ClientActionAuthorizationResult.Denied)
        {
            return ListClientsResult.AccessDenied;
        }

        IReadOnlyList<ClientReadModel> clients = await _readQueries.ListAsync(
            organizationId,
            pageNumber,
            pageSize,
            cancellationToken);

        return ListClientsResult.Success(clients, pageNumber, pageSize);
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
