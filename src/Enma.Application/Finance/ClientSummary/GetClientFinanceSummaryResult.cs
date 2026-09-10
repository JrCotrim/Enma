namespace Enma.Application.Finance.ClientSummary;

public sealed class GetClientFinanceSummaryResult
{
    private GetClientFinanceSummaryResult(
        GetClientFinanceSummaryResultStatus status,
        ClientFinanceSummaryReadModel? summary)
    {
        Status = status;
        Summary = summary;
    }

    public GetClientFinanceSummaryResultStatus Status { get; }
    public ClientFinanceSummaryReadModel? Summary { get; }

    public static GetClientFinanceSummaryResult AccessDenied { get; } = new(
        GetClientFinanceSummaryResultStatus.AccessDenied,
        null);

    public static GetClientFinanceSummaryResult NotFound { get; } = new(
        GetClientFinanceSummaryResultStatus.NotFound,
        null);

    public static GetClientFinanceSummaryResult Success(
        ClientFinanceSummaryReadModel summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new GetClientFinanceSummaryResult(
            GetClientFinanceSummaryResultStatus.Succeeded,
            summary);
    }
}

public enum GetClientFinanceSummaryResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
