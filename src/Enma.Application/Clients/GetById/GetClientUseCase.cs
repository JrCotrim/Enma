using Enma.Application.Authorization;

namespace Enma.Application.Clients.GetById;

public sealed class GetClientUseCase
{
    private readonly ClientActionAuthorization _actionAuthorization;
    private readonly IClientReadQueries _readQueries;

    public GetClientUseCase(
        ClientActionAuthorization actionAuthorization,
        IClientReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<GetClientResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        ClientActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ClientAction.View,
                cancellationToken);

        if (authorization == ClientActionAuthorizationResult.Denied)
        {
            return GetClientResult.AccessDenied;
        }

        ClientReadModel? client = await _readQueries.FindAsync(
            clientId,
            organizationId,
            cancellationToken);

        return client is null
            ? GetClientResult.NotFound
            : GetClientResult.Success(client);
    }
}
