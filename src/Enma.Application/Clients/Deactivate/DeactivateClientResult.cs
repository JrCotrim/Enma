namespace Enma.Application.Clients.Deactivate;

public sealed class DeactivateClientResult
{
    private DeactivateClientResult(DeactivateClientResultStatus status)
    {
        Status = status;
    }

    public DeactivateClientResultStatus Status { get; }

    public static DeactivateClientResult AccessDenied { get; } = new(
        DeactivateClientResultStatus.AccessDenied);

    public static DeactivateClientResult NotFound { get; } = new(
        DeactivateClientResultStatus.NotFound);

    public static DeactivateClientResult Succeeded { get; } = new(
        DeactivateClientResultStatus.Succeeded);
}

public enum DeactivateClientResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
