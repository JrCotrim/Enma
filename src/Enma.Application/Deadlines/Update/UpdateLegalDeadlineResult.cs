namespace Enma.Application.Deadlines.Update;

public sealed class UpdateLegalDeadlineResult
{
    private UpdateLegalDeadlineResult(UpdateLegalDeadlineResultStatus status)
    {
        Status = status;
    }

    public UpdateLegalDeadlineResultStatus Status { get; }

    public static UpdateLegalDeadlineResult AccessDenied { get; } = new(
        UpdateLegalDeadlineResultStatus.AccessDenied);

    public static UpdateLegalDeadlineResult NotFound { get; } = new(
        UpdateLegalDeadlineResultStatus.NotFound);

    public static UpdateLegalDeadlineResult Conflict { get; } = new(
        UpdateLegalDeadlineResultStatus.Conflict);

    public static UpdateLegalDeadlineResult Updated { get; } = new(
        UpdateLegalDeadlineResultStatus.Updated);
}

public enum UpdateLegalDeadlineResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Conflict = 2,
    Updated = 3
}
