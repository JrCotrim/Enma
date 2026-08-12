namespace Enma.Application.Clients.Reactivate;

public sealed class ReactivateClientResult
{
    private ReactivateClientResult(ReactivateClientResultStatus status)
    {
        Status = status;
    }

    public ReactivateClientResultStatus Status { get; }

    public static ReactivateClientResult AccessDenied { get; } = new(
        ReactivateClientResultStatus.AccessDenied);

    public static ReactivateClientResult NotFound { get; } = new(
        ReactivateClientResultStatus.NotFound);

    public static ReactivateClientResult Succeeded { get; } = new(
        ReactivateClientResultStatus.Succeeded);
}

public enum ReactivateClientResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
