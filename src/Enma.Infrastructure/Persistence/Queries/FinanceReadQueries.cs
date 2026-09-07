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

    public async Task<FinanceOverviewReadModel> GetOverviewAsync(
        Guid organizationId,
        DateOnly referenceDate,
        CancellationToken cancellationToken = default)
    {
        var installmentMetrics = _dbContext.PaymentInstallments
            .AsNoTracking()
            .Where(installment =>
                installment.OrganizationId == organizationId)
            .GroupBy(installment => new
            {
                installment.OrganizationId,
                installment.PaymentPlanId
            })
            .Select(group => new
            {
                group.Key.OrganizationId,
                group.Key.PaymentPlanId,
                ReceivedAmount = (decimal?)group.Sum(installment =>
                    installment.PaidAt != null ? installment.Amount : 0m),
                OverdueAmount = (decimal?)group.Sum(installment =>
                    installment.PaidAt == null &&
                    installment.DueDate < referenceDate
                        ? installment.Amount
                        : 0m),
                DueTodayAmount = (decimal?)group.Sum(installment =>
                    installment.PaidAt == null &&
                    installment.DueDate == referenceDate
                        ? installment.Amount
                        : 0m),
                UpcomingAmount = (decimal?)group.Sum(installment =>
                    installment.PaidAt == null &&
                    installment.DueDate > referenceDate
                        ? installment.Amount
                        : 0m),
                PaidInstallmentCount = (long?)group.LongCount(installment =>
                    installment.PaidAt != null),
                OverdueInstallmentCount = (long?)group.LongCount(installment =>
                    installment.PaidAt == null &&
                    installment.DueDate < referenceDate),
                DueTodayInstallmentCount = (long?)group.LongCount(installment =>
                    installment.PaidAt == null &&
                    installment.DueDate == referenceDate),
                UpcomingInstallmentCount = (long?)group.LongCount(installment =>
                    installment.PaidAt == null &&
                    installment.DueDate > referenceDate)
            });

        var planMetrics =
            from paymentPlan in _dbContext.ClientPaymentPlans.AsNoTracking()
            where paymentPlan.OrganizationId == organizationId
            join installmentMetric in installmentMetrics
                on new
                {
                    paymentPlan.OrganizationId,
                    PaymentPlanId = paymentPlan.Id
                }
                equals new
                {
                    installmentMetric.OrganizationId,
                    installmentMetric.PaymentPlanId
                }
                into installmentMetricMatches
            from installmentMetric in installmentMetricMatches.DefaultIfEmpty()
            select new
            {
                paymentPlan.TotalAmount,
                ReceivedAmount = installmentMetric.ReceivedAmount ?? 0m,
                OverdueAmount = installmentMetric.OverdueAmount ?? 0m,
                DueTodayAmount = installmentMetric.DueTodayAmount ?? 0m,
                UpcomingAmount = installmentMetric.UpcomingAmount ?? 0m,
                PaidInstallmentCount =
                    installmentMetric.PaidInstallmentCount ?? 0L,
                OverdueInstallmentCount =
                    installmentMetric.OverdueInstallmentCount ?? 0L,
                DueTodayInstallmentCount =
                    installmentMetric.DueTodayInstallmentCount ?? 0L,
                UpcomingInstallmentCount =
                    installmentMetric.UpcomingInstallmentCount ?? 0L
            };

        var overview = await planMetrics
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalContractedAmount = group.Sum(plan => plan.TotalAmount),
                TotalReceivedAmount = group.Sum(plan => plan.ReceivedAmount),
                OverdueAmount = group.Sum(plan => plan.OverdueAmount),
                DueTodayAmount = group.Sum(plan => plan.DueTodayAmount),
                UpcomingAmount = group.Sum(plan => plan.UpcomingAmount),
                PaymentPlanCount = group.LongCount(),
                OpenPaymentPlanCount = group.LongCount(plan =>
                    plan.OverdueInstallmentCount +
                    plan.DueTodayInstallmentCount +
                    plan.UpcomingInstallmentCount > 0),
                PaidInstallmentCount = group.Sum(plan =>
                    plan.PaidInstallmentCount),
                OverdueInstallmentCount = group.Sum(plan =>
                    plan.OverdueInstallmentCount),
                DueTodayInstallmentCount = group.Sum(plan =>
                    plan.DueTodayInstallmentCount),
                UpcomingInstallmentCount = group.Sum(plan =>
                    plan.UpcomingInstallmentCount)
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (overview is null)
        {
            return new FinanceOverviewReadModel(
                referenceDate,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0L,
                0L,
                0L,
                0L,
                0L,
                0L);
        }

        decimal totalOutstandingAmount =
            overview.OverdueAmount +
            overview.DueTodayAmount +
            overview.UpcomingAmount;

        return new FinanceOverviewReadModel(
            referenceDate,
            overview.TotalContractedAmount,
            overview.TotalReceivedAmount,
            totalOutstandingAmount,
            overview.OverdueAmount,
            overview.DueTodayAmount,
            overview.UpcomingAmount,
            overview.PaymentPlanCount,
            overview.OpenPaymentPlanCount,
            overview.PaidInstallmentCount,
            overview.OverdueInstallmentCount,
            overview.DueTodayInstallmentCount,
            overview.UpcomingInstallmentCount);
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
