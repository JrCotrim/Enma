namespace Enma.Domain.Finance;

public sealed class ClientPaymentPlan
{
    private const int MaximumInstallmentCount = 120;
    private const decimal MaximumTotalAmount = 9_999_999_999_999_999.99m;

    private readonly List<PaymentInstallment> _installments = [];

    private ClientPaymentPlan()
    {
    }

    public ClientPaymentPlan(
        Guid organizationId,
        Guid clientId,
        decimal totalAmount,
        int installmentCount,
        DateOnly firstDueDate,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                FinanceErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException(
                FinanceErrors.ClientIdRequired,
                nameof(clientId));
        }

        if (installmentCount is < 1 or > MaximumInstallmentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(installmentCount),
                FinanceErrors.InstallmentCountInvalid);
        }

        if (firstDueDate == DateOnly.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstDueDate),
                FinanceErrors.FirstDueDateInvalid);
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                FinanceErrors.CreatedAtInvalid);
        }

        long totalCents = ConvertToCents(totalAmount);

        if (installmentCount > totalCents)
        {
            throw new ArgumentOutOfRangeException(
                nameof(installmentCount),
                FinanceErrors.InstallmentCountExceedsAmount);
        }

        Guid id = Guid.NewGuid();
        PaymentInstallment[] installments = CreateInstallments(
            organizationId,
            id,
            totalCents,
            installmentCount,
            firstDueDate,
            createdAt);

        Id = id;
        OrganizationId = organizationId;
        ClientId = clientId;
        TotalAmount = totalAmount;
        InstallmentCount = installmentCount;
        FirstDueDate = firstDueDate;
        CreatedAt = createdAt;

        _installments.AddRange(installments);
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ClientId { get; private set; }

    public decimal TotalAmount { get; private set; }

    public int InstallmentCount { get; private set; }

    public DateOnly FirstDueDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<PaymentInstallment> Installments => _installments;

    private static long ConvertToCents(decimal amount)
    {
        if (amount <= 0 || amount > MaximumTotalAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                FinanceErrors.TotalAmountInvalid);
        }

        decimal cents = amount * 100m;

        if (decimal.Truncate(cents) != cents)
        {
            throw new ArgumentException(
                FinanceErrors.TotalAmountScaleInvalid,
                nameof(amount));
        }

        return decimal.ToInt64(cents);
    }

    private static PaymentInstallment[] CreateInstallments(
        Guid organizationId,
        Guid paymentPlanId,
        long totalCents,
        int installmentCount,
        DateOnly firstDueDate,
        DateTimeOffset createdAt)
    {
        long baseAmountInCents = totalCents / installmentCount;
        long remainderInCents = totalCents % installmentCount;

        var installments = new PaymentInstallment[installmentCount];

        for (int index = 0; index < installmentCount; index++)
        {
            long installmentAmountInCents =
                baseAmountInCents +
                (index < remainderInCents ? 1 : 0);

            DateOnly dueDate = GetMonthlyDueDate(
                firstDueDate,
                index);

            installments[index] = new PaymentInstallment(
                organizationId,
                paymentPlanId,
                index + 1,
                installmentAmountInCents / 100m,
                dueDate,
                createdAt);
        }

        return installments;
    }

    private static DateOnly GetMonthlyDueDate(
        DateOnly firstDueDate,
        int monthOffset)
    {
        try
        {
            DateOnly targetMonth = new DateOnly(
                    firstDueDate.Year,
                    firstDueDate.Month,
                    1)
                .AddMonths(monthOffset);

            int dueDay = Math.Min(
                firstDueDate.Day,
                DateTime.DaysInMonth(
                    targetMonth.Year,
                    targetMonth.Month));

            return new DateOnly(
                targetMonth.Year,
                targetMonth.Month,
                dueDay);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstDueDate),
                firstDueDate,
                FinanceErrors.ScheduleOutOfRange);
        }
    }
}