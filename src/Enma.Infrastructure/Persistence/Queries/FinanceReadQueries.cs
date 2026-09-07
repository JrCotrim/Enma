using Enma.Application.Finance;
using Enma.Domain.Finance;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class FinanceReadQueries : IFinanceReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public FinanceReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<PaymentPlanListItemReadModel>> ListAsync(
        Guid organizationId,
        Guid? clientId,
        DateOnly referenceDate,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        int skippedItems = checked((pageNumber - 1) * pageSize);

        IQueryable<ClientPaymentPlan> paymentPlans =
            _dbContext.ClientPaymentPlans
                .AsNoTracking()
                .Where(paymentPlan =>
                    paymentPlan.OrganizationId == organizationId);

        if (clientId is Guid filteredClientId)
        {
            paymentPlans = paymentPlans.Where(paymentPlan =>
                paymentPlan.ClientId == filteredClientId);
        }

        return await (
            from paymentPlan in paymentPlans
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    paymentPlan.OrganizationId,
                    Id = paymentPlan.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    client.Id
                }
            orderby paymentPlan.CreatedAt descending, paymentPlan.Id descending
            select new PaymentPlanListItemReadModel(
                paymentPlan.Id,
                paymentPlan.ClientId,
                client.Name,
                paymentPlan.TotalAmount,
                paymentPlan.InstallmentCount,
                paymentPlan.FirstDueDate,
                paymentPlan.CreatedAt,
                _dbContext.PaymentInstallments
                    .Where(installment =>
                        installment.OrganizationId == organizationId &&
                        installment.PaymentPlanId == paymentPlan.Id &&
                        installment.PaidAt == null)
                    .Sum(installment => (decimal?)installment.Amount) ?? 0m,
                _dbContext.PaymentInstallments
                    .Count(installment =>
                        installment.OrganizationId == organizationId &&
                        installment.PaymentPlanId == paymentPlan.Id &&
                        installment.PaidAt == null &&
                        installment.DueDate < referenceDate),
                _dbContext.PaymentInstallments
                    .Where(installment =>
                        installment.OrganizationId == organizationId &&
                        installment.PaymentPlanId == paymentPlan.Id &&
                        installment.PaidAt == null)
                    .Min(installment => (DateOnly?)installment.DueDate)))
            .Skip(skippedItems)
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<PaymentPlanDetailReadModel?> FindAsync(
        Guid organizationId,
        Guid paymentPlanId,
        DateOnly referenceDate,
        CancellationToken cancellationToken = default)
    {
        var header = await (
            from paymentPlan in _dbContext.ClientPaymentPlans.AsNoTracking()
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    paymentPlan.OrganizationId,
                    Id = paymentPlan.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    client.Id
                }
            where paymentPlan.OrganizationId == organizationId &&
                paymentPlan.Id == paymentPlanId
            select new
            {
                paymentPlan.Id,
                paymentPlan.ClientId,
                ClientName = client.Name,
                paymentPlan.TotalAmount,
                paymentPlan.InstallmentCount,
                paymentPlan.FirstDueDate,
                paymentPlan.CreatedAt
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
        {
            return null;
        }

        var installmentRows = await _dbContext.PaymentInstallments
            .AsNoTracking()
            .Where(installment =>
                installment.OrganizationId == organizationId &&
                installment.PaymentPlanId == paymentPlanId)
            .OrderBy(installment => installment.SequenceNumber)
            .Select(installment => new
            {
                installment.Id,
                installment.SequenceNumber,
                installment.Amount,
                installment.DueDate,
                installment.PaidAt
            })
            .ToArrayAsync(cancellationToken);

        PaymentInstallmentReadModel[] installments = installmentRows
            .Select(installment => new PaymentInstallmentReadModel(
                installment.Id,
                installment.SequenceNumber,
                installment.Amount,
                installment.DueDate,
                installment.PaidAt,
                PaymentInstallmentStatusClassifier.Classify(
                    installment.DueDate,
                    installment.PaidAt,
                    referenceDate)))
            .ToArray();

        return new PaymentPlanDetailReadModel(
            header.Id,
            header.ClientId,
            header.ClientName,
            header.TotalAmount,
            header.InstallmentCount,
            header.FirstDueDate,
            header.CreatedAt,
            referenceDate,
            installments);
    }
}
