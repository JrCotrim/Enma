using Enma.Application.Finance;
using Enma.Domain.Clients;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class FinanceReadQueriesTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateOnly ReferenceDate = new(2026, 9, 7);
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListAsync_IsTenantScopedFilteredOrderedPagedAndComputesMetrics()
    {
        Organization tenant = CreateOrganization("tenant");
        Organization foreignTenant = CreateOrganization("foreign");
        Client client = CreateClient(tenant, "Authoritative Client");
        Client secondClient = CreateClient(tenant, "Second Client");
        Client foreignClient = CreateClient(foreignTenant, "Foreign Client");
        ClientPaymentPlan older = CreatePlan(
            client, 40m, 4, new DateOnly(2026, 7, 7), CreatedAt);
        ClientPaymentPlan sameTimeLowerOrHigher = CreatePlan(
            client, 20m, 2, new DateOnly(2026, 8, 7), CreatedAt.AddDays(1));
        ClientPaymentPlan sameTimePeer = CreatePlan(
            secondClient, 30m, 3, new DateOnly(2026, 8, 7), CreatedAt.AddDays(1));
        ClientPaymentPlan foreign = CreatePlan(
            foreignClient, 999m, 1, ReferenceDate, CreatedAt.AddYears(1));
        older.Installments.Single(item => item.SequenceNumber == 2)
            .MarkPaid(CreatedAt.AddDays(40));
        await SeedAsync(
            tenant, foreignTenant, client, secondClient, foreignClient,
            older, sameTimeLowerOrHigher, sameTimePeer, foreign);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new FinanceReadQueries(dbContext);

        IReadOnlyList<PaymentPlanListItemReadModel> firstPage =
            await queries.ListAsync(tenant.Id, null, ReferenceDate, 1, 1);
        IReadOnlyList<PaymentPlanListItemReadModel> secondPage =
            await queries.ListAsync(tenant.Id, null, ReferenceDate, 2, 1);
        IReadOnlyList<PaymentPlanListItemReadModel> filtered =
            await queries.ListAsync(tenant.Id, client.Id, ReferenceDate, 1, 100);
        IReadOnlyList<PaymentPlanListItemReadModel> foreignFilter =
            await queries.ListAsync(tenant.Id, foreignClient.Id, ReferenceDate, 1, 20);
        IReadOnlyList<PaymentPlanListItemReadModel> missingFilter =
            await queries.ListAsync(tenant.Id, Guid.NewGuid(), ReferenceDate, 1, 20);

        Assert.Equal(2, firstPage.Count);
        ClientPaymentPlan[] expectedPeers = [sameTimeLowerOrHigher, sameTimePeer];
        Guid[] expectedOrder = expectedPeers
            .OrderByDescending(plan => plan.Id)
            .Select(plan => plan.Id)
            .ToArray();
        Assert.Equal(expectedOrder[0], firstPage[0].Id);
        Assert.Equal(expectedOrder[1], secondPage[0].Id);
        Assert.Equal([sameTimeLowerOrHigher.Id, older.Id],
            filtered.Select(item => item.Id));
        Assert.All(filtered, item => Assert.Equal("Authoritative Client", item.ClientName));
        Assert.Empty(foreignFilter);
        Assert.Empty(missingFilter);
        Assert.DoesNotContain(firstPage, item => item.Id == foreign.Id);

        PaymentPlanListItemReadModel olderItem = Assert.Single(
            filtered, item => item.Id == older.Id);
        Assert.Equal(30m, olderItem.OutstandingAmount);
        Assert.Equal(1, olderItem.OverdueInstallmentCount);
        Assert.Equal(new DateOnly(2026, 7, 7), olderItem.NextDueDate);
    }

    [Fact]
    public async Task ListAsync_FullyPaidPlanReturnsZeroMetrics()
    {
        Organization tenant = CreateOrganization("paid");
        Client client = CreateClient(tenant, "Paid Client");
        ClientPaymentPlan plan = CreatePlan(
            client, 20m, 2, new DateOnly(2026, 7, 7), CreatedAt);
        foreach (PaymentInstallment installment in plan.Installments)
        {
            installment.MarkPaid(CreatedAt.AddDays(1));
        }
        await SeedAsync(tenant, client, plan);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PaymentPlanListItemReadModel item = Assert.Single(
            await new FinanceReadQueries(dbContext).ListAsync(
                tenant.Id, null, ReferenceDate, 1, 100));

        Assert.Equal(0m, item.OutstandingAmount);
        Assert.Equal(0, item.OverdueInstallmentCount);
        Assert.Null(item.NextDueDate);
    }

    [Fact]
    public async Task FindAsync_IsTenantScopedAndReturnsOrderedDerivedStatuses()
    {
        Organization tenant = CreateOrganization("detail");
        Organization foreignTenant = CreateOrganization("detail-foreign");
        Client client = CreateClient(tenant, "Detail Client");
        Client foreignClient = CreateClient(foreignTenant, "Foreign Detail Client");
        ClientPaymentPlan plan = CreatePlan(
            client, 40m, 4, new DateOnly(2026, 7, 7), CreatedAt);
        ClientPaymentPlan foreignPlan = CreatePlan(
            foreignClient, 10m, 1, ReferenceDate, CreatedAt);
        DateTimeOffset paidAt = CreatedAt.AddDays(1);
        plan.Installments.Single(item => item.SequenceNumber == 2).MarkPaid(paidAt);
        await SeedAsync(tenant, foreignTenant, client, foreignClient, plan, foreignPlan);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new FinanceReadQueries(dbContext);

        PaymentPlanDetailReadModel? detail = await queries.FindAsync(
            tenant.Id, plan.Id, ReferenceDate);
        PaymentPlanDetailReadModel? foreign = await queries.FindAsync(
            tenant.Id, foreignPlan.Id, ReferenceDate);
        PaymentPlanDetailReadModel? missing = await queries.FindAsync(
            tenant.Id, Guid.NewGuid(), ReferenceDate);

        Assert.NotNull(detail);
        Assert.Equal("Detail Client", detail.ClientName);
        Assert.Equal(ReferenceDate, detail.ReferenceDate);
        Assert.Equal([1, 2, 3, 4], detail.Installments.Select(item => item.SequenceNumber));
        Assert.Equal(
            [
                PaymentInstallmentStatus.Overdue,
                PaymentInstallmentStatus.Paid,
                PaymentInstallmentStatus.DueToday,
                PaymentInstallmentStatus.Upcoming
            ],
            detail.Installments.Select(item => item.Status));
        Assert.Equal(paidAt, detail.Installments[1].PaidAt);
        Assert.Null(detail.Installments[0].PaidAt);
        Assert.Null(foreign);
        Assert.Null(missing);
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    private static Organization CreateOrganization(string marker) => new(
        $"Finance {marker}", $"finance-{marker}-{Guid.NewGuid():N}", CreatedAt.AddDays(-2));

    private static Client CreateClient(Organization organization, string name) =>
        new(organization.Id, name, CreatedAt.AddDays(-1));

    private static ClientPaymentPlan CreatePlan(
        Client client,
        decimal amount,
        int installments,
        DateOnly firstDueDate,
        DateTimeOffset createdAt) =>
        new(client.OrganizationId, client.Id, amount, installments, firstDueDate, createdAt);

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }
}
