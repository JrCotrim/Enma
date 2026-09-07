namespace Enma.Api.Contracts.Finance;

public sealed record ListPaymentPlansResponse(
    IReadOnlyList<PaymentPlanSummaryResponse> Items,
    int PageNumber,
    int PageSize,
    bool HasNext);

public sealed record PaymentPlanSummaryResponse(
    Guid Id,
    Guid ClientId,
    string ClientName,
    decimal TotalAmount,
    int InstallmentCount,
    DateOnly FirstDueDate,
    DateTimeOffset CreatedAt,
    decimal OutstandingAmount,
    int OverdueInstallmentCount,
    DateOnly? NextDueDate);
