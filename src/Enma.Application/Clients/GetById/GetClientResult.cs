namespace Enma.Application.Clients.GetById;

public sealed class GetClientResult
{
    private GetClientResult(
        GetClientResultStatus status,
        ClientReadModel? client)
    {
        Status = status;
        Client = client;
    }

    public GetClientResultStatus Status { get; }

    public ClientReadModel? Client { get; }

    public static GetClientResult AccessDenied { get; } = new(
        GetClientResultStatus.AccessDenied,
        null);

    public static GetClientResult NotFound { get; } = new(
        GetClientResultStatus.NotFound,
        null);

    public static GetClientResult Success(ClientReadModel client)
    {
        ArgumentNullException.ThrowIfNull(client);

        return new GetClientResult(
            GetClientResultStatus.Succeeded,
            client);
    }
}

public enum GetClientResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
