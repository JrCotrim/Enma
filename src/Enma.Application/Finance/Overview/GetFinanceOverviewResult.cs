namespace Enma.Application.Finance.Overview;

public sealed class GetFinanceOverviewResult
{
    private GetFinanceOverviewResult(
        GetFinanceOverviewResultStatus status,
        FinanceOverviewReadModel? overview)
    {
        Status = status;
        Overview = overview;
    }

    public GetFinanceOverviewResultStatus Status { get; }

    public FinanceOverviewReadModel? Overview { get; }

    public static GetFinanceOverviewResult AccessDenied { get; } = new(
        GetFinanceOverviewResultStatus.AccessDenied,
        null);

    public static GetFinanceOverviewResult Success(
        FinanceOverviewReadModel overview)
    {
        ArgumentNullException.ThrowIfNull(overview);

        return new GetFinanceOverviewResult(
            GetFinanceOverviewResultStatus.Succeeded,
            overview);
    }
}

public enum GetFinanceOverviewResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
