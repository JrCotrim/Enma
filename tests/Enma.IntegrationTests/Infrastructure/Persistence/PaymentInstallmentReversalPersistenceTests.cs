using Enma.Application.Authorization;
using Enma.Application.Finance;
using Enma.Application.Finance.MarkPaid;
using Enma.Application.Finance.ReversePayment;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class PaymentInstallmentReversalPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        1,
        12,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset PaidAt =
        CreatedAt.AddHours(2);

    private readonly AdvancingTimeProvider timeProvider =
        new(CreatedAt.AddHours(4));

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task Reverse_PaidInstallment_ClearsPaymentAndAppendsSingleReasonAudit(
        OrganizationRole role)
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            $"reverse-{role}",
            role,
            paidSequenceNumbers: [1, 2]);

        ReverseInstallmentPaymentResult result = await ReverseAsync(
            seeded,
            seeded.Installment.Id,
            "wrongInstallment");

        Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, result);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        Assert.Null((await GetInstallmentAsync(
            verificationContext,
            seeded.Installment.Id)).PaidAt);
        Assert.Equal(PaidAt, (await GetInstallmentAsync(
            verificationContext,
            seeded.SiblingInstallment.Id)).PaidAt);

        AuditLog auditLog = Assert.Single(
            await verificationContext.AuditLogs.AsNoTracking().ToArrayAsync());

        Assert.Equal(
            AuditEventType.PaymentInstallmentPaymentReversed,
            auditLog.EventType);
        Assert.Equal(AuditEntityType.PaymentInstallment, auditLog.EntityType);
        Assert.Equal(seeded.Installment.Id, auditLog.EntityId);
        Assert.Equal(seeded.Organization.Id, auditLog.OrganizationId);
        Assert.Equal(seeded.User.Id, auditLog.ActorUserId);
        Assert.Equal(seeded.Membership.Id, auditLog.ActorMembershipId);
        Assert.Equal(role, auditLog.ActorRoleAtOccurrence);
        Assert.True(auditLog.OccurredAt > CreatedAt.AddHours(4));

        PaymentInstallmentPaymentReversedAuditDetails details =
            Assert.IsType<PaymentInstallmentPaymentReversedAuditDetails>(
                auditLog.Details);
        Assert.Equal(PaymentReversalReason.WrongInstallment, details.Reason);
        Assert.Equal(
            "{\"reason\": 2}",
            await ReadRawAuditDetailsAsync(auditLog.Id));
    }

    [Fact]
    public async Task Reverse_RepeatedRequest_DoesNotEmitSecondAudit()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-repeated",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);

        ReverseInstallmentPaymentResult first = await ReverseAsync(
            seeded,
            seeded.Installment.Id,
            "registeredByMistake");
        ReverseInstallmentPaymentResult second = await ReverseAsync(
            seeded,
            seeded.Installment.Id,
            "other");

        Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, first);
        Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, second);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        Assert.Null((await GetInstallmentAsync(
            verificationContext,
            seeded.Installment.Id)).PaidAt);

        AuditLog auditLog = Assert.Single(
            await verificationContext.AuditLogs.AsNoTracking().ToArrayAsync());
        Assert.Equal(
            PaymentReversalReason.RegisteredByMistake,
            Assert.IsType<PaymentInstallmentPaymentReversedAuditDetails>(
                auditLog.Details).Reason);
    }

    [Fact]
    public async Task Reverse_UnpaidInstallment_IsNoOpWithoutAudit()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-unpaid",
            OrganizationRole.Administrator,
            paidSequenceNumbers: []);

        ReverseInstallmentPaymentResult result = await ReverseAsync(
            seeded,
            seeded.Installment.Id,
            "paymentNotCompleted");

        Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, result);

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        Assert.Null((await GetInstallmentAsync(
            verificationContext,
            seeded.Installment.Id)).PaidAt);
        Assert.Empty(await verificationContext.AuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task Reverse_MarkPaidAgainAfterReversal_RecordsNewPaymentAndBothAudits()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-then-pay",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);

        Assert.Equal(
            ReverseInstallmentPaymentResult.Succeeded,
            await ReverseAsync(seeded, seeded.Installment.Id, "other"));
        Assert.Equal(
            MarkPaymentInstallmentPaidResult.Succeeded,
            await MarkPaidAsync(seeded, seeded.Installment.Id));

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        PaymentInstallment persisted = await GetInstallmentAsync(
            verificationContext,
            seeded.Installment.Id);
        Assert.NotNull(persisted.PaidAt);
        Assert.True(persisted.PaidAt > PaidAt);

        Assert.Equal(
            new[]
            {
                AuditEventType.PaymentInstallmentPaymentReversed,
                AuditEventType.PaymentInstallmentPaid
            },
            await GetAuditEventTypesAsync(seeded.Installment.Id));
    }

    [Fact]
    public async Task ReversePaymentAsync_ForeignTenantPlan_ReturnsNotFoundWithoutMutationOrAudit()
    {
        SeededPaymentPlan tenantA = await SeedPaymentPlanAsync(
            "reverse-foreign-a",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);
        SeededPaymentPlan tenantB = await SeedPaymentPlanAsync(
            "reverse-foreign-b",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);

        bool decisionCalled = false;
        PaymentInstallmentMutationPersistenceResult result =
            await CreatePersistence().ReversePaymentAsync(
                new PaymentInstallmentMutationPersistenceRequest(
                    tenantB.User.Id,
                    tenantB.Organization.Id,
                    tenantB.Membership.Id,
                    tenantA.PaymentPlan.Id,
                    tenantA.Installment.Id),
                PaymentReversalReason.Other,
                state =>
                {
                    decisionCalled = true;
                    state.Installment.ReversePayment();
                    return PaymentInstallmentMutationDecision.Persist;
                });

        Assert.Equal(PaymentInstallmentMutationPersistenceResult.NotFound, result);
        Assert.False(decisionCalled);
        await AssertPaidWithoutAuditAsync(tenantA.Installment.Id);
    }

    [Fact]
    public async Task ReversePaymentAsync_ForeignInstallmentUnderOwnPlan_ReturnsNotFound()
    {
        SeededPaymentPlan tenantA = await SeedPaymentPlanAsync(
            "reverse-foreign-installment-a",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);
        SeededPaymentPlan tenantB = await SeedPaymentPlanAsync(
            "reverse-foreign-installment-b",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);

        bool decisionCalled = false;
        PaymentInstallmentMutationPersistenceResult result =
            await CreatePersistence().ReversePaymentAsync(
                new PaymentInstallmentMutationPersistenceRequest(
                    tenantB.User.Id,
                    tenantB.Organization.Id,
                    tenantB.Membership.Id,
                    tenantB.PaymentPlan.Id,
                    tenantA.Installment.Id),
                PaymentReversalReason.Other,
                state =>
                {
                    decisionCalled = true;
                    state.Installment.ReversePayment();
                    return PaymentInstallmentMutationDecision.Persist;
                });

        Assert.Equal(PaymentInstallmentMutationPersistenceResult.NotFound, result);
        Assert.False(decisionCalled);
        await AssertPaidWithoutAuditAsync(tenantA.Installment.Id);
        await AssertPaidWithoutAuditAsync(tenantB.Installment.Id);
    }

    [Fact]
    public async Task ReversePaymentAsync_InstallmentOutsidePlan_ReturnsNotFound()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-outside-plan",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);
        ClientPaymentPlan otherPlan = await SeedAdditionalPaidPlanAsync(seeded);
        PaymentInstallment otherInstallment = Assert.Single(otherPlan.Installments);

        bool decisionCalled = false;
        PaymentInstallmentMutationPersistenceResult result =
            await CreatePersistence().ReversePaymentAsync(
                CreateRequest(seeded, seeded.PaymentPlan.Id, otherInstallment.Id),
                PaymentReversalReason.Other,
                state =>
                {
                    decisionCalled = true;
                    state.Installment.ReversePayment();
                    return PaymentInstallmentMutationDecision.Persist;
                });

        Assert.Equal(PaymentInstallmentMutationPersistenceResult.NotFound, result);
        Assert.False(decisionCalled);
        await AssertPaidWithoutAuditAsync(otherInstallment.Id);
        await AssertPaidWithoutAuditAsync(seeded.Installment.Id);
    }

    [Fact]
    public async Task Reverse_MemberActor_IsDeniedBeforePersistence()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-member",
            OrganizationRole.Member,
            paidSequenceNumbers: [1]);
        var persistence = new InterceptingPersistence(CreatePersistence());

        ReverseInstallmentPaymentResult result = await ReverseAsync(
            seeded,
            seeded.Installment.Id,
            "other",
            persistence);

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
        await AssertPaidWithoutAuditAsync(seeded.Installment.Id);
    }

    [Theory]
    [InlineData(ConcurrentActorChange.MembershipDeactivated)]
    [InlineData(ConcurrentActorChange.RoleDemotedToMember)]
    [InlineData(ConcurrentActorChange.UserDeactivated)]
    [InlineData(ConcurrentActorChange.OrganizationDeactivated)]
    public async Task Reverse_ActorChangedAfterPrecheck_IsDeniedAgainstLockedState(
        ConcurrentActorChange change)
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            $"reverse-race-{change}",
            OrganizationRole.Administrator,
            paidSequenceNumbers: [1]);
        var persistence = new InterceptingPersistence(
            CreatePersistence(),
            () => ApplyConcurrentChangeAsync(seeded, change));

        ReverseInstallmentPaymentResult result = await ReverseAsync(
            seeded,
            seeded.Installment.Id,
            "other",
            persistence);

        Assert.Equal(ReverseInstallmentPaymentResult.AccessDenied, result);
        Assert.Equal(1, persistence.CallCount);
        await AssertPaidWithoutAuditAsync(seeded.Installment.Id);
    }

    [Fact]
    public async Task ReversePaymentAsync_DecisionThatKeepsPayment_FailsClosedWithoutWrites()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-guard-kept",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePersistence().ReversePaymentAsync(
                CreateRequest(seeded, seeded.PaymentPlan.Id, seeded.Installment.Id),
                PaymentReversalReason.Other,
                _ => PaymentInstallmentMutationDecision.Persist));

        await AssertPaidWithoutAuditAsync(seeded.Installment.Id);
    }

    [Fact]
    public async Task ReversePaymentAsync_DecisionThatMarksPayment_FailsClosedWithoutWrites()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-guard-marked",
            OrganizationRole.Owner,
            paidSequenceNumbers: []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePersistence().ReversePaymentAsync(
                CreateRequest(seeded, seeded.PaymentPlan.Id, seeded.Installment.Id),
                PaymentReversalReason.Other,
                state =>
                {
                    state.Installment.MarkPaid(PaidAt);
                    return PaymentInstallmentMutationDecision.Persist;
                }));

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();
        Assert.Null((await GetInstallmentAsync(
            verificationContext,
            seeded.Installment.Id)).PaidAt);
        Assert.Empty(await verificationContext.AuditLogs.ToArrayAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task ReversePaymentAsync_UndefinedReason_RejectsBeforeDecision(
        int reason)
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            $"reverse-invalid-reason-{reason}",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);

        bool decisionCalled = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreatePersistence().ReversePaymentAsync(
                CreateRequest(seeded, seeded.PaymentPlan.Id, seeded.Installment.Id),
                (PaymentReversalReason)reason,
                state =>
                {
                    decisionCalled = true;
                    return PaymentInstallmentMutationDecision.Persist;
                }));

        Assert.False(decisionCalled);
        await AssertPaidWithoutAuditAsync(seeded.Installment.Id);
    }

    [Fact]
    public async Task Reverse_CompetingReversals_ClearOnceAndEmitExactlyOneAudit()
    {
        SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
            "reverse-concurrent",
            OrganizationRole.Owner,
            paidSequenceNumbers: [1]);

        ReverseInstallmentPaymentResult[] results = await Task.WhenAll(
                ReverseAsync(seeded, seeded.Installment.Id, "registeredByMistake"),
                ReverseAsync(seeded, seeded.Installment.Id, "wrongInstallment"))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(
            results,
            result => Assert.Equal(
                ReverseInstallmentPaymentResult.Succeeded,
                result));

        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();
        Assert.Null((await GetInstallmentAsync(
            verificationContext,
            seeded.Installment.Id)).PaidAt);
        Assert.Equal(
            new[] { AuditEventType.PaymentInstallmentPaymentReversed },
            await GetAuditEventTypesAsync(seeded.Installment.Id));
    }

    [Fact]
    public async Task PayAndReverse_CompetingFromUnpaid_ReachConsistentStateWithProportionalAudit()
    {
        for (int iteration = 0; iteration < 6; iteration++)
        {
            SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
                $"pay-reverse-unpaid-{iteration}",
                OrganizationRole.Owner,
                paidSequenceNumbers: []);

            Task<MarkPaymentInstallmentPaidResult> pay =
                MarkPaidAsync(seeded, seeded.Installment.Id);
            Task<ReverseInstallmentPaymentResult> reverse =
                ReverseAsync(seeded, seeded.Installment.Id, "other");

            await Task.WhenAll(pay, reverse).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(MarkPaymentInstallmentPaidResult.Succeeded, await pay);
            Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, await reverse);

            await using EnmaDbContext verificationContext =
                fixture.CreateDbContext();
            PaymentInstallment persisted = await GetInstallmentAsync(
                verificationContext,
                seeded.Installment.Id);
            AuditEventType[] auditEvents =
                await GetAuditEventTypesAsync(seeded.Installment.Id);

            AuditEventType[] expectedEvents = persisted.PaidAt is null
                ? new[]
                {
                    AuditEventType.PaymentInstallmentPaid,
                    AuditEventType.PaymentInstallmentPaymentReversed
                }
                : new[] { AuditEventType.PaymentInstallmentPaid };

            Assert.Equal(expectedEvents, auditEvents);
        }
    }

    [Fact]
    public async Task PayAndReverse_CompetingFromPaid_ReachConsistentStateWithProportionalAudit()
    {
        for (int iteration = 0; iteration < 6; iteration++)
        {
            SeededPaymentPlan seeded = await SeedPaymentPlanAsync(
                $"pay-reverse-paid-{iteration}",
                OrganizationRole.Administrator,
                paidSequenceNumbers: [1]);

            Task<MarkPaymentInstallmentPaidResult> pay =
                MarkPaidAsync(seeded, seeded.Installment.Id);
            Task<ReverseInstallmentPaymentResult> reverse =
                ReverseAsync(seeded, seeded.Installment.Id, "other");

            await Task.WhenAll(pay, reverse).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(MarkPaymentInstallmentPaidResult.Succeeded, await pay);
            Assert.Equal(ReverseInstallmentPaymentResult.Succeeded, await reverse);

            await using EnmaDbContext verificationContext =
                fixture.CreateDbContext();
            PaymentInstallment persisted = await GetInstallmentAsync(
                verificationContext,
                seeded.Installment.Id);
            AuditEventType[] auditEvents =
                await GetAuditEventTypesAsync(seeded.Installment.Id);

            AuditEventType[] expectedEvents = persisted.PaidAt is null
                ? new[] { AuditEventType.PaymentInstallmentPaymentReversed }
                : new[]
                {
                    AuditEventType.PaymentInstallmentPaymentReversed,
                    AuditEventType.PaymentInstallmentPaid
                };

            Assert.Equal(expectedEvents, auditEvents);
        }
    }

    private async Task<ReverseInstallmentPaymentResult> ReverseAsync(
        SeededPaymentPlan seeded,
        Guid installmentId,
        string reason,
        IPaymentInstallmentMutationPersistence? persistence = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new ReverseInstallmentPaymentUseCase(
            CreateAuthorization(dbContext),
            persistence ?? CreatePersistence());

        return await useCase.ExecuteAsync(
            new ReverseInstallmentPaymentCommand(
                seeded.User.Id,
                seeded.Organization.Id,
                seeded.PaymentPlan.Id,
                installmentId,
                reason));
    }

    private async Task<MarkPaymentInstallmentPaidResult> MarkPaidAsync(
        SeededPaymentPlan seeded,
        Guid installmentId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new MarkPaymentInstallmentPaidUseCase(
            CreateAuthorization(dbContext),
            CreatePersistence(),
            timeProvider);

        return await useCase.ExecuteAsync(
            new MarkPaymentInstallmentPaidCommand(
                seeded.User.Id,
                seeded.Organization.Id,
                seeded.PaymentPlan.Id,
                installmentId));
    }

    private async Task ApplyConcurrentChangeAsync(
        SeededPaymentPlan seeded,
        ConcurrentActorChange change)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        switch (change)
        {
            case ConcurrentActorChange.MembershipDeactivated:
                (await dbContext.OrganizationMemberships.SingleAsync(
                    membership => membership.Id == seeded.Membership.Id))
                    .Deactivate();
                break;
            case ConcurrentActorChange.RoleDemotedToMember:
                (await dbContext.OrganizationMemberships.SingleAsync(
                    membership => membership.Id == seeded.Membership.Id))
                    .ChangeRole(OrganizationRole.Member);
                break;
            case ConcurrentActorChange.UserDeactivated:
                (await dbContext.Users.SingleAsync(
                    user => user.Id == seeded.User.Id))
                    .Deactivate();
                break;
            case ConcurrentActorChange.OrganizationDeactivated:
                (await dbContext.Organizations.SingleAsync(
                    organization => organization.Id == seeded.Organization.Id))
                    .Deactivate();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task AssertPaidWithoutAuditAsync(Guid installmentId)
    {
        await using EnmaDbContext verificationContext =
            fixture.CreateDbContext();

        Assert.Equal(PaidAt, (await GetInstallmentAsync(
            verificationContext,
            installmentId)).PaidAt);
        Assert.Empty(await verificationContext.AuditLogs.ToArrayAsync());
    }

    private static Task<PaymentInstallment> GetInstallmentAsync(
        EnmaDbContext dbContext,
        Guid installmentId)
    {
        return dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == installmentId);
    }

    private async Task<AuditEventType[]> GetAuditEventTypesAsync(
        Guid installmentId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        return (await dbContext.AuditLogs
                .AsNoTracking()
                .Where(auditLog => auditLog.EntityId == installmentId)
                .ToArrayAsync())
            .OrderBy(auditLog => auditLog.OccurredAt)
            .Select(auditLog => auditLog.EventType)
            .ToArray();
    }

    private async Task<string?> ReadRawAuditDetailsAsync(Guid auditLogId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT details::text FROM audit_logs WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", auditLogId);

        return (string?)await command.ExecuteScalarAsync();
    }

    private static FinanceActionAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new FinanceActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
    }

    private PaymentInstallmentMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new PaymentInstallmentMutationPersistence(
            options,
            timeProvider);
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

    private async Task<SeededPaymentPlan> SeedPaymentPlanAsync(
        string suffix,
        OrganizationRole role,
        int[] paidSequenceNumbers)
    {
        var organization = new Organization(
            $"Reversal Organization {suffix}",
            $"reversal-organization-{suffix.ToLowerInvariant()}",
            CreatedAt);

        var user = new User(
            $"Reversal User {suffix}",
            $"reversal-{suffix.ToLowerInvariant()}@example.test",
            CreatedAt);

        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);

        var client = new Client(
            organization.Id,
            $"Reversal Client {suffix}",
            CreatedAt);

        var paymentPlan = new ClientPaymentPlan(
            organization.Id,
            client.Id,
            200m,
            2,
            new DateOnly(2026, 10, 10),
            CreatedAt);

        PaymentInstallment[] installments = paymentPlan.Installments
            .OrderBy(candidate => candidate.SequenceNumber)
            .ToArray();

        foreach (PaymentInstallment installment in installments.Where(
            candidate => paidSequenceNumbers.Contains(candidate.SequenceNumber)))
        {
            installment.MarkPaid(PaidAt);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
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
            installments[0],
            installments[1]);
    }

    private async Task<ClientPaymentPlan> SeedAdditionalPaidPlanAsync(
        SeededPaymentPlan seeded)
    {
        var paymentPlan = new ClientPaymentPlan(
            seeded.Organization.Id,
            seeded.Client.Id,
            100m,
            1,
            new DateOnly(2026, 10, 11),
            CreatedAt);
        Assert.Single(paymentPlan.Installments).MarkPaid(PaidAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Add(paymentPlan);
        await dbContext.SaveChangesAsync();

        return paymentPlan;
    }

    public enum ConcurrentActorChange
    {
        MembershipDeactivated = 0,
        RoleDemotedToMember = 1,
        UserDeactivated = 2,
        OrganizationDeactivated = 3
    }

    private sealed class InterceptingPersistence(
        IPaymentInstallmentMutationPersistence inner,
        Func<Task>? beforeReverse = null) : IPaymentInstallmentMutationPersistence
    {
        public int CallCount { get; private set; }

        public Task<PaymentInstallmentMutationPersistenceResult> ExecuteAsync(
            PaymentInstallmentMutationPersistenceRequest request,
            Func<
                PaymentInstallmentMutationLockedState,
                PaymentInstallmentMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Payment reversal must never mark an installment as paid.");
        }

        public async Task<PaymentInstallmentMutationPersistenceResult> ReversePaymentAsync(
            PaymentInstallmentMutationPersistenceRequest request,
            PaymentReversalReason reason,
            Func<
                PaymentInstallmentMutationLockedState,
                PaymentInstallmentMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            if (beforeReverse is not null)
            {
                await beforeReverse();
            }

            return await inner.ReversePaymentAsync(
                request,
                reason,
                decide,
                cancellationToken);
        }
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow()
        {
            return start.AddSeconds(Interlocked.Increment(ref _ticks));
        }
    }

    private sealed record SeededPaymentPlan(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client Client,
        ClientPaymentPlan PaymentPlan,
        PaymentInstallment Installment,
        PaymentInstallment SiblingInstallment);
}
