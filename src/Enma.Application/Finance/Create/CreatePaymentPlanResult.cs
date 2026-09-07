namespace Enma.Application.Finance.Create;

public sealed class CreatePaymentPlanResult
{
    private CreatePaymentPlanResult(
        CreatePaymentPlanResultStatus status,
        Guid? paymentPlanId)
    {
        Status = status;
        PaymentPlanId = paymentPlanId;
    }

    public CreatePaymentPlanResultStatus Status { get; }

    public Guid? PaymentPlanId { get; }

    public static CreatePaymentPlanResult AccessDenied { get; } = new(
        CreatePaymentPlanResultStatus.AccessDenied,
        null);

    public static CreatePaymentPlanResult RelatedClientUnavailable { get; } = new(
        CreatePaymentPlanResultStatus.RelatedClientUnavailable,
        null);

    public static CreatePaymentPlanResult Succeeded(Guid paymentPlanId)
    {
        if (paymentPlanId == Guid.Empty)
        {
            throw new ArgumentException(
                "Payment plan id cannot be empty.",
                nameof(paymentPlanId));
        }

        return new CreatePaymentPlanResult(
            CreatePaymentPlanResultStatus.Succeeded,
            paymentPlanId);
    }
}

public enum CreatePaymentPlanResultStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    Succeeded = 2
}
