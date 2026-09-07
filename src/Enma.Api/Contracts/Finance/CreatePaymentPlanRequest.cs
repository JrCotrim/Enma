namespace Enma.Api.Contracts.Finance;

public sealed class CreatePaymentPlanRequest
{
    public required Guid ClientId { get; init; }

    public required decimal TotalAmount { get; init; }

    public required int InstallmentCount { get; init; }

    public required DateOnly FirstDueDate { get; init; }
}
