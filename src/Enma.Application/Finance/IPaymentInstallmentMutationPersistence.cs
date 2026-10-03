using Enma.Domain.Finance;
using Enma.Domain.Organizations;

namespace Enma.Application.Finance;

public interface IPaymentInstallmentMutationPersistence
{
    Task<PaymentInstallmentMutationPersistenceResult> ExecuteAsync(
        PaymentInstallmentMutationPersistenceRequest request,
        Func<
            PaymentInstallmentMutationLockedState,
            PaymentInstallmentMutationDecision> decide,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the payment plan, the installment (scoped to that plan), the actor
    /// membership, its user and the organization, in that order, and runs the
    /// decision against the locked state. A paid-to-unpaid transition appends
    /// the reversal audit with the reason in the same transaction.
    /// </summary>
    Task<PaymentInstallmentMutationPersistenceResult> ReversePaymentAsync(
        PaymentInstallmentMutationPersistenceRequest request,
        PaymentReversalReason reason,
        Func<
            PaymentInstallmentMutationLockedState,
            PaymentInstallmentMutationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record PaymentInstallmentMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid PaymentPlanId,
    Guid InstallmentId);

public sealed record PaymentInstallmentMutationLockedState(
    PaymentInstallment Installment,
    bool IsOrganizationActive,
    PaymentInstallmentMutationActorState? Actor);

public sealed record PaymentInstallmentMutationActorState(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive)
{
    public bool IsAvailableFor(
        Guid userId,
        Guid organizationId,
        Guid membershipId)
    {
        return MembershipId == membershipId &&
            OrganizationId == organizationId &&
            UserId == userId &&
            IsMembershipActive &&
            IsUserActive &&
            Enum.IsDefined(Role);
    }
}

public sealed class PaymentInstallmentMutationDecision
{
    private PaymentInstallmentMutationDecision(
        PaymentInstallmentMutationDecisionStatus status)
    {
        Status = status;
    }

    public PaymentInstallmentMutationDecisionStatus Status { get; }

    public static PaymentInstallmentMutationDecision AccessDenied { get; } =
        new(PaymentInstallmentMutationDecisionStatus.AccessDenied);

    public static PaymentInstallmentMutationDecision Persist { get; } =
        new(PaymentInstallmentMutationDecisionStatus.Persist);
}

public enum PaymentInstallmentMutationDecisionStatus
{
    AccessDenied = 0,
    Persist = 1
}

public enum PaymentInstallmentMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}