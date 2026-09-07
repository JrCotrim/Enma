using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.Create;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class PaymentPlanCreationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset OperationTime = new(
        2026, 9, 7, 19, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_ValidCreation_PersistsScheduleAndAudit(
        OrganizationRole role)
    {
        ActorGraph graph = await SeedActorGraphAsync(role);
        await using EnmaDbContext authorizationContext =
            fixture.CreateDbContext();
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            authorizationContext);

        CreatePaymentPlanResult result = await useCase.ExecuteAsync(
            new CreatePaymentPlanCommand(
                graph.UserId,
                graph.OrganizationId,
                graph.ClientId,
                100.01m,
                3,
                new DateOnly(2027, 1, 31)));

        Guid paymentPlanId = Assert.IsType<Guid>(result.PaymentPlanId);
        Assert.Equal(CreatePaymentPlanResultStatus.Succeeded, result.Status);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ClientPaymentPlan paymentPlan = await dbContext.ClientPaymentPlans
            .AsNoTracking()
            .Include(candidate => candidate.Installments)
            .SingleAsync(candidate => candidate.Id == paymentPlanId);
        PaymentInstallment[] installments = paymentPlan.Installments
            .OrderBy(item => item.SequenceNumber)
            .ToArray();

        Assert.Equal(graph.OrganizationId, paymentPlan.OrganizationId);
        Assert.Equal(graph.ClientId, paymentPlan.ClientId);
        Assert.Equal(100.01m, paymentPlan.TotalAmount);
        Assert.Equal(3, paymentPlan.InstallmentCount);
        Assert.Equal(new DateOnly(2027, 1, 31), paymentPlan.FirstDueDate);
        Assert.Equal(OperationTime, paymentPlan.CreatedAt);
        Assert.Equal([1, 2, 3], installments.Select(item => item.SequenceNumber));
        Assert.Equal([33.34m, 33.34m, 33.33m],
            installments.Select(item => item.Amount));
        Assert.Equal(
            [
                new DateOnly(2027, 1, 31),
                new DateOnly(2027, 2, 28),
                new DateOnly(2027, 3, 31)
            ],
            installments.Select(item => item.DueDate));
        Assert.All(installments, installment =>
        {
            Assert.Equal(graph.OrganizationId, installment.OrganizationId);
            Assert.Equal(paymentPlanId, installment.PaymentPlanId);
            Assert.Equal(OperationTime, installment.CreatedAt);
            Assert.Null(installment.PaidAt);
        });

        AuditLog auditLog = await dbContext.AuditLogs
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(graph.OrganizationId, auditLog.OrganizationId);
        Assert.Equal(graph.UserId, auditLog.ActorUserId);
        Assert.Equal(graph.MembershipId, auditLog.ActorMembershipId);
        Assert.Equal(role, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(AuditEventType.PaymentPlanCreated, auditLog.EventType);
        Assert.Equal(AuditEntityType.ClientPaymentPlan, auditLog.EntityType);
        Assert.Equal(paymentPlanId, auditLog.EntityId);
        Assert.Equal(OperationTime, auditLog.OccurredAt);
        Assert.Null(auditLog.Details);
    }

    [Fact]
    public async Task ExecuteAsync_UnavailableClients_ReturnSameResultAndWriteNothing()
    {
        ActorGraph graph = await SeedActorGraphAsync(OrganizationRole.Owner);
        var foreignOrganization = new Organization(
            "Foreign Finance Tenant",
            $"foreign-finance-{Guid.NewGuid():N}",
            OperationTime.AddDays(-2));
        var foreignClient = new Client(
            foreignOrganization.Id,
            "Foreign Client",
            OperationTime.AddDays(-1));
        var inactiveClient = new Client(
            graph.OrganizationId,
            "Inactive Client",
            OperationTime.AddDays(-1));
        inactiveClient.Deactivate();
        await SeedAsync(foreignOrganization, foreignClient, inactiveClient);
        await using EnmaDbContext authorizationContext =
            fixture.CreateDbContext();
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            authorizationContext);

        CreatePaymentPlanResult missing = await ExecuteAsync(
            useCase,
            graph,
            Guid.NewGuid());
        CreatePaymentPlanResult foreign = await ExecuteAsync(
            useCase,
            graph,
            foreignClient.Id);
        CreatePaymentPlanResult inactive = await ExecuteAsync(
            useCase,
            graph,
            inactiveClient.Id);

        Assert.Same(CreatePaymentPlanResult.RelatedClientUnavailable, missing);
        Assert.Same(CreatePaymentPlanResult.RelatedClientUnavailable, foreign);
        Assert.Same(CreatePaymentPlanResult.RelatedClientUnavailable, inactive);
        await AssertNoFinanceWritesAsync();
    }

    [Fact]
    public async Task ExecuteAsync_RoleDemotedAfterPrecheck_DeniesAndWritesNothing()
    {
        ActorGraph graph = await SeedActorGraphAsync(OrganizationRole.Owner);
        IPaymentPlanCreationPersistence inner = CreatePersistence(
            new FixedTimeProvider(OperationTime));
        var persistence = new BeforePaymentPlanCreationPersistence(
            inner,
            async () =>
            {
                await using EnmaDbContext mutationContext =
                    fixture.CreateDbContext();
                OrganizationMembership membership = await mutationContext
                    .OrganizationMemberships
                    .SingleAsync(candidate => candidate.Id == graph.MembershipId);
                membership.ChangeRole(OrganizationRole.Member);
                await mutationContext.SaveChangesAsync();
            });
        await using EnmaDbContext authorizationContext =
            fixture.CreateDbContext();
        CreatePaymentPlanUseCase useCase = CreateUseCase(
            authorizationContext,
            persistence);

        CreatePaymentPlanResult result = await ExecuteAsync(
            useCase,
            graph,
            graph.ClientId);

        Assert.Same(CreatePaymentPlanResult.AccessDenied, result);
        await AssertNoFinanceWritesAsync();
    }

    [Fact]
    public async Task ExecuteAsync_AuditFailure_RollsBackEntireAggregate()
    {
        ActorGraph graph = await SeedActorGraphAsync(OrganizationRole.Owner);
        PaymentPlanCreationPersistence persistence = CreatePersistence(
            new FixedTimeProvider(DateTimeOffset.MinValue));
        var request = new PaymentPlanCreationPersistenceRequest(
            graph.UserId,
            graph.OrganizationId,
            graph.MembershipId,
            graph.ClientId);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            persistence.ExecuteAsync(
                request,
                state =>
                {
                    Assert.True(state.IsOrganizationActive);
                    Assert.True(state.IsClientAvailable);
                    Assert.NotNull(state.Actor);
                    return PaymentPlanCreationDecision.Persist(
                        new ClientPaymentPlan(
                            graph.OrganizationId,
                            graph.ClientId,
                            10m,
                            2,
                            new DateOnly(2027, 1, 1),
                            OperationTime));
                }));

        await AssertNoFinanceWritesAsync();
    }

    private async Task<ActorGraph> SeedActorGraphAsync(OrganizationRole role)
    {
        var organization = new Organization(
            $"Finance {role} Tenant",
            $"finance-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            OperationTime.AddDays(-2));
        var user = new User(
            $"Finance {role} Actor",
            $"finance-{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.test",
            OperationTime.AddDays(-2));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            OperationTime.AddDays(-1));
        var client = new Client(
            organization.Id,
            "Finance Client",
            OperationTime.AddDays(-1));

        await SeedAsync(organization, user, membership, client);
        return new ActorGraph(
            organization.Id,
            user.Id,
            membership.Id,
            client.Id);
    }

    private CreatePaymentPlanUseCase CreateUseCase(
        EnmaDbContext authorizationContext,
        IPaymentPlanCreationPersistence? persistence = null)
    {
        var authorization = new FinanceActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(authorizationContext)));

        return new CreatePaymentPlanUseCase(
            authorization,
            persistence ?? CreatePersistence(new FixedTimeProvider(OperationTime)),
            new FixedTimeProvider(OperationTime));
    }

    private PaymentPlanCreationPersistence CreatePersistence(
        TimeProvider timeProvider)
    {
        var options = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        return new PaymentPlanCreationPersistence(options, timeProvider);
    }

    private static Task<CreatePaymentPlanResult> ExecuteAsync(
        CreatePaymentPlanUseCase useCase,
        ActorGraph graph,
        Guid clientId)
    {
        return useCase.ExecuteAsync(
            new CreatePaymentPlanCommand(
                graph.UserId,
                graph.OrganizationId,
                clientId,
                10m,
                2,
                new DateOnly(2027, 1, 1)));
    }

    private async Task AssertNoFinanceWritesAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.ClientPaymentPlans.CountAsync());
        Assert.Equal(0, await dbContext.PaymentInstallments.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private sealed record ActorGraph(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId,
        Guid ClientId);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class BeforePaymentPlanCreationPersistence(
        IPaymentPlanCreationPersistence inner,
        Func<Task> before) : IPaymentPlanCreationPersistence
    {
        public async Task<PaymentPlanCreationPersistenceResult> ExecuteAsync(
            PaymentPlanCreationPersistenceRequest request,
            Func<PaymentPlanCreationLockedState, PaymentPlanCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            await before();
            return await inner.ExecuteAsync(request, decide, cancellationToken);
        }
    }
}
