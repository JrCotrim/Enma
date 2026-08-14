namespace Enma.Application.Tasks.GetById;

public sealed class GetLegalTaskResult
{
    private GetLegalTaskResult(
        GetLegalTaskResultStatus status,
        LegalTaskDetailReadModel? legalTask)
    {
        Status = status;
        LegalTask = legalTask;
    }

    public GetLegalTaskResultStatus Status { get; }

    public LegalTaskDetailReadModel? LegalTask { get; }

    public static GetLegalTaskResult AccessDenied { get; } = new(
        GetLegalTaskResultStatus.AccessDenied,
        null);

    public static GetLegalTaskResult NotFound { get; } = new(
        GetLegalTaskResultStatus.NotFound,
        null);

    public static GetLegalTaskResult InvalidInput { get; } = new(
        GetLegalTaskResultStatus.InvalidInput,
        null);

    public static GetLegalTaskResult Succeeded(LegalTaskDetailReadModel legalTask)
    {
        ArgumentNullException.ThrowIfNull(legalTask);

        return new GetLegalTaskResult(
            GetLegalTaskResultStatus.Succeeded,
            legalTask);
    }
}

public enum GetLegalTaskResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    InvalidInput = 2,
    Succeeded = 3
}
