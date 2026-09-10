namespace Enma.Application.Finance;

public sealed record ClientFinanceSummaryReadModel(
    Guid ClientId,
    DateOnly ReferenceDate,
    decimal TotalContractedAmount,
    decimal TotalReceivedAmount,
    decimal TotalOutstandingAmount,
    decimal OverdueAmount,
    long PaymentPlanCount);
