namespace Enma.Application.Processes.Create;

public sealed class CreateLegalProcessResult
{
    private CreateLegalProcessResult(
        CreateLegalProcessResultStatus status,
        Guid? processId)
    {
        Status = status;
        ProcessId = processId;
    }

    public CreateLegalProcessResultStatus Status { get; }

    public Guid? ProcessId { get; }

    public static CreateLegalProcessResult AccessDenied { get; } = new(
        CreateLegalProcessResultStatus.AccessDenied,
        null);

    public static CreateLegalProcessResult RelatedClientUnavailable { get; } = new(
        CreateLegalProcessResultStatus.RelatedClientUnavailable,
        null);

    public static CreateLegalProcessResult Success(Guid processId)
    {
        if (processId == Guid.Empty)
        {
            throw new ArgumentException(
                "Process id cannot be empty.",
                nameof(processId));
        }

        return new CreateLegalProcessResult(
            CreateLegalProcessResultStatus.Succeeded,
            processId);
    }
}

public enum CreateLegalProcessResultStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    Succeeded = 2
}
