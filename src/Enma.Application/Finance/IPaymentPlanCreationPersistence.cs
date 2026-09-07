using Enma.Domain.Finance;
using Enma.Domain.Organizations;

namespace Enma.Application.Finance;

public interface IPaymentPlanCreationPersistence
{
    Task<PaymentPlanCreationPersistenceResult> ExecuteAsync(
        PaymentPlanCreationPersistenceRequest request,
        Func<PaymentPlanCreationLockedState, PaymentPlanCreationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record PaymentPlanCreationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid ClientId);

public sealed record PaymentPlanCreationLockedState(
    bool IsOrganizationActive,
    PaymentPlanCreationActorState? Actor,
    bool IsClientAvailable);

public sealed record PaymentPlanCreationActorState(
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

public sealed class PaymentPlanCreationDecision
{
    private PaymentPlanCreationDecision(
        PaymentPlanCreationDecisionStatus status,
        ClientPaymentPlan? paymentPlan)
    {
        Status = status;
        PaymentPlan = paymentPlan;
    }

    public PaymentPlanCreationDecisionStatus Status { get; }

    public ClientPaymentPlan? PaymentPlan { get; }

    public static PaymentPlanCreationDecision AccessDenied { get; } = new(
        PaymentPlanCreationDecisionStatus.AccessDenied,
        null);

    public static PaymentPlanCreationDecision RelatedClientUnavailable { get; } =
        new(
            PaymentPlanCreationDecisionStatus.RelatedClientUnavailable,
            null);

    public static PaymentPlanCreationDecision Persist(
        ClientPaymentPlan paymentPlan)
    {
        ArgumentNullException.ThrowIfNull(paymentPlan);

        return new PaymentPlanCreationDecision(
            PaymentPlanCreationDecisionStatus.Persist,
            paymentPlan);
    }
}

public enum PaymentPlanCreationDecisionStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    Persist = 2
}

public sealed class PaymentPlanCreationPersistenceResult
{
    private PaymentPlanCreationPersistenceResult(
        PaymentPlanCreationDecisionStatus status,
        Guid? paymentPlanId)
    {
        Status = status;
        PaymentPlanId = paymentPlanId;
    }

    public PaymentPlanCreationDecisionStatus Status { get; }

    public Guid? PaymentPlanId { get; }

    public static PaymentPlanCreationPersistenceResult Rejected(
        PaymentPlanCreationDecisionStatus status)
    {
        if (status == PaymentPlanCreationDecisionStatus.Persist ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new PaymentPlanCreationPersistenceResult(status, null);
    }

    public static PaymentPlanCreationPersistenceResult Created(
        Guid paymentPlanId)
    {
        if (paymentPlanId == Guid.Empty)
        {
            throw new ArgumentException(
                "Payment plan id cannot be empty.",
                nameof(paymentPlanId));
        }

        return new PaymentPlanCreationPersistenceResult(
            PaymentPlanCreationDecisionStatus.Persist,
            paymentPlanId);
    }
}
