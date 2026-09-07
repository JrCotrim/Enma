using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Finance;
using Enma.Domain.Auditing;
using Enma.Domain.Finance;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class PaymentInstallmentMutationPersistence
    : IPaymentInstallmentMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public PaymentInstallmentMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public async Task<PaymentInstallmentMutationPersistenceResult> ExecuteAsync(
        PaymentInstallmentMutationPersistenceRequest request,
        Func<
            PaymentInstallmentMutationLockedState,
            PaymentInstallmentMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty)
        {
            return PaymentInstallmentMutationPersistenceResult.AccessDenied;
        }

        if (request.PaymentPlanId == Guid.Empty ||
            request.InstallmentId == Guid.Empty)
        {
            return PaymentInstallmentMutationPersistenceResult.NotFound;
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        ClientPaymentPlan? paymentPlan = await LockPaymentPlanAsync(
            dbContext,
            request.OrganizationId,
            request.PaymentPlanId,
            cancellationToken);

        if (paymentPlan is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PaymentInstallmentMutationPersistenceResult.NotFound;
        }

        PaymentInstallment? installment = await LockInstallmentAsync(
            dbContext,
            request.OrganizationId,
            request.PaymentPlanId,
            request.InstallmentId,
            cancellationToken);

        if (installment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PaymentInstallmentMutationPersistenceResult.NotFound;
        }

        DateTimeOffset? oldPaidAt = installment.PaidAt;

        OrganizationMembership? actorMembership =
            await LockActorMembershipAsync(
                dbContext,
                request.OrganizationId,
                request.ActorMembershipId,
                cancellationToken);

        User? actorUser = actorMembership is null
            ? null
            : await LockActorUserAsync(
                dbContext,
                actorMembership.UserId,
                cancellationToken);

        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);

        PaymentInstallmentMutationDecision decision = decide(
            new PaymentInstallmentMutationLockedState(
                installment,
                organization?.IsActive == true,
                CreateActorState(actorMembership, actorUser)));

        if (decision.Status ==
            PaymentInstallmentMutationDecisionStatus.AccessDenied)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PaymentInstallmentMutationPersistenceResult.AccessDenied;
        }

        if (decision.Status !=
            PaymentInstallmentMutationDecisionStatus.Persist)
        {
            throw new InvalidOperationException(
                "Payment installment mutation returned an invalid decision.");
        }

        if (installment.OrganizationId != request.OrganizationId ||
            installment.PaymentPlanId != request.PaymentPlanId ||
            installment.Id != request.InstallmentId)
        {
            throw new InvalidOperationException(
                "Payment installment mutation produced invalid state.");
        }

        if (oldPaidAt is null && installment.PaidAt is null)
        {
            throw new InvalidOperationException(
                "Payment installment mark-paid decision did not mark the installment.");
        }

        if (oldPaidAt is not null && installment.PaidAt != oldPaidAt)
        {
            throw new InvalidOperationException(
                "An already-paid installment changed its payment timestamp.");
        }

        bool paymentTransitioned =
            oldPaidAt is null &&
            installment.PaidAt is not null;

        if (!paymentTransitioned)
        {
            await transaction.CommitAsync(cancellationToken);
            return PaymentInstallmentMutationPersistenceResult.Succeeded;
        }

        if (actorMembership is null)
        {
            throw new InvalidOperationException(
                "Payment installment mutation accepted a missing actor.");
        }

        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(
                actorMembership);

        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(
                AuditEventType.PaymentInstallmentPaid,
                installment.Id));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return PaymentInstallmentMutationPersistenceResult.Succeeded;
    }

    private static PaymentInstallmentMutationActorState? CreateActorState(
        OrganizationMembership? membership,
        User? user)
    {
        return membership is null
            ? null
            : new PaymentInstallmentMutationActorState(
                membership.Id,
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                user?.Id == membership.UserId && user.IsActive);
    }

    private static Task<ClientPaymentPlan?> LockPaymentPlanAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid paymentPlanId,
        CancellationToken cancellationToken)
    {
        return dbContext.ClientPaymentPlans
            .FromSqlInterpolated(
                $"""
                SELECT * FROM client_payment_plans
                WHERE id = {paymentPlanId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<PaymentInstallment?> LockInstallmentAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid paymentPlanId,
        Guid installmentId,
        CancellationToken cancellationToken)
    {
        return dbContext.PaymentInstallments
            .FromSqlInterpolated(
                $"""
                SELECT * FROM payment_installments
                WHERE id = {installmentId}
                  AND organization_id = {organizationId}
                  AND payment_plan_id = {paymentPlanId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<OrganizationMembership?> LockActorMembershipAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {organizationId}
                  AND id = {actorMembershipId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<User?> LockActorUserAsync(
        EnmaDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return dbContext.Users
            .FromSqlInterpolated(
                $"""
                SELECT * FROM users
                WHERE id = {userId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<Organization?> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return dbContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }
}