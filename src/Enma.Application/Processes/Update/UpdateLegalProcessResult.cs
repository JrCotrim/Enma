namespace Enma.Application.Processes.Update;

public sealed class UpdateLegalProcessResult
{
    private UpdateLegalProcessResult(UpdateLegalProcessResultStatus status)
    {
        Status = status;
    }

    public UpdateLegalProcessResultStatus Status { get; }

    public static UpdateLegalProcessResult AccessDenied { get; } = new(
        UpdateLegalProcessResultStatus.AccessDenied);

    public static UpdateLegalProcessResult NotFound { get; } = new(
        UpdateLegalProcessResultStatus.NotFound);

    public static UpdateLegalProcessResult Updated { get; } = new(
        UpdateLegalProcessResultStatus.Updated);
}

public enum UpdateLegalProcessResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Updated = 2
}
