namespace Enma.Application.Finance.Create;

public sealed record CreatePaymentPlanCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid ClientId,
    decimal TotalAmount,
    int InstallmentCount,
    DateOnly FirstDueDate);
