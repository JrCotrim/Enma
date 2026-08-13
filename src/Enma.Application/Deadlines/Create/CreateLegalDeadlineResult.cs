namespace Enma.Application.Deadlines.Create;

public sealed class CreateLegalDeadlineResult
{
    private CreateLegalDeadlineResult(
        CreateLegalDeadlineResultStatus status,
        Guid? deadlineId)
    {
        Status = status;
        DeadlineId = deadlineId;
    }

    public CreateLegalDeadlineResultStatus Status { get; }

    public Guid? DeadlineId { get; }

    public static CreateLegalDeadlineResult AccessDenied { get; } = new(
        CreateLegalDeadlineResultStatus.AccessDenied,
        null);

    public static CreateLegalDeadlineResult RelatedProcessUnavailable { get; } = new(
        CreateLegalDeadlineResultStatus.RelatedProcessUnavailable,
        null);

    public static CreateLegalDeadlineResult Created(Guid deadlineId)
    {
        if (deadlineId == Guid.Empty)
        {
            throw new ArgumentException(
                "Deadline id cannot be empty.",
                nameof(deadlineId));
        }

        return new CreateLegalDeadlineResult(
            CreateLegalDeadlineResultStatus.Created,
            deadlineId);
    }
}

public enum CreateLegalDeadlineResultStatus
{
    AccessDenied = 0,
    RelatedProcessUnavailable = 1,
    Created = 2
}
