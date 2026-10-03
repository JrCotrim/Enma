namespace Enma.Application.Finance.ReversePayment;

public sealed record ReverseInstallmentPaymentCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid PaymentPlanId,
    Guid InstallmentId,
    string? Reason);
