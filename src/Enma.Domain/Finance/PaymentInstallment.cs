namespace Enma.Domain.Finance;

public sealed class PaymentInstallment
{
    private PaymentInstallment()
    {
    }

    internal PaymentInstallment(
        Guid organizationId,
        Guid paymentPlanId,
        int sequenceNumber,
        decimal amount,
        DateOnly dueDate,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                FinanceErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (paymentPlanId == Guid.Empty)
        {
            throw new ArgumentException(
                FinanceErrors.PaymentPlanIdRequired,
                nameof(paymentPlanId));
        }

        if (sequenceNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                FinanceErrors.SequenceNumberInvalid);
        }

        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                FinanceErrors.InstallmentAmountInvalid);
        }

        if (dueDate == DateOnly.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueDate),
                FinanceErrors.DueDateInvalid);
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                FinanceErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        PaymentPlanId = paymentPlanId;
        SequenceNumber = sequenceNumber;
        Amount = amount;
        DueDate = dueDate;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid PaymentPlanId { get; private set; }

    public int SequenceNumber { get; private set; }

    public decimal Amount { get; private set; }

    public DateOnly DueDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }
}