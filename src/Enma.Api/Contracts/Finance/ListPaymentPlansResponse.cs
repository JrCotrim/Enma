using System.Text.Json.Serialization;

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
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal TotalAmount,
    int InstallmentCount,
    DateOnly FirstDueDate,
    DateTimeOffset CreatedAt,
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal OutstandingAmount,
    int OverdueInstallmentCount,
    DateOnly? NextDueDate);
