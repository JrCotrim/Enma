using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Finance;

public sealed record PaymentPlanResponse(
    Guid Id,
    Guid ClientId,
    string ClientName,
    decimal TotalAmount,
    int InstallmentCount,
    DateOnly FirstDueDate,
    DateTimeOffset CreatedAt,
    DateOnly ReferenceDate,
    IReadOnlyList<PaymentInstallmentResponse> Installments);

public sealed record PaymentInstallmentResponse(
    Guid Id,
    int SequenceNumber,
    decimal Amount,
    DateOnly DueDate,
    DateTimeOffset? PaidAt,
    PaymentInstallmentStatusResponse Status);

[JsonConverter(typeof(JsonStringEnumConverter<PaymentInstallmentStatusResponse>))]
public enum PaymentInstallmentStatusResponse
{
    Paid = 0,
    Overdue = 1,
    DueToday = 2,
    Upcoming = 3
}
