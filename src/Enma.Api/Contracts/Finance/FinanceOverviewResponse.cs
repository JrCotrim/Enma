using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Finance;

public sealed record FinanceOverviewResponse(
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
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal DueTodayAmount,
    [property: JsonNumberHandling(
        JsonNumberHandling.WriteAsString |
        JsonNumberHandling.AllowReadingFromString)]
    decimal UpcomingAmount,
    long PaymentPlanCount,
    long OpenPaymentPlanCount,
    long PaidInstallmentCount,
    long OverdueInstallmentCount,
    long DueTodayInstallmentCount,
    long UpcomingInstallmentCount);
