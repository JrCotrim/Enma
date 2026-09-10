using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Finance;

public sealed record ClientFinanceSummaryResponse(
    Guid ClientId,
    DateOnly ReferenceDate,
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal TotalContractedAmount,
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal TotalReceivedAmount,
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal TotalOutstandingAmount,
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal OverdueAmount,
    long PaymentPlanCount);
