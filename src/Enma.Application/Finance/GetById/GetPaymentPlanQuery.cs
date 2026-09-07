namespace Enma.Application.Finance.GetById;

public sealed record GetPaymentPlanQuery(
    Guid UserId,
    Guid OrganizationId,
    Guid PaymentPlanId);
