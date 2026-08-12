namespace Enma.Application.Clients.Create;

public sealed class CreateClientResult
{
    private CreateClientResult(
        CreateClientResultStatus status,
        Guid? clientId)
    {
        Status = status;
        ClientId = clientId;
    }

    public CreateClientResultStatus Status { get; }

    public Guid? ClientId { get; }

    public static CreateClientResult AccessDenied { get; } = new(
        CreateClientResultStatus.AccessDenied,
        null);

    public static CreateClientResult Success(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException(
                "Client id cannot be empty.",
                nameof(clientId));
        }

        return new CreateClientResult(
            CreateClientResultStatus.Succeeded,
            clientId);
    }
}

public enum CreateClientResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
