using Enma.Application.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class DashboardReadQueries : IDashboardReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public DashboardReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<DashboardMetricsReadModel> ReadMetricsAsync(
        DashboardMetricsReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _dbContext.Organizations
            .AsNoTracking()
            .Where(organization =>
                organization.Id == request.OrganizationId)
            .Select(_ => new DashboardMetricsReadModel(
                _dbContext.Clients
                    .AsNoTracking()
                    .Count(client =>
                        client.OrganizationId == request.OrganizationId &&
                        client.IsActive),
                _dbContext.LegalProcesses
                    .AsNoTracking()
                    .Count(legalProcess =>
                        legalProcess.OrganizationId == request.OrganizationId),
                _dbContext.LegalDeadlines
                    .AsNoTracking()
                    .Count(deadline =>
                        deadline.OrganizationId == request.OrganizationId &&
                        deadline.CompletedAt == null),
                _dbContext.LegalTasks
                    .AsNoTracking()
                    .Count(legalTask =>
                        legalTask.OrganizationId == request.OrganizationId &&
                        legalTask.CompletedAt == null),
                _dbContext.LegalDeadlines
                    .AsNoTracking()
                    .Count(deadline =>
                        deadline.OrganizationId == request.OrganizationId &&
                        deadline.CompletedAt == null &&
                        deadline.DueDate < request.ReferenceDate),
                _dbContext.LegalDeadlines
                    .AsNoTracking()
                    .Count(deadline =>
                        deadline.OrganizationId == request.OrganizationId &&
                        deadline.CompletedAt == null &&
                        deadline.DueDate == request.ReferenceDate),
                _dbContext.LegalDeadlines
                    .AsNoTracking()
                    .Count(deadline =>
                        deadline.OrganizationId == request.OrganizationId &&
                        deadline.CompletedAt == null &&
                        deadline.DueDate > request.ReferenceDate &&
                        deadline.DueDate <= request.ThroughDate),
                _dbContext.LegalTasks
                    .AsNoTracking()
                    .Count(legalTask =>
                        legalTask.OrganizationId == request.OrganizationId &&
                        legalTask.CompletedAt == null &&
                        legalTask.DueDate != null &&
                        legalTask.DueDate < request.ReferenceDate),
                _dbContext.LegalTasks
                    .AsNoTracking()
                    .Count(legalTask =>
                        legalTask.OrganizationId == request.OrganizationId &&
                        legalTask.CompletedAt == null &&
                        legalTask.DueDate != null &&
                        legalTask.DueDate == request.ReferenceDate),
                _dbContext.LegalTasks
                    .AsNoTracking()
                    .Count(legalTask =>
                        legalTask.OrganizationId == request.OrganizationId &&
                        legalTask.CompletedAt == null &&
                        legalTask.DueDate != null &&
                        legalTask.DueDate > request.ReferenceDate &&
                        legalTask.DueDate <= request.ThroughDate)))
            .SingleAsync(cancellationToken);
    }
}
