namespace Enma.Application.Finance.MarkPaid;

public sealed record MarkPaymentInstallmentPaidCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid PaymentPlanId,
    Guid InstallmentId);