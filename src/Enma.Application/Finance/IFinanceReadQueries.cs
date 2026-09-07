namespace Enma.Application.Finance;

public interface IFinanceReadQueries
{
    Task<IReadOnlyList<PaymentPlanListItemReadModel>> ListAsync(
        Guid organizationId,
        Guid? clientId,
        DateOnly referenceDate,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<PaymentPlanDetailReadModel?> FindAsync(
        Guid organizationId,
        Guid paymentPlanId,
        DateOnly referenceDate,
        CancellationToken cancellationToken = default);
}
