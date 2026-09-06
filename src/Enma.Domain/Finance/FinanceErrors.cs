namespace Enma.Domain.Finance;

public static class FinanceErrors
{
    public const string OrganizationIdRequired =
        "Organization id is required.";

    public const string ClientIdRequired =
        "Client id is required.";

    public const string PaymentPlanIdRequired =
        "Payment plan id is required.";

    public const string TotalAmountInvalid =
        "Total amount must be greater than zero and fit the supported monetary range.";

    public const string TotalAmountScaleInvalid =
        "Total amount cannot have more than two decimal places.";

    public const string InstallmentCountInvalid =
        "Installment count must be between 1 and 120.";

    public const string InstallmentCountExceedsAmount =
        "Installment count cannot exceed the total amount in cents.";

    public const string FirstDueDateInvalid =
        "First due date is invalid.";

    public const string CreatedAtInvalid =
        "Created at is invalid.";

    public const string ScheduleOutOfRange =
        "The installment schedule exceeds the supported date range.";

    public const string SequenceNumberInvalid =
        "Installment sequence number must be positive.";

    public const string InstallmentAmountInvalid =
        "Installment amount must be greater than zero.";

    public const string DueDateInvalid =
        "Installment due date is invalid.";
}