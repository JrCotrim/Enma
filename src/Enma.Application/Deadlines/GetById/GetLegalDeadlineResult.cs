namespace Enma.Application.Deadlines.GetById;

public sealed class GetLegalDeadlineResult
{
    private GetLegalDeadlineResult(
        GetLegalDeadlineResultStatus status,
        LegalDeadlineDetailReadModel? legalDeadline)
    {
        Status = status;
        LegalDeadline = legalDeadline;
    }

    public GetLegalDeadlineResultStatus Status { get; }

    public LegalDeadlineDetailReadModel? LegalDeadline { get; }

    public static GetLegalDeadlineResult AccessDenied { get; } = new(
        GetLegalDeadlineResultStatus.AccessDenied,
        null);

    public static GetLegalDeadlineResult NotFound { get; } = new(
        GetLegalDeadlineResultStatus.NotFound,
        null);

    public static GetLegalDeadlineResult Success(
        LegalDeadlineDetailReadModel legalDeadline)
    {
        ArgumentNullException.ThrowIfNull(legalDeadline);

        return new GetLegalDeadlineResult(
            GetLegalDeadlineResultStatus.Succeeded,
            legalDeadline);
    }
}

public enum GetLegalDeadlineResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
