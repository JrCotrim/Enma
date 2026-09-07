using Enma.Application.Finance;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class PaymentInstallmentMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        9,
        7,
        12,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset PaidAt =
        CreatedAt.AddHours(2);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteAsync_FirstPayment_PersistsTimestampAndAudit()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "first-payment",
            OrganizationRole.Owner);

        PaymentInstallmentMutationPersistence persistence =
            CreatePersistence();

        PaymentInstallmentMutationPersistenceResult result =
            await ExecuteMarkPaidAsync(
                persistence,
                seeded,
                seeded.PaymentPlan.Id,
                seeded.Installment.Id,
                PaidAt);

        Assert.Equal(
            PaymentInstallmentMutationPersistenceResult.Succeeded,
            result);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        PaymentInstallment persisted = await verificationContext
            .PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.Installment.Id);

        Assert.Equal(PaidAt, persisted.PaidAt);

        AuditLog auditLog = await verificationContext.AuditLogs
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(
            AuditEventType.PaymentInstallmentPaid,
            auditLog.EventType);

        Assert.Equal(seeded.Installment.Id, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedPayment_PreservesFirstTimestampAndSingleAudit()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "repeated-payment",
            OrganizationRole.Administrator);

        PaymentInstallmentMutationPersistence persistence =
            CreatePersistence();

        DateTimeOffset firstPaidAt = CreatedAt.AddHours(1);
        DateTimeOffset secondPaidAt = CreatedAt.AddHours(3);

        PaymentInstallmentMutationPersistenceResult firstResult =
            await ExecuteMarkPaidAsync(
                persistence,
                seeded,
                seeded.PaymentPlan.Id,
                seeded.Installment.Id,
                firstPaidAt);

        PaymentInstallmentMutationPersistenceResult secondResult =
            await ExecuteMarkPaidAsync(
                persistence,
                seeded,
                seeded.PaymentPlan.Id,
                seeded.Installment.Id,
                secondPaidAt);

        Assert.Equal(
            PaymentInstallmentMutationPersistenceResult.Succeeded,
            firstResult);

        Assert.Equal(
            PaymentInstallmentMutationPersistenceResult.Succeeded,
            secondResult);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        PaymentInstallment persisted = await verificationContext
            .PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.Installment.Id);

        Assert.Equal(firstPaidAt, persisted.PaidAt);

        AuditLog[] auditLogs = await verificationContext.AuditLogs
            .AsNoTracking()
            .ToArrayAsync();

        AuditLog auditLog = Assert.Single(auditLogs);

        Assert.Equal(
            AuditEventType.PaymentInstallmentPaid,
            auditLog.EventType);

        Assert.Equal(seeded.Installment.Id, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    [Fact]
    public async Task ExecuteAsync_WithForeignTenantPlan_ReturnsNotFoundWithoutMutationOrAudit()
    {
        SeededPaymentPlan tenantA = await SeedPaymentPlanAsync(
            "foreign-plan-a",
            OrganizationRole.Owner);

        SeededPaymentPlan tenantB = await SeedPaymentPlanAsync(
            "foreign-plan-b",
            OrganizationRole.Owner);

        PaymentInstallmentMutationPersistence persistence =
            CreatePersistence();

        bool decisionCalled = false;

        var request = new PaymentInstallmentMutationPersistenceRequest(
            tenantB.User.Id,
            tenantB.Organization.Id,
            tenantB.Membership.Id,
            tenantA.PaymentPlan.Id,
            tenantA.Installment.Id);

        PaymentInstallmentMutationPersistenceResult result =
            await persistence.ExecuteAsync(
                request,
                state =>
                {
                    decisionCalled = true;
                    return PaymentInstallmentMutationDecision.Persist;
                });

        Assert.Equal(
            PaymentInstallmentMutationPersistenceResult.NotFound,
            result);

        Assert.False(decisionCalled);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        PaymentInstallment persisted = await verificationContext
            .PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == tenantA.Installment.Id);

        Assert.Null(persisted.PaidAt);
        Assert.Empty(await verificationContext.AuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task ExecuteAsync_WithInstallmentOutsidePlan_ReturnsNotFound()
    {
        SeededTwoPlans seeded =
            await SeedTwoPlansInSameTenantAsync();

        PaymentInstallmentMutationPersistence persistence =
            CreatePersistence();

        bool decisionCalled = false;

        var request = new PaymentInstallmentMutationPersistenceRequest(
            seeded.User.Id,
            seeded.Organization.Id,
            seeded.Membership.Id,
            seeded.PaymentPlanA.Id,
            seeded.InstallmentB.Id);

        PaymentInstallmentMutationPersistenceResult result =
            await persistence.ExecuteAsync(
                request,
                state =>
                {
                    decisionCalled = true;
                    return PaymentInstallmentMutationDecision.Persist;
                });

        Assert.Equal(
            PaymentInstallmentMutationPersistenceResult.NotFound,
            result);

        Assert.False(decisionCalled);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        PaymentInstallment persistedA = await verificationContext
            .PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.InstallmentA.Id);

        PaymentInstallment persistedB = await verificationContext
            .PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.InstallmentB.Id);

        Assert.Null(persistedA.PaidAt);
        Assert.Null(persistedB.PaidAt);
        Assert.Empty(await verificationContext.AuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberActor_ReturnsAccessDeniedWithoutMutationOrAudit()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "member-denied",
            OrganizationRole.Member);

        PaymentInstallmentMutationPersistence persistence =
            CreatePersistence();

        var request = CreateRequest(
            seeded,
            seeded.PaymentPlan.Id,
            seeded.Installment.Id);

        PaymentInstallmentMutationPersistenceResult result =
            await persistence.ExecuteAsync(
                request,
                state =>
                {
                    Assert.True(state.IsOrganizationActive);

                    PaymentInstallmentMutationActorState actor =
                        Assert.IsType<
                            PaymentInstallmentMutationActorState>(
                            state.Actor);

                    Assert.Equal(
                        OrganizationRole.Member,
                        actor.Role);

                    Assert.True(actor.IsMembershipActive);
                    Assert.True(actor.IsUserActive);

                    return PaymentInstallmentMutationDecision.AccessDenied;
                });

        Assert.Equal(
            PaymentInstallmentMutationPersistenceResult.AccessDenied,
            result);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        PaymentInstallment persisted = await verificationContext
            .PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.Installment.Id);

        Assert.Null(persisted.PaidAt);
        Assert.Empty(await verificationContext.AuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task ExecuteAsync_CompetingPayments_PreserveFirstSerializedTimestampAndEmitExactlyOneAudit()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "concurrent-payment",
            OrganizationRole.Owner);

        DateTimeOffset firstPaidAt = CreatedAt.AddHours(1);
        DateTimeOffset secondPaidAt = CreatedAt.AddHours(3);

        Task<PaymentInstallmentMutationPersistenceResult> first =
            ExecuteMarkPaidAsync(
                CreatePersistence(),
                seeded,
                seeded.PaymentPlan.Id,
                seeded.Installment.Id,
                firstPaidAt);

        Task<PaymentInstallmentMutationPersistenceResult> second =
            ExecuteMarkPaidAsync(
                CreatePersistence(),
                seeded,
                seeded.PaymentPlan.Id,
                seeded.Installment.Id,
                secondPaidAt);

        PaymentInstallmentMutationPersistenceResult[] results =
            await Task.WhenAll(first, second)
                .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(
            results,
            result => Assert.Equal(
                PaymentInstallmentMutationPersistenceResult.Succeeded,
                result));

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        PaymentInstallment persisted = await verificationContext
            .PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == seeded.Installment.Id);

        Assert.True(
            persisted.PaidAt == firstPaidAt ||
            persisted.PaidAt == secondPaidAt);

        AuditLog[] auditLogs = await verificationContext.AuditLogs
            .AsNoTracking()
            .ToArrayAsync();

        AuditLog auditLog = Assert.Single(auditLogs);

        Assert.Equal(
            AuditEventType.PaymentInstallmentPaid,
            auditLog.EventType);

        Assert.Equal(seeded.Installment.Id, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    private PaymentInstallmentMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new PaymentInstallmentMutationPersistence(
            options,
            new FixedTimeProvider(CreatedAt.AddHours(4)));
    }

    private static PaymentInstallmentMutationPersistenceRequest CreateRequest(
        SeededPaymentPlan seeded,
        Guid paymentPlanId,
        Guid installmentId)
    {
        return new PaymentInstallmentMutationPersistenceRequest(
            seeded.User.Id,
            seeded.Organization.Id,
            seeded.Membership.Id,
            paymentPlanId,
            installmentId);
    }

    private static Task<PaymentInstallmentMutationPersistenceResult>
        ExecuteMarkPaidAsync(
            PaymentInstallmentMutationPersistence persistence,
            SeededPaymentPlan seeded,
            Guid paymentPlanId,
            Guid installmentId,
            DateTimeOffset paidAt)
    {
        PaymentInstallmentMutationPersistenceRequest request =
            CreateRequest(
                seeded,
                paymentPlanId,
                installmentId);

        return persistence.ExecuteAsync(
            request,
            state =>
            {
                if (!state.IsOrganizationActive ||
                    state.Actor is not { } actor ||
                    !actor.IsAvailableFor(
                        request.UserId,
                        request.OrganizationId,
                        request.ActorMembershipId) ||
                    actor.Role is not (
                        OrganizationRole.Owner or
                        OrganizationRole.Administrator))
                {
                    return PaymentInstallmentMutationDecision.AccessDenied;
                }

                if (state.Installment.PaidAt is null)
                {
                    state.Installment.MarkPaid(paidAt);
                }

                return PaymentInstallmentMutationDecision.Persist;
            });
    }

    private async Task<SeededPaymentPlan> SeedPaymentPlanAsync(
        string suffix,
        OrganizationRole role)
    {
        var organization = new Organization(
            $"Finance Organization {suffix}",
            $"finance-organization-{suffix}",
            CreatedAt);

        var user = new User(
            $"Finance User {suffix}",
            $"finance-{suffix}@example.test",
            CreatedAt);

        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);

        var client = new Client(
            organization.Id,
            $"Finance Client {suffix}",
            CreatedAt);

        var paymentPlan = new ClientPaymentPlan(
            organization.Id,
            client.Id,
            200m,
            2,
            new DateOnly(2026, 9, 10),
            CreatedAt);

        PaymentInstallment installment =
            paymentPlan.Installments
                .OrderBy(candidate => candidate.SequenceNumber)
                .First();

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        dbContext.AddRange(
            organization,
            user,
            membership,
            client,
            paymentPlan);

        await dbContext.SaveChangesAsync();

        return new SeededPaymentPlan(
            organization,
            user,
            membership,
            client,
            paymentPlan,
            installment);
    }

    private async Task<SeededTwoPlans>
        SeedTwoPlansInSameTenantAsync()
    {
        var organization = new Organization(
            "Finance Two Plans Organization",
            "finance-two-plans-organization",
            CreatedAt);

        var user = new User(
            "Finance Two Plans User",
            "finance-two-plans@example.test",
            CreatedAt);

        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);

        var clientA = new Client(
            organization.Id,
            "Finance Client A",
            CreatedAt);

        var clientB = new Client(
            organization.Id,
            "Finance Client B",
            CreatedAt);

        var paymentPlanA = new ClientPaymentPlan(
            organization.Id,
            clientA.Id,
            100m,
            1,
            new DateOnly(2026, 9, 10),
            CreatedAt);

        var paymentPlanB = new ClientPaymentPlan(
            organization.Id,
            clientB.Id,
            100m,
            1,
            new DateOnly(2026, 9, 11),
            CreatedAt);

        PaymentInstallment installmentA =
            Assert.Single(paymentPlanA.Installments);

        PaymentInstallment installmentB =
            Assert.Single(paymentPlanB.Installments);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        dbContext.AddRange(
            organization,
            user,
            membership,
            clientA,
            clientB,
            paymentPlanA,
            paymentPlanB);

        await dbContext.SaveChangesAsync();

        return new SeededTwoPlans(
            organization,
            user,
            membership,
            paymentPlanA,
            installmentA,
            paymentPlanB,
            installmentB);
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed record SeededPaymentPlan(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client Client,
        ClientPaymentPlan PaymentPlan,
        PaymentInstallment Installment);

    private sealed record SeededTwoPlans(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        ClientPaymentPlan PaymentPlanA,
        PaymentInstallment InstallmentA,
        ClientPaymentPlan PaymentPlanB,
        PaymentInstallment InstallmentB);
}