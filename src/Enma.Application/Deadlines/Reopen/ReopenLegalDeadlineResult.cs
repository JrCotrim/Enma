namespace Enma.Application.Deadlines.Reopen;

public sealed class ReopenLegalDeadlineResult
{
    private ReopenLegalDeadlineResult(ReopenLegalDeadlineResultStatus status)
    {
        Status = status;
    }

    public ReopenLegalDeadlineResultStatus Status { get; }

    public static ReopenLegalDeadlineResult AccessDenied { get; } = new(
        ReopenLegalDeadlineResultStatus.AccessDenied);

    public static ReopenLegalDeadlineResult NotFound { get; } = new(
        ReopenLegalDeadlineResultStatus.NotFound);

    public static ReopenLegalDeadlineResult Succeeded { get; } = new(
        ReopenLegalDeadlineResultStatus.Succeeded);
}

public enum ReopenLegalDeadlineResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
