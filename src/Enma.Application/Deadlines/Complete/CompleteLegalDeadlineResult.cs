namespace Enma.Application.Deadlines.Complete;

public sealed class CompleteLegalDeadlineResult
{
    private CompleteLegalDeadlineResult(CompleteLegalDeadlineResultStatus status)
    {
        Status = status;
    }

    public CompleteLegalDeadlineResultStatus Status { get; }

    public static CompleteLegalDeadlineResult AccessDenied { get; } = new(
        CompleteLegalDeadlineResultStatus.AccessDenied);

    public static CompleteLegalDeadlineResult NotFound { get; } = new(
        CompleteLegalDeadlineResultStatus.NotFound);

    public static CompleteLegalDeadlineResult Succeeded { get; } = new(
        CompleteLegalDeadlineResultStatus.Succeeded);
}

public enum CompleteLegalDeadlineResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
