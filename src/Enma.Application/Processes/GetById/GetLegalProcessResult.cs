namespace Enma.Application.Processes.GetById;

public sealed class GetLegalProcessResult
{
    private GetLegalProcessResult(
        GetLegalProcessResultStatus status,
        LegalProcessReadModel? legalProcess)
    {
        Status = status;
        LegalProcess = legalProcess;
    }

    public GetLegalProcessResultStatus Status { get; }

    public LegalProcessReadModel? LegalProcess { get; }

    public static GetLegalProcessResult AccessDenied { get; } = new(
        GetLegalProcessResultStatus.AccessDenied,
        null);

    public static GetLegalProcessResult NotFound { get; } = new(
        GetLegalProcessResultStatus.NotFound,
        null);

    public static GetLegalProcessResult Success(
        LegalProcessReadModel legalProcess)
    {
        ArgumentNullException.ThrowIfNull(legalProcess);

        return new GetLegalProcessResult(
            GetLegalProcessResultStatus.Succeeded,
            legalProcess);
    }
}

public enum GetLegalProcessResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
