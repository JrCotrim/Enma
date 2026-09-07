namespace Enma.Application.Finance;

public sealed record PaymentPlanListItemReadModel(
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

public sealed record PaymentPlanDetailReadModel(
    Guid Id,
    Guid ClientId,
    string ClientName,
    decimal TotalAmount,
    int InstallmentCount,
    DateOnly FirstDueDate,
    DateTimeOffset CreatedAt,
    DateOnly ReferenceDate,
    IReadOnlyList<PaymentInstallmentReadModel> Installments);

public sealed record PaymentInstallmentReadModel(
    Guid Id,
    int SequenceNumber,
    decimal Amount,
    DateOnly DueDate,
    DateTimeOffset? PaidAt,
    PaymentInstallmentStatus Status);

public enum PaymentInstallmentStatus
{
    Paid = 0,
    Overdue = 1,
    DueToday = 2,
    Upcoming = 3
}

public static class PaymentInstallmentStatusClassifier
{
    public static PaymentInstallmentStatus Classify(
        DateOnly dueDate,
        DateTimeOffset? paidAt,
        DateOnly referenceDate)
    {
        if (paidAt is not null)
        {
            return PaymentInstallmentStatus.Paid;
        }

        return dueDate.CompareTo(referenceDate) switch
        {
            < 0 => PaymentInstallmentStatus.Overdue,
            0 => PaymentInstallmentStatus.DueToday,
            _ => PaymentInstallmentStatus.Upcoming
        };
    }
}
