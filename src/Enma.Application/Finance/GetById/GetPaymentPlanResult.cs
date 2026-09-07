namespace Enma.Application.Finance.GetById;

public sealed class GetPaymentPlanResult
{
    private GetPaymentPlanResult(
        GetPaymentPlanResultStatus status,
        PaymentPlanDetailReadModel? paymentPlan)
    {
        Status = status;
        PaymentPlan = paymentPlan;
    }

    public GetPaymentPlanResultStatus Status { get; }
    public PaymentPlanDetailReadModel? PaymentPlan { get; }

    public static GetPaymentPlanResult AccessDenied { get; } = new(
        GetPaymentPlanResultStatus.AccessDenied,
        null);

    public static GetPaymentPlanResult NotFound { get; } = new(
        GetPaymentPlanResultStatus.NotFound,
        null);

    public static GetPaymentPlanResult Success(
        PaymentPlanDetailReadModel paymentPlan)
    {
        ArgumentNullException.ThrowIfNull(paymentPlan);
        return new GetPaymentPlanResult(
            GetPaymentPlanResultStatus.Succeeded,
            paymentPlan);
    }
}

public enum GetPaymentPlanResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
