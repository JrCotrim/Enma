using System.Data.Common;
using Enma.Application.Finance;
using Enma.Domain.Clients;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
    public async Task GetOverviewAsync_OrganizationWithoutFinance_ReturnsZeros()
    {
        Organization tenant = CreateOrganization("overview-empty");
        await SeedAsync(tenant);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        FinanceOverviewReadModel overview =
            await new FinanceReadQueries(dbContext).GetOverviewAsync(
                tenant.Id,
                ReferenceDate);

        Assert.Equal(
            new FinanceOverviewReadModel(
                ReferenceDate,
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
                0L),
            overview);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetOverviewAsync_MixedPlans_ReturnsExactTenantScopedMetrics()
    {
        Organization tenant = CreateOrganization("overview");
        Organization foreignTenant = CreateOrganization("overview-foreign");
        Client client = CreateClient(tenant, "Overview Client");
        Client foreignClient = CreateClient(foreignTenant, "Foreign Overview Client");
        ClientPaymentPlan openPlan = CreatePlan(
            client,
            100.01m,
            4,
            new DateOnly(2026, 7, 7),
            CreatedAt);
        ClientPaymentPlan paidPlan = CreatePlan(
            client,
            20m,
            2,
            new DateOnly(2026, 11, 7),
            CreatedAt);
        ClientPaymentPlan foreignPlan = CreatePlan(
            foreignClient,
            9_999_999.99m,
            1,
            ReferenceDate.AddDays(-1),
            CreatedAt);

        openPlan.Installments.Single(item => item.SequenceNumber == 1)
            .MarkPaid(CreatedAt.AddDays(1));
        foreach (PaymentInstallment installment in paidPlan.Installments)
        {
            installment.MarkPaid(CreatedAt.AddDays(1));
        }

        await SeedAsync(
            tenant,
            foreignTenant,
            client,
            foreignClient,
            openPlan,
            paidPlan,
            foreignPlan);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        FinanceOverviewReadModel overview =
            await new FinanceReadQueries(dbContext).GetOverviewAsync(
                tenant.Id,
                ReferenceDate);

        Assert.Equal(ReferenceDate, overview.ReferenceDate);
        Assert.Equal(120.01m, overview.TotalContractedAmount);
        Assert.Equal(45.01m, overview.TotalReceivedAmount);
        Assert.Equal(75m, overview.TotalOutstandingAmount);
        Assert.Equal(25m, overview.OverdueAmount);
        Assert.Equal(25m, overview.DueTodayAmount);
        Assert.Equal(25m, overview.UpcomingAmount);
        Assert.Equal(2L, overview.PaymentPlanCount);
        Assert.Equal(1L, overview.OpenPaymentPlanCount);
        Assert.Equal(3L, overview.PaidInstallmentCount);
        Assert.Equal(1L, overview.OverdueInstallmentCount);
        Assert.Equal(1L, overview.DueTodayInstallmentCount);
        Assert.Equal(1L, overview.UpcomingInstallmentCount);
        Assert.Equal(
            overview.TotalContractedAmount,
            overview.TotalReceivedAmount + overview.TotalOutstandingAmount);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetOverviewAsync_UsesOneAggregateCommandWithoutClientJoin()
    {
        Organization tenant = CreateOrganization("overview-sql");
        Client client = CreateClient(tenant, "Overview SQL Client");
        ClientPaymentPlan plan = CreatePlan(
            client,
            10m,
            1,
            ReferenceDate,
            CreatedAt);
        await SeedAsync(tenant, client, plan);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);

        await new FinanceReadQueries(dbContext).GetOverviewAsync(
            tenant.Id,
            ReferenceDate);

        string sql = Assert.Single(interceptor.CommandTexts);
        Assert.Contains("client_payment_plans", sql, StringComparison.Ordinal);
        Assert.Contains("payment_installments", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM clients", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CROSS JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetClientSummaryAsync_ReturnsExactTenantAndClientScopedMetrics()
    {
        Organization tenant = CreateOrganization("client-summary");
        Organization foreignTenant = CreateOrganization("client-summary-foreign");
        Client client = CreateClient(tenant, "Summary Client");
        Client sameTenantPeer = CreateClient(tenant, "Summary Peer");
        Client foreignClient = CreateClient(foreignTenant, "Foreign Summary Client");
        ClientPaymentPlan openPlan = CreatePlan(
            client,
            100.01m,
            4,
            new DateOnly(2026, 7, 7),
            CreatedAt);
        ClientPaymentPlan paidPlan = CreatePlan(
            client,
            20m,
            2,
            new DateOnly(2026, 11, 7),
            CreatedAt);
        ClientPaymentPlan peerPlan = CreatePlan(
            sameTenantPeer,
            8_888.88m,
            1,
            ReferenceDate.AddDays(-1),
            CreatedAt);
        ClientPaymentPlan foreignPlan = CreatePlan(
            foreignClient,
            9_999_999.99m,
            1,
            ReferenceDate.AddDays(-1),
            CreatedAt);

        openPlan.Installments.Single(item => item.SequenceNumber == 1)
            .MarkPaid(CreatedAt.AddDays(1));
        foreach (PaymentInstallment installment in paidPlan.Installments)
        {
            installment.MarkPaid(CreatedAt.AddDays(1));
        }

        await SeedAsync(
            tenant,
            foreignTenant,
            client,
            sameTenantPeer,
            foreignClient,
            openPlan,
            paidPlan,
            peerPlan,
            foreignPlan);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        ClientFinanceSummaryReadModel? summary =
            await new FinanceReadQueries(dbContext).GetClientSummaryAsync(
                tenant.Id,
                client.Id,
                ReferenceDate);

        Assert.Equal(
            new ClientFinanceSummaryReadModel(
                client.Id,
                ReferenceDate,
                120.01m,
                45.01m,
                75m,
                25m,
                2L),
            summary);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetClientSummaryAsync_HandlesEmptyMissingAndForeignClients()
    {
        Organization tenant = CreateOrganization("client-summary-empty");
        Organization foreignTenant = CreateOrganization("client-summary-empty-foreign");
        Client emptyClient = CreateClient(tenant, "Empty Summary Client");
        Client foreignClient = CreateClient(foreignTenant, "Foreign Empty Client");
        ClientPaymentPlan foreignPlan = CreatePlan(
            foreignClient,
            999m,
            1,
            ReferenceDate.AddDays(-1),
            CreatedAt);
        await SeedAsync(
            tenant,
            foreignTenant,
            emptyClient,
            foreignClient,
            foreignPlan);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new FinanceReadQueries(dbContext);

        ClientFinanceSummaryReadModel? empty = await queries.GetClientSummaryAsync(
            tenant.Id,
            emptyClient.Id,
            ReferenceDate);
        ClientFinanceSummaryReadModel? missing = await queries.GetClientSummaryAsync(
            tenant.Id,
            Guid.NewGuid(),
            ReferenceDate);
        ClientFinanceSummaryReadModel? foreign = await queries.GetClientSummaryAsync(
            tenant.Id,
            foreignClient.Id,
            ReferenceDate);

        Assert.Equal(
            new ClientFinanceSummaryReadModel(
                emptyClient.Id,
                ReferenceDate,
                0m,
                0m,
                0m,
                0m,
                0L),
            empty);
        Assert.Null(missing);
        Assert.Null(foreign);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetClientSummaryAsync_UsesOneAggregateCommandWithoutMultiplication()
    {
        Organization tenant = CreateOrganization("client-summary-sql");
        Client client = CreateClient(tenant, "Summary SQL Client");
        ClientPaymentPlan plan = CreatePlan(
            client,
            1_000m,
            4,
            ReferenceDate.AddMonths(-2),
            CreatedAt);
        await SeedAsync(tenant, client, plan);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);

        ClientFinanceSummaryReadModel? summary =
            await new FinanceReadQueries(dbContext).GetClientSummaryAsync(
                tenant.Id,
                client.Id,
                ReferenceDate);

        Assert.NotNull(summary);
        Assert.Equal(1_000m, summary.TotalContractedAmount);
        Assert.Equal(1L, summary.PaymentPlanCount);
        string sql = Assert.Single(interceptor.CommandTexts);
        Assert.Contains("clients", sql, StringComparison.Ordinal);
        Assert.Contains("client_payment_plans", sql, StringComparison.Ordinal);
        Assert.Contains("payment_installments", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetClientSummaryAsync_PreservesLargeDecimalExactly()
    {
        const decimal amount = 9_999_999_999_999_999.99m;
        Organization tenant = CreateOrganization("client-summary-money");
        Client client = CreateClient(tenant, "Summary Money Client");
        ClientPaymentPlan plan = CreatePlan(
            client,
            amount,
            1,
            ReferenceDate,
            CreatedAt);
        await SeedAsync(tenant, client, plan);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        ClientFinanceSummaryReadModel? summary =
            await new FinanceReadQueries(dbContext).GetClientSummaryAsync(
                tenant.Id,
                client.Id,
                ReferenceDate);

        Assert.NotNull(summary);
        Assert.Equal(amount, summary.TotalContractedAmount);
        Assert.Equal(amount, summary.TotalOutstandingAmount);
        Assert.Equal(0m, summary.TotalReceivedAmount);
        Assert.Equal(0m, summary.OverdueAmount);
    }

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

    private EnmaDbContext CreateContext(DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        return new EnmaDbContext(options);
    }

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
