namespace Enma.Application.Clients.Update;

public sealed class UpdateClientResult
{
    private UpdateClientResult(UpdateClientResultStatus status)
    {
        Status = status;
    }

    public UpdateClientResultStatus Status { get; }

    public static UpdateClientResult AccessDenied { get; } = new(
        UpdateClientResultStatus.AccessDenied);

    public static UpdateClientResult NotFound { get; } = new(
        UpdateClientResultStatus.NotFound);

    public static UpdateClientResult Succeeded { get; } = new(
        UpdateClientResultStatus.Succeeded);
}

public enum UpdateClientResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
