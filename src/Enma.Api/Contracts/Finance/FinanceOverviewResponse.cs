namespace Enma.Api.Contracts.Finance;

public sealed record FinanceOverviewResponse(
    DateOnly ReferenceDate,
    decimal TotalContractedAmount,
    decimal TotalReceivedAmount,
    decimal TotalOutstandingAmount,
    decimal OverdueAmount,
    decimal DueTodayAmount,
    decimal UpcomingAmount,
    long PaymentPlanCount,
    long OpenPaymentPlanCount,
    long PaidInstallmentCount,
    long OverdueInstallmentCount,
    long DueTodayInstallmentCount,
    long UpcomingInstallmentCount);
