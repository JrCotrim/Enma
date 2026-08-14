namespace Enma.Application.Tasks.Create;

public sealed class CreateLegalTaskResult
{
    private CreateLegalTaskResult(
        CreateLegalTaskResultStatus status,
        Guid? legalTaskId)
    {
        Status = status;
        LegalTaskId = legalTaskId;
    }

    public CreateLegalTaskResultStatus Status { get; }

    public Guid? LegalTaskId { get; }

    public static CreateLegalTaskResult AccessDenied { get; } = new(
        CreateLegalTaskResultStatus.AccessDenied,
        null);

    public static CreateLegalTaskResult RelatedProcessUnavailable { get; } = new(
        CreateLegalTaskResultStatus.RelatedProcessUnavailable,
        null);

    public static CreateLegalTaskResult RelatedAssigneeUnavailable { get; } = new(
        CreateLegalTaskResultStatus.RelatedAssigneeUnavailable,
        null);

    public static CreateLegalTaskResult InvalidInput { get; } = new(
        CreateLegalTaskResultStatus.InvalidInput,
        null);

    public static CreateLegalTaskResult Succeeded(Guid legalTaskId)
    {
        if (legalTaskId == Guid.Empty)
        {
            throw new ArgumentException(
                "Legal task id cannot be empty.",
                nameof(legalTaskId));
        }

        return new CreateLegalTaskResult(
            CreateLegalTaskResultStatus.Succeeded,
            legalTaskId);
    }
}

public enum CreateLegalTaskResultStatus
{
    AccessDenied = 0,
    RelatedProcessUnavailable = 1,
    RelatedAssigneeUnavailable = 2,
    InvalidInput = 3,
    Succeeded = 4
}
